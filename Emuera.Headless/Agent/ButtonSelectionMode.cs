using System;
using System.Collections.Generic;
using MinorShift.Emuera.Runtime;
using MinorShift.Emuera.UI.Game;

namespace MinorShift.Emuera.GameView
{
    /// <summary>
    /// 按钮选择模式：管理方向键导航、高亮、确认，以及 VT 鼠标区域同步。
    /// ADR-0005：VT-only 后原 <c>_ansiEnabled</c> 字段已删除，ANSI 路径直接走。
    /// ADR-0005 Issue 4：删除 <c>_getScreen() != null</c> / <c>vtInput == null</c>
    /// 降级分支与 <c>_requestFullRefresh</c> 字段，调用方保证 VT 主循环内必非 null。
    /// 从 AgentCliProtocol 拆分以隔离按钮相关状态与逻辑。
    /// </summary>
    internal sealed class ButtonSelectionMode
    {
        private readonly EmueraConsole _console;
        private readonly ScrollController _scroll;
        private readonly Func<AgentCliVtScreen?> _getScreen;
        private readonly Action<string> _dispatchInput;
        private readonly Action _clearInputBuffer;

        private bool _buttonMode;
        private List<ButtonPos> _buttonPositions = [];
        private int _selectedButtonIndex = -1;
        private long _lastButtonSyncGen = -1;
        private long _lastRegionGeneration = -1;

        public ButtonSelectionMode(
            EmueraConsole console,
            ScrollController scroll,
            Func<AgentCliVtScreen?> getScreen,
            Action<string> dispatchInput,
            Action clearInputBuffer)
        {
            _console = console;
            _scroll = scroll;
            _getScreen = getScreen;
            _dispatchInput = dispatchInput;
            _clearInputBuffer = clearInputBuffer;
        }

        public bool IsButtonMode => _buttonMode;
        public bool HasButtons => _buttonPositions.Count > 0;

        /// <summary>
        /// 处理按键。返回 true 表示已消费（按钮模式），false 表示未消费需常规处理。
        /// </summary>
        internal bool HandleKey(ConsoleKeyInfo key)
        {
            bool isArrow = key.Key == ConsoleKey.UpArrow
                || key.Key == ConsoleKey.DownArrow
                || key.Key == ConsoleKey.LeftArrow
                || key.Key == ConsoleKey.RightArrow;

            if (_buttonMode)
            {
                if (key.Key == ConsoleKey.Enter || isArrow)
                {
                    ProcessButtonModeKey(key);
                    return true;
                }
                ExitButtonMode();
                return false;
            }

            if (isArrow && _buttonPositions.Count > 0)
            {
                _buttonMode = true;
                _selectedButtonIndex = 0;
                _console.SetSelectingButton(_buttonPositions[0].Button);
                _clearInputBuffer();
                RedrawButtonLine(_buttonPositions[0].Row);
                ProcessButtonModeKey(key);
                return true;
            }

            return false;
        }

        /// <summary>鼠标点击时退出按钮模式（若有）。</summary>
        internal void ExitButtonMode() => ExitButtonModeCore();

        /// <summary>同步按钮位置状态，按需进入/退出选择模式或更新高亮。
        /// ADR-0006：Scroll Mode（offset>0）下跳过——按钮命中区由 RefreshButtonRegions 清空，
        /// 不维护选择模式状态（避免误触旧 Generation 按钮）。</summary>
        internal void SyncButtonState()
        {
            if (_scroll.ScrollOffset > 0) return;

            bool shouldHaveButtons = false;
            List<ButtonPos> newPositions = [];

            if (_console.State == ConsoleState.WaitInput)
            {
                var req = _console.CurrentRequest;
                if (req != null && req.InputType != InputType.EnterKey && req.InputType != InputType.AnyKey)
                {
                    long currentGen = _console.LastButtonGeneration;
                    if (_buttonMode && currentGen == _lastButtonSyncGen)
                    {
                        return;
                    }

                    newPositions = RebuildButtonPositions();
                    _lastButtonSyncGen = currentGen;
                    if (newPositions.Count > 0)
                        shouldHaveButtons = true;
                }
            }
            else
            {
                _lastButtonSyncGen = -1;
            }

            if (!shouldHaveButtons)
            {
                if (_buttonMode) ExitButtonModeCore();
                _buttonPositions = [];
            }
            else
            {
                // 按钮存在时仅维护位置列表，不自动进入选择模式；
                // 选择模式由玩家按方向键显式触发。
                if (_buttonMode)
                {
                    if (!ButtonInputKeysEqual(_buttonPositions, newPositions))
                    {
                        int oldRow = _selectedButtonIndex >= 0 && _selectedButtonIndex < _buttonPositions.Count
                            ? _buttonPositions[_selectedButtonIndex].Row : -1;
                        _buttonPositions = newPositions;
                        _selectedButtonIndex = Math.Clamp(_selectedButtonIndex, 0, newPositions.Count - 1);
                        _console.SetSelectingButton(newPositions[_selectedButtonIndex].Button);
                        if (oldRow >= 0) RedrawButtonLine(oldRow);
                        int newRow = newPositions[_selectedButtonIndex].Row;
                        if (newRow != oldRow)
                            RedrawButtonLine(newRow);
                    }
                }
                else
                {
                    _buttonPositions = newPositions;
                }
            }
        }

        /// <summary>刷新 VT 鼠标命中区域。ADR-0005 Issue 4：VT-only 后 vtInput 必非 null。
        /// ADR-0006：Scroll Mode（offset>0）下走 ClearRegions 分支，不 SyncButtonState——
        /// 避免点击触发旧 Generation 按钮分发导致 input rejection。</summary>
        internal void RefreshButtonRegions(VtInputHandler vtInput, bool force = false)
        {
            // ADR-0006：Scroll Mode 下清空命中区，不更新 _lastRegionGeneration
            // （退出 scroll mode 时由 force=true 路径重建）。
            if (_scroll.ScrollOffset > 0)
            {
                vtInput.ClearRegions();
                return;
            }

            // 非按钮模式或无请求时清除所有区域，防止过期按钮被点击触发
            var req = _console.CurrentRequest;
            if (req == null || req.InputType == InputType.EnterKey || req.InputType == InputType.AnyKey)
            {
                vtInput.ClearRegions();
                _lastRegionGeneration = _console.LastButtonGeneration;
                return;
            }

            long currentGen = _console.LastButtonGeneration;
            if (!force && currentGen == _lastRegionGeneration) return;

            _lastRegionGeneration = currentGen;
            vtInput.ClearRegions();

            var lines = _console.DisplayLineList;
            if (lines == null || lines.Count == 0) return;

            int windowHeight = _getScreen()!.WindowHeight;
            int visibleLines = Math.Min(Math.Max(windowHeight - 1, 1), lines.Count);
            int startLine = Math.Max(0, lines.Count - visibleLines);

            for (int i = 0; i < visibleLines; i++)
            {
                int lineIndex = startLine + i;
                var line = lines[lineIndex];
                if (line?.Buttons == null || line.Buttons.Length == 0) continue;

                string formatted = TerminalLineFormatter.FormatLineForTerminal(
                    line, _console.SelectingButton, _console.CharWidthConfig, ansiEnabled: true);
                // viewport row = i（备用屏绝对坐标，与 SGR mouse 的 Cy-1 同一空间）
                vtInput.RecordLineRegions(formatted, i, line.Buttons, currentGen);
            }
        }

        // 按钮位置信息：viewport 行号 + 终端列范围，用于二维方向键导航
        private readonly record struct ButtonPos(
            int Row,
            int Left,
            int Right,
            ConsoleButtonString Button,
            string InputKey);

        /// <summary>
        /// 遍历可见 displayLineList，按终端显示宽度计算每个按钮的 (row, left, right)。
        /// 同一 InputKey 的分裂片段（DivideAt 产生）只保留第一个。
        /// </summary>
        private List<ButtonPos> RebuildButtonPositions()
        {
            var result = new List<ButtonPos>();
            var lines = _console.DisplayLineList;
            if (lines == null || lines.Count == 0) return result;

            int windowHeight = _getScreen()!.WindowHeight;
            int visibleLines = Math.Min(Math.Max(windowHeight - 1, 1), lines.Count);
            int startLine = Math.Max(0, lines.Count - visibleLines);

            long currentGen = _console.LastButtonGeneration;
            // 整数输入语义下（IntValue/IntButton/AnyValue），字符串按钮提交后会被
            // DispatchInput 的 long.TryParse 拒绝、或 SelectedString 返回 null，
            // 属于按了无反应的噪声按钮，这里统一过滤掉。
            var reqType = _console.CurrentRequest?.InputType;
            bool intMode = reqType == InputType.IntValue
                || reqType == InputType.IntButton
                || reqType == InputType.AnyValue;
            var seenKeys = new HashSet<string>();

            for (int i = 0; i < visibleLines; i++)
            {
                int lineIndex = startLine + i;
                var line = lines[lineIndex];
                if (line?.Buttons == null || line.Buttons.Length == 0) continue;

                string formatted = TerminalLineFormatter.FormatLineForTerminal(
                    line, _console.SelectingButton, _console.CharWidthConfig, ansiEnabled: true);
                int column = TerminalDisplayWidth.LeadingDisplayWidth(formatted);

                foreach (var btn in line.Buttons)
                {
                    if (btn == null) continue;
                    string btnText = btn.ToString() ?? "";
                    int segmentWidth = TerminalDisplayWidth.GetDisplayWidth(btnText);

                    if (btn.IsButton && btn.Generation == currentGen && segmentWidth > 0
                        && (!intMode || btn.IsInteger))
                    {
                        string key = btn.IsInteger ? btn.Input.ToString() : btn.Inputs;
                        if (seenKeys.Add(key))
                            result.Add(new ButtonPos(i, column, column + segmentWidth - 1, btn, key));
                    }
                    column += segmentWidth;
                }
            }
            return result;
        }

        /// <summary>
        /// 重绘指定 viewport 行的按钮内容（含选中高亮）。
        /// ADR-0005 Issue 4：删除非 VT FullRefresh 退化分支，仅保留 VT 绝对定位单行重绘。
        /// </summary>
        private void RedrawButtonLine(int viewportRow)
        {
            var screen = _getScreen()!;
            int savedRow, savedCol;
            try { savedRow = Console.CursorTop; savedCol = Console.CursorLeft; }
            catch (Exception) { /* 光标位置探测失败，用 0,0 */ savedRow = 0; savedCol = 0; }

            var lines = _console.DisplayLineList;
            int windowHeight = screen.WindowHeight;
            int visibleLines = Math.Min(Math.Max(windowHeight - 1, 1), lines.Count);
            int startLine = Math.Max(0, lines.Count - visibleLines);
            int lineIndex = startLine + viewportRow;
            if (lineIndex >= 0 && lineIndex < lines.Count)
            {
                string formatted = TerminalLineFormatter.FormatLineForTerminal(
                    lines[lineIndex], _console.SelectingButton, _console.CharWidthConfig, ansiEnabled: true);
                screen.WriteLineAt(viewportRow, formatted.Length > 0 ? formatted : "");
            }

            screen.SetCursor(savedRow, savedCol);
        }

        private void ProcessButtonModeKey(ConsoleKeyInfo key)
        {
            if (_buttonPositions.Count == 0) return;

            if (key.Key == ConsoleKey.Enter)
            {
                ConfirmButton();
                return;
            }

            int currentIdx = (_selectedButtonIndex >= 0 && _selectedButtonIndex < _buttonPositions.Count)
                ? _selectedButtonIndex
                : 0;

            var current = _buttonPositions[currentIdx];
            int currentCenter = (current.Left + current.Right) / 2;

            // 四方向导航：候选过滤 + 更优判定，统一走 FindNextButton。
            // Up/Down：跨行选取最近行，同行取中心列最近；Left/Right：同行取最远边界。
            int newIdx = key.Key switch
            {
                ConsoleKey.UpArrow => FindNextButton(
                    p => p.Row < current.Row,
                    (a, best) => a.Row > best.Row
                        || (a.Row == best.Row && CenterDist(a, currentCenter) < CenterDist(best, currentCenter))),
                ConsoleKey.DownArrow => FindNextButton(
                    p => p.Row > current.Row,
                    (a, best) => a.Row < best.Row
                        || (a.Row == best.Row && CenterDist(a, currentCenter) < CenterDist(best, currentCenter))),
                ConsoleKey.LeftArrow => FindNextButton(
                    p => p.Row == current.Row && p.Right < current.Left,
                    (a, best) => a.Right > best.Right),
                ConsoleKey.RightArrow => FindNextButton(
                    p => p.Row == current.Row && p.Left > current.Right,
                    (a, best) => a.Left < best.Left),
                _ => -1,
            };

            if (newIdx >= 0 && newIdx != currentIdx)
            {
                int oldRow = _buttonPositions[currentIdx].Row;
                int newRow = _buttonPositions[newIdx].Row;
                _selectedButtonIndex = newIdx;
                _console.SetSelectingButton(_buttonPositions[newIdx].Button);
                RedrawButtonLine(oldRow);
                if (newRow != oldRow)
                    RedrawButtonLine(newRow);
            }
        }

        /// <summary>
        /// 在按钮列表中查找下一个目标按钮。
        /// <paramref name="isCandidate"/> 过滤候选；<paramref name="isBetter"/> 判定候选是否优于当前最优。
        /// 返回命中索引，无候选返回 -1。
        /// </summary>
        private int FindNextButton(Func<ButtonPos, bool> isCandidate, Func<ButtonPos, ButtonPos, bool> isBetter)
        {
            int newIdx = -1;
            for (int i = 0; i < _buttonPositions.Count; i++)
            {
                var p = _buttonPositions[i];
                if (!isCandidate(p)) continue;
                if (newIdx < 0 || isBetter(p, _buttonPositions[newIdx]))
                    newIdx = i;
            }
            return newIdx;
        }

        private static int CenterDist(ButtonPos p, int center)
            => Math.Abs((p.Left + p.Right) / 2 - center);

        private void ConfirmButton()
        {
            if (_selectedButtonIndex < 0 || _selectedButtonIndex >= _buttonPositions.Count) return;

            var btn = _buttonPositions[_selectedButtonIndex].Button;
            string input = btn.IsInteger ? btn.Input.ToString() : btn.Inputs;
            ExitButtonModeCore();
            _dispatchInput(input);
        }

        private void ExitButtonModeCore()
        {
            int highlightedRow = -1;
            if (_selectedButtonIndex >= 0 && _selectedButtonIndex < _buttonPositions.Count)
                highlightedRow = _buttonPositions[_selectedButtonIndex].Row;

            _console.SetSelectingButton(null);
            _buttonMode = false;
            _buttonPositions = [];
            _selectedButtonIndex = -1;

            if (highlightedRow >= 0)
                RedrawButtonLine(highlightedRow);
        }

        private static bool ButtonInputKeysEqual(List<ButtonPos> a, List<ButtonPos> b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
            {
                if (a[i].InputKey != b[i].InputKey) return false;
            }
            return true;
        }
    }
}
