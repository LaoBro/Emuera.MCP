using System;
using System.Collections.Generic;
using MinorShift.Emuera.Runtime;
using MinorShift.Emuera.UI.Game;

namespace MinorShift.Emuera.GameView
{
    /// <summary>
    /// 按钮选择模式：管理方向键导航、高亮、确认，以及 VT 鼠标区域同步。
    /// 从 AgentCliProtocol 拆分以隔离按钮相关状态与逻辑。
    /// </summary>
    internal sealed class ButtonSelectionMode
    {
        private readonly EmueraConsole _console;
        private readonly Func<AgentCliVtScreen?> _getScreen;
        private readonly Action _requestFullRefresh;
        private readonly Action<string> _dispatchInput;
        private readonly Action _clearInputBuffer;

        private bool _buttonMode;
        private List<ButtonPos> _buttonPositions = [];
        private int _selectedButtonIndex = -1;
        private long _lastButtonSyncGen = -1;
        private long _lastRegionGeneration = -1;

        public ButtonSelectionMode(
            EmueraConsole console,
            Func<AgentCliVtScreen?> getScreen,
            Action requestFullRefresh,
            Action<string> dispatchInput,
            Action clearInputBuffer)
        {
            _console = console;
            _getScreen = getScreen;
            _requestFullRefresh = requestFullRefresh;
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

        /// <summary>同步按钮位置状态，按需进入/退出选择模式或更新高亮。</summary>
        internal void SyncButtonState()
        {
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
                    if (!ButtonListEquals(_buttonPositions, newPositions))
                    {
                        int oldRow = _selectedButtonIndex >= 0 && _selectedButtonIndex < _buttonPositions.Count
                            ? _buttonPositions[_selectedButtonIndex].Row : -1;
                        _buttonPositions = newPositions;
                        _selectedButtonIndex = Math.Clamp(_selectedButtonIndex, 0, newPositions.Count - 1);
                        _console.SetSelectingButton(newPositions[_selectedButtonIndex].Button);
                        if (oldRow >= 0) RedrawButtonLine(oldRow);
                        int newRow = newPositions[_selectedButtonIndex].Row;
                        if (newRow != oldRow && _getScreen() != null)
                            RedrawButtonLine(newRow);
                    }
                }
                else
                {
                    _buttonPositions = newPositions;
                }
            }
        }

        /// <summary>刷新 VT 鼠标命中区域。仅 VT 模式有效。</summary>
        internal void RefreshButtonRegions(AgentCliVtInput? vtInput, bool force = false)
        {
            if (vtInput == null) return;

            long currentGen = _console.LastButtonGeneration;
            if (!force && currentGen == _lastRegionGeneration) return;

            _lastRegionGeneration = currentGen;
            vtInput.ClearRegions();

            var lines = _console.DisplayLineList;
            if (lines == null || lines.Count == 0) return;

            int windowHeight = _getScreen()?.WindowHeight ?? TerminalCursor.TryGetWindowHeight();
            int visibleLines = Math.Min(Math.Max(windowHeight - 1, 1), lines.Count);
            int startLine = Math.Max(0, lines.Count - visibleLines);

            for (int i = 0; i < visibleLines; i++)
            {
                int lineIndex = startLine + i;
                var line = lines[lineIndex];
                if (line?.Buttons == null || line.Buttons.Length == 0) continue;

                string formatted = _console.FormatLineForTerminal(line);
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

            int windowHeight = _getScreen()?.WindowHeight ?? TerminalCursor.TryGetWindowHeight();
            int visibleLines = Math.Min(Math.Max(windowHeight - 1, 1), lines.Count);
            int startLine = Math.Max(0, lines.Count - visibleLines);

            long currentGen = _console.LastButtonGeneration;
            var seenKeys = new HashSet<string>();

            for (int i = 0; i < visibleLines; i++)
            {
                int lineIndex = startLine + i;
                var line = lines[lineIndex];
                if (line?.Buttons == null || line.Buttons.Length == 0) continue;

                string formatted = _console.FormatLineForTerminal(line);
                int column = LeadingDisplayWidth(formatted);

                foreach (var btn in line.Buttons)
                {
                    if (btn == null) continue;
                    string btnText = btn.ToString() ?? "";
                    int segmentWidth = TerminalDisplayWidth.GetDisplayWidth(btnText);

                    if (btn.IsButton && btn.Generation == currentGen && segmentWidth > 0)
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

        // 计算格式化行的前导空白宽度（对齐缩进），与 ButtonRegionTracker 逻辑一致
        private static int LeadingDisplayWidth(string s)
        {
            int width = 0;
            foreach (char c in s)
            {
                if (c == ' ') { width += 1; continue; }
                if (c == '\u3000') { width += 2; continue; }
                break;
            }
            return width;
        }

        /// <summary>
        /// 重绘指定 viewport 行的按钮内容（含选中高亮）。
        /// VT 模式用绝对定位单行重绘，非 VT 模式退化为 FullRefresh。
        /// </summary>
        private void RedrawButtonLine(int viewportRow)
        {
            var screen = _getScreen();
            if (screen != null)
            {
                int savedRow, savedCol;
                try { savedRow = Console.CursorTop; savedCol = Console.CursorLeft; }
                catch { savedRow = 0; savedCol = 0; }

                var lines = _console.DisplayLineList;
                int windowHeight = screen.WindowHeight;
                int visibleLines = Math.Min(Math.Max(windowHeight - 1, 1), lines.Count);
                int startLine = Math.Max(0, lines.Count - visibleLines);
                int lineIndex = startLine + viewportRow;
                if (lineIndex >= 0 && lineIndex < lines.Count)
                {
                    string formatted = _console.FormatLineForTerminal(lines[lineIndex]);
                    screen.WriteLineAt(viewportRow, formatted.Length > 0 ? formatted : "");
                }

                screen.SetCursor(savedRow, savedCol);
            }
            else
            {
                // 非 VT 模式无法定位单行，退化为全量刷新
                _requestFullRefresh();
            }
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
            int newIdx = -1;

            switch (key.Key)
            {
                case ConsoleKey.UpArrow:
                    {
                        // 上方行中选取最接近当前行的，同行则取中心列最近
                        int bestRow = int.MinValue;
                        int bestDist = int.MaxValue;
                        for (int i = 0; i < _buttonPositions.Count; i++)
                        {
                            var p = _buttonPositions[i];
                            if (p.Row >= current.Row) continue;
                            int pCenter = (p.Left + p.Right) / 2;
                            int dist = Math.Abs(pCenter - currentCenter);
                            if (p.Row > bestRow || (p.Row == bestRow && dist < bestDist))
                            {
                                bestRow = p.Row;
                                bestDist = dist;
                                newIdx = i;
                            }
                        }
                        break;
                    }
                case ConsoleKey.DownArrow:
                    {
                        // 下方行中选取最接近当前行的，同行则取中心列最近
                        int bestRow = int.MaxValue;
                        int bestDist = int.MaxValue;
                        for (int i = 0; i < _buttonPositions.Count; i++)
                        {
                            var p = _buttonPositions[i];
                            if (p.Row <= current.Row) continue;
                            int pCenter = (p.Left + p.Right) / 2;
                            int dist = Math.Abs(pCenter - currentCenter);
                            if (p.Row < bestRow || (p.Row == bestRow && dist < bestDist))
                            {
                                bestRow = p.Row;
                                bestDist = dist;
                                newIdx = i;
                            }
                        }
                        break;
                    }
                case ConsoleKey.LeftArrow:
                    {
                        // 同行中 right < currentLeft 的最大 right
                        int bestRight = int.MinValue;
                        for (int i = 0; i < _buttonPositions.Count; i++)
                        {
                            var p = _buttonPositions[i];
                            if (p.Row != current.Row || p.Right >= current.Left) continue;
                            if (p.Right > bestRight)
                            {
                                bestRight = p.Right;
                                newIdx = i;
                            }
                        }
                        break;
                    }
                case ConsoleKey.RightArrow:
                    {
                        // 同行中 left > currentRight 的最小 left
                        int bestLeft = int.MaxValue;
                        for (int i = 0; i < _buttonPositions.Count; i++)
                        {
                            var p = _buttonPositions[i];
                            if (p.Row != current.Row || p.Left <= current.Right) continue;
                            if (p.Left < bestLeft)
                            {
                                bestLeft = p.Left;
                                newIdx = i;
                            }
                        }
                        break;
                    }
                default:
                    return;
            }

            if (newIdx >= 0 && newIdx != currentIdx)
            {
                int oldRow = _buttonPositions[currentIdx].Row;
                int newRow = _buttonPositions[newIdx].Row;
                _selectedButtonIndex = newIdx;
                _console.SetSelectingButton(_buttonPositions[newIdx].Button);
                RedrawButtonLine(oldRow);
                if (newRow != oldRow && _getScreen() != null)
                    RedrawButtonLine(newRow);
            }
        }

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

        private static bool ButtonListEquals(List<ButtonPos> a, List<ButtonPos> b)
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
