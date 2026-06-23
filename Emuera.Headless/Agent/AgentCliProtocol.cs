using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using MinorShift.Emuera.Runtime;
using MinorShift.Emuera.UI.Game;

namespace MinorShift.Emuera.GameView
{
    internal sealed class AgentCliProtocol : AgentProtocolBase
    {
        private readonly StringBuilder _buf = new();

        // 倒计时行状态（viewport row，备用屏下与 buffer row 等价）
        private int _countdownLineRow = -1;
        private string _lastCountdownText = "";
        private int _lastCountdownWidth;

        // 按钮选择模式状态
        private bool _buttonMode;
        private List<ButtonPos> _buttonPositions = [];
        private int _selectedButtonIndex = -1;

        // VT 模式状态（DA1 探测通过后非 null）
        private AgentCliVtScreen? _screen;
        private AgentCliVtInput? _vtInput;
        private bool _vtCleanupDone;
        private bool _vtHooksRegistered;

        // resize 检测
        private int _lastWindowWidth = -1;
        private int _lastWindowHeight = -1;

        private readonly TerminalCursor _cursor;
        private readonly bool _ansiEnabled;
        private long _lastRegionGeneration = -1;
        private long _lastButtonSyncGen = -1;

        public AgentCliProtocol(EmueraConsole console, IConsoleUI ui)
            : base(console, ui)
        {
            _ansiEnabled = Program.AnsiEnabled || !OperatingSystem.IsWindows();
            _cursor = new TerminalCursor(_ansiEnabled);
        }

        internal override string? GetInitialTurn() => null;
        internal override string? Step(string input) => null;

        internal void RunCliLoop()
        {
            FlushBuffer();
            if (Console.IsInputRedirected)
                RunPipeCliLoop(Console.In);
            else if (!TryRunVtLoop())
                RunConsoleKeyLoop();
            FlushBuffer();
        }

        #region Main loops

        /// <summary>
        /// 尝试启动 VT 路径：DA1 探测 → 备用屏 → SGR mouse → 主循环轮询。
        /// DA1 探测失败时返回 false，调用方走降级路径。
        /// </summary>
        private bool TryRunVtLoop()
        {
            _vtInput = (AgentCliVtInput?)WindowsVtInput.TryCreate(this) ?? UnixVtInput.TryCreate(this);
            if (_vtInput == null)
                return false;

            _screen = new AgentCliVtScreen();
            RegisterVtCleanupHooks();

            try
            {
                // 进入备用屏 → 启用 SGR mouse
                _screen.EnterAlternateScreen();
                _vtInput.EnableSgrMouse();

                RunVtMainLoop();
            }
            finally
            {
                // 严格顺序：禁用 SGR mouse → 恢复 input mode → 退出备用屏
                // VtInput.Dispose 负责前两步，VtScreen.Dispose 负责第三步
                CleanupVt();
            }

            return true;
        }

        private void RunVtMainLoop()
        {
            var token = StopToken;
            var vtInput = _vtInput!;
            var screen = _screen!;

            // 初始化 resize 检测
            _lastWindowWidth = screen.WindowWidth;
            _lastWindowHeight = screen.WindowHeight;

            while (!token.IsCancellationRequested)
            {
                if (console._needFullRefresh)
                {
                    console._needFullRefresh = false;
                    FlushBuffer();
                    FullRefresh();
                    ResetCountdown();
                    SyncButtonState();
                    RefreshButtonRegions(vtInput, force: true);
                    continue;
                }

                if (vtInput.HasInputAvailable())
                {
                    int b = vtInput.ReadByte();
                    if (b >= 0)
                        vtInput.Feed((byte)b);
                }
                else
                {
                    var timeoutMs = console.InputTimeoutMs;
                    if (timeoutMs.HasValue && timeoutMs.Value <= 0)
                    {
                        OverwriteCountdownLine(console.TimeUpMessage ?? "");
                        ResetCountdown();
                        ClearInputBuffer();
                        console.SubmitTimeout();
                        FlushBuffer();
                        SyncButtonState();
                        RefreshButtonRegions(vtInput);
                        continue;
                    }
                    UpdateCountdown();
                    token.WaitHandle.WaitOne(PollIntervalMs);
                }

                if (CheckResize())
                {
                    FullRefresh();
                    RefreshButtonRegions(vtInput, force: true);
                }

                FlushBuffer();
                SyncButtonState();
                RefreshButtonRegions(vtInput);
            }
        }

        private void RunConsoleKeyLoop()
        {
            var token = StopToken;

            while (!token.IsCancellationRequested)
            {
                if (console._needFullRefresh)
                {
                    console._needFullRefresh = false;
                    FlushBuffer();
                    FullRefresh();
                    ResetCountdown();
                    SyncButtonState();
                    continue;
                }

                var timeoutMs = console.InputTimeoutMs;
                if (timeoutMs.HasValue && timeoutMs.Value <= 0)
                {
                    OverwriteCountdownLine(console.TimeUpMessage ?? "");
                    ResetCountdown();
                    ClearInputBuffer();
                    console.SubmitTimeout();
                    FlushBuffer();
                    SyncButtonState();
                    continue;
                }

                UpdateCountdown();

                if (Console.KeyAvailable)
                    ProcessKey(Console.ReadKey(true));

                FlushBuffer();
                SyncButtonState();

                token.WaitHandle.WaitOne(PollIntervalMs);
            }
        }

        private void RunPipeCliLoop(TextReader input)
        {
            var token = StopToken;
            while (!token.IsCancellationRequested)
            {
                if (console._needFullRefresh)
                {
                    console._needFullRefresh = false;
                    FlushBuffer();
                    FullRefresh();
                }

                string? line = input.ReadLine();
                if (line == null) break;

                foreach (char ch in line)
                    ProcessChar(ch);
                ProcessChar('\r');
                FlushBuffer();
            }
        }

        #endregion

        #region VT lifecycle helpers

        /// <summary>VT 路径请求退出（Ctrl+C / CancelKeyPress 触发）。</summary>
        internal void RequestExit() => Stop();

        /// <summary>VT 解析器输出的 ConsoleKeyInfo 入口，复用现有 ProcessKey 分支。</summary>
        internal void ProcessKeyFromVt(ConsoleKeyInfo key) => ProcessKey(key);

        /// <summary>检测终端尺寸变化，返回 true 时调用方触发 FullRefresh。</summary>
        private bool CheckResize()
        {
            if (_screen == null) return false;
            int w = _screen.WindowWidth;
            int h = _screen.WindowHeight;
            if (w == _lastWindowWidth && h == _lastWindowHeight) return false;
            _lastWindowWidth = w;
            _lastWindowHeight = h;
            return true;
        }

        /// <summary>注册异常退出钩子，确保终端恢复。多钩子保障，cleanup 幂等。</summary>
        private void RegisterVtCleanupHooks()
        {
            if (_vtHooksRegistered) return;
            _vtHooksRegistered = true;

            try
            {
                Console.CancelKeyPress += OnVtCancelKeyPress;
                AppDomain.CurrentDomain.UnhandledException += OnVtUnhandledException;
                AppDomain.CurrentDomain.ProcessExit += OnVtProcessExit;
            }
            catch { }
        }

        private void OnVtCancelKeyPress(object? sender, ConsoleCancelEventArgs e)
        {
            // raw mode 下 Ctrl+C 主要靠 0x03 检测，此处作为安全网
            e.Cancel = true;
            Stop();
            CleanupVt();
        }

        private void OnVtUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            CleanupVt();
        }

        private void OnVtProcessExit(object? sender, EventArgs e)
        {
            CleanupVt();
        }

        /// <summary>幂等清理：禁用 SGR mouse → 恢复 input mode → 退出备用屏。</summary>
        private void CleanupVt()
        {
            if (_vtCleanupDone) return;
            _vtCleanupDone = true;

            try { _vtInput?.Dispose(); }
            catch { }

            try { _screen?.Dispose(); }
            catch { }

            _vtInput = null;
            _screen = null;
        }

        #endregion

        #region Countdown

        private void UpdateCountdown()
        {
            if (console.IsDisplayTimeActive)
            {
                string currentText = console.BuildCountdownText();
                if (currentText != _lastCountdownText)
                {
                    if (_countdownLineRow < 0)
                    {
                        try { _countdownLineRow = Console.CursorTop - 1; }
                        catch { _countdownLineRow = -1; }
                    }
                    OverwriteCountdownLine(currentText);
                }
            }
            else if (_countdownLineRow >= 0)
            {
                ResetCountdown();
            }
        }

        private void OverwriteCountdownLine(string newText)
        {
            if (_countdownLineRow < 0) return;

            string padded = PadToWidth(newText, _lastCountdownWidth, out int newWidth);

            if (_screen != null)
            {
                // VT 模式：绝对定位 + 清行尾
                _screen.WriteLineAt(_countdownLineRow, padded);
            }
            else
            {
                _cursor.Save(out int left, out int top);
                _cursor.Set(0, _countdownLineRow);
                if (_ansiEnabled)
                    TerminalCursor.TryWrite($"\x1b[2K{padded}");
                else
                    TerminalCursor.TryWrite(padded);
                _cursor.Set(left, top);
            }

            _lastCountdownText = newText;
            _lastCountdownWidth = Math.Max(newWidth, _lastCountdownWidth);
        }

        private void ResetCountdown()
        {
            _countdownLineRow = -1;
            _lastCountdownText = "";
            _lastCountdownWidth = 0;
        }

        #endregion

        #region Terminal output

        private void FlushBuffer()
        {
            EraseTerminalRows();
            string text = console.TakeAgentBuffer();
            if (text.Length > 0)
                Console.Write(text);
        }

        private void FullRefresh()
        {
            var lines = console.DisplayLineList;

            if (_screen != null)
            {
                // VT 模式：备用屏绝对定位重绘，不依赖自然滚动
                _screen.ClearScreen();
                if (lines.Count == 0) return;

                int consoleHeight = _screen.WindowHeight;
                int visibleLines = Math.Max(consoleHeight - 1, 1);
                int startLine = Math.Max(0, lines.Count - visibleLines);

                for (int i = 0; i < visibleLines && (startLine + i) < lines.Count; i++)
                {
                    int lineIndex = startLine + i;
                    int viewportRow = i;
                    string formatted = console.FormatLineForTerminal(lines[lineIndex]);
                    _screen.WriteLineAt(viewportRow, formatted.Length > 0 ? formatted : "");
                }

                // 光标定位到实际内容末尾的下一行行首，作为输入回显行
                int drawnRows = Math.Min(visibleLines, lines.Count - startLine);
                _screen.SetCursor(drawnRows, 0);
                return;
            }

            // 非 VT 模式：Console.WriteLine 自然滚动
            _cursor.ClearScreen();
            if (lines.Count == 0) return;

            int height = TerminalCursor.TryGetWindowHeight();
            int visLines = Math.Max(height - 1, 1);
            int startLn = Math.Max(0, lines.Count - visLines);

            for (int i = startLn; i < lines.Count; i++)
            {
                string formatted = console.FormatLineForTerminal(lines[i]);
                Console.WriteLine(formatted.Length > 0 ? formatted : "");
            }
        }

        private void EraseTerminalRows()
        {
            int rows = console._pendingEraseRows;
            if (rows <= 0) return;
            console._pendingEraseRows = 0;

            if (_screen != null)
            {
                // VT 模式：绝对定位 + ESC[2K 清行
                int currentRow = _screen.GetCurrentRow();
                for (int i = 0; i < rows; i++)
                {
                    int targetRow = currentRow - 1 - i;
                    if (targetRow < 0) break;
                    _screen.ClearLine(targetRow);
                }
                _screen.SetCursor(0, Math.Max(currentRow - rows, 0));
                return;
            }

            // 非 VT 模式：写空格覆盖
            _cursor.Save(out int savedLeft, out int savedTop);
            int consoleWidth = TerminalCursor.TryGetWindowWidth();

            for (int i = 0; i < rows; i++)
            {
                int targetTop = savedTop - 1 - i;
                if (targetTop < 0) break;
                _cursor.Set(0, targetTop);
                TerminalCursor.TryWrite(new string(' ', consoleWidth));
            }

            _cursor.Set(0, Math.Max(savedTop - rows, 0));
        }

        #endregion

        #region Keyboard / character input

        internal void ProcessChar(char ch)
        {
            if (ch == '\r' || ch == '\n')
            {
                WriteOutput("\r" + new string(' ', _buf.Length) + "\r", false);
                string input = _buf.ToString();
                _buf.Clear();
                DispatchInput(input);
            }
            else if (ch == '\b')
            {
                if (_buf.Length > 0)
                {
                    _buf.Remove(_buf.Length - 1, 1);
                    WriteOutput("\b \b", false);
                }
            }
            else if (ch == 27)
            {
                WriteOutput("\r" + new string(' ', _buf.Length) + "\r", false);
                _buf.Clear();
            }
            else if (!char.IsControl(ch))
            {
                _buf.Append(ch);
                WriteOutput(ch.ToString(), false);
            }
        }

        private void ProcessKey(ConsoleKeyInfo key)
        {
            // 按钮存在时，方向键用于进入/导航按钮选择模式
            bool isArrow = key.Key == ConsoleKey.UpArrow
                || key.Key == ConsoleKey.DownArrow
                || key.Key == ConsoleKey.LeftArrow
                || key.Key == ConsoleKey.RightArrow;

            if (_buttonMode)
            {
                if (key.Key == ConsoleKey.Enter)
                {
                    ProcessButtonModeKey(key);
                    return;
                }
                if (isArrow)
                {
                    ProcessButtonModeKey(key);
                    return;
                }
                // 其他键：退出按钮模式并透传原输入
                ExitButtonMode();
                // 继续走下面的常规输入处理
            }
            else if (isArrow && _buttonPositions.Count > 0)
            {
                // 首次按方向键进入按钮选择模式
                _buttonMode = true;
                _selectedButtonIndex = 0;
                console.SetSelectingButton(_buttonPositions[0].Button);
                ClearInputBuffer();
                RedrawButtonLine(_buttonPositions[0].Row);
                ProcessButtonModeKey(key);
                return;
            }

            if (key.Key == ConsoleKey.Enter)
                ProcessChar('\r');
            else if (key.Key == ConsoleKey.Backspace)
                ProcessChar('\b');
            else if (key.Key == ConsoleKey.Escape)
                ProcessChar((char)27);
            else if (!char.IsControl(key.KeyChar))
                ProcessChar(key.KeyChar);
        }

        internal void ProcessKeyFromMouseInput(ConsoleKeyInfo key) => ProcessKey(key);

        #endregion

        #region Mouse dispatch

        internal void DispatchMouseClick(ConsoleButtonString btn)
        {
            if (console.State != ConsoleState.WaitInput) return;

            // 鼠标点击退出按钮选择模式（若有），并执行点击命中
            if (_buttonMode) ExitButtonMode();

            string input = btn.IsInteger ? btn.Input.ToString() : btn.Inputs;
            DispatchInput(input);
        }

        internal void DispatchMouseMiss()
        {
            if (console.State != ConsoleState.WaitInput) return;
            if (_buttonMode) return;

            var req = console.CurrentRequest;
            if (req == null) return;

            // 仅在允许空输入的请求类型下才 dispatch 空输入（模拟回车）；
            // 整数输入等场景下点击空白区域直接忽略，与 winforms 行为一致
            switch (req.InputType)
            {
                case InputType.EnterKey:
                case InputType.AnyKey:
                case InputType.StrValue:
                case InputType.AnyValue:
                case InputType.IntButton:
                case InputType.StrButton:
                    DispatchInput("");
                    break;
            }
        }

        #endregion

        #region Button mode

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
            var lines = console.DisplayLineList;
            if (lines == null || lines.Count == 0) return result;

            int windowHeight = _screen != null
                ? _screen.WindowHeight
                : TerminalCursor.TryGetWindowHeight();
            int visibleLines = Math.Min(Math.Max(windowHeight - 1, 1), lines.Count);
            int startLine = Math.Max(0, lines.Count - visibleLines);

            long currentGen = console.LastButtonGeneration;
            var seenKeys = new HashSet<string>();

            for (int i = 0; i < visibleLines; i++)
            {
                int lineIndex = startLine + i;
                var line = lines[lineIndex];
                if (line?.Buttons == null || line.Buttons.Length == 0) continue;

                string formatted = console.FormatLineForTerminal(line);
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
        /// VT 模式用绝对定位单行重绘，并通过 Console.CursorTop/Left 保存恢复光标；
        /// 非 VT 模式退化为 FullRefresh。
        /// </summary>
        private void RedrawButtonLine(int viewportRow)
        {
            if (_screen != null)
            {
                int savedRow, savedCol;
                try { savedRow = Console.CursorTop; savedCol = Console.CursorLeft; }
                catch { savedRow = 0; savedCol = 0; }

                var lines = console.DisplayLineList;
                int windowHeight = _screen.WindowHeight;
                int visibleLines = Math.Min(Math.Max(windowHeight - 1, 1), lines.Count);
                int startLine = Math.Max(0, lines.Count - visibleLines);
                int lineIndex = startLine + viewportRow;
                if (lineIndex >= 0 && lineIndex < lines.Count)
                {
                    string formatted = console.FormatLineForTerminal(lines[lineIndex]);
                    _screen.WriteLineAt(viewportRow, formatted.Length > 0 ? formatted : "");
                }

                _screen.SetCursor(savedRow, savedCol);
            }
            else
            {
                // 非 VT 模式无法定位单行，退化为全量刷新
                FullRefresh();
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
                console.SetSelectingButton(_buttonPositions[newIdx].Button);
                RedrawButtonLine(oldRow);
                if (newRow != oldRow && _screen != null)
                    RedrawButtonLine(newRow);
            }
        }

        private void ConfirmButton()
        {
            if (_selectedButtonIndex < 0 || _selectedButtonIndex >= _buttonPositions.Count) return;

            var btn = _buttonPositions[_selectedButtonIndex].Button;
            string input = btn.IsInteger ? btn.Input.ToString() : btn.Inputs;
            ExitButtonMode();
            DispatchInput(input);
        }

        private void ExitButtonMode()
        {
            int highlightedRow = -1;
            if (_selectedButtonIndex >= 0 && _selectedButtonIndex < _buttonPositions.Count)
                highlightedRow = _buttonPositions[_selectedButtonIndex].Row;

            console.SetSelectingButton(null);
            _buttonMode = false;
            _buttonPositions = [];
            _selectedButtonIndex = -1;

            if (highlightedRow >= 0)
                RedrawButtonLine(highlightedRow);
        }

        private void ClearInputBuffer()
        {
            if (_buf.Length > 0)
            {
                WriteOutput("\r" + new string(' ', _buf.Length) + "\r", false);
                _buf.Clear();
            }
        }

        private void SyncButtonState()
        {
            bool shouldHaveButtons = false;
            List<ButtonPos> newPositions = [];

            if (console.State == ConsoleState.WaitInput)
            {
                var req = console.CurrentRequest;
                if (req != null && req.InputType != InputType.EnterKey && req.InputType != InputType.AnyKey)
                {
                    long currentGen = console.LastButtonGeneration;
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
                if (_buttonMode) ExitButtonMode();
                _buttonPositions = [];
                _vtInput?.ClearRegions();
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
                        console.SetSelectingButton(newPositions[_selectedButtonIndex].Button);
                        if (oldRow >= 0) RedrawButtonLine(oldRow);
                        int newRow = newPositions[_selectedButtonIndex].Row;
                        if (newRow != oldRow && _screen != null)
                            RedrawButtonLine(newRow);
                    }
                }
                else
                {
                    _buttonPositions = newPositions;
                }
            }
        }

        private void RefreshButtonRegions(AgentCliVtInput vtInput, bool force = false)
        {
            long currentGen = console.LastButtonGeneration;
            if (!force && currentGen == _lastRegionGeneration) return;

            _lastRegionGeneration = currentGen;
            vtInput.ClearRegions();

            var lines = console.DisplayLineList;
            if (lines == null || lines.Count == 0) return;

            // viewport 坐标：备用屏下 WindowTop 恒为 0，viewportRow = i
            int windowHeight = _screen != null
                ? _screen.WindowHeight
                : TerminalCursor.TryGetWindowHeight();

            int visibleLines = Math.Min(Math.Max(windowHeight - 1, 1), lines.Count);
            int startLine = Math.Max(0, lines.Count - visibleLines);

            for (int i = 0; i < visibleLines; i++)
            {
                int lineIndex = startLine + i;
                var line = lines[lineIndex];
                if (line?.Buttons == null || line.Buttons.Length == 0) continue;

                string formatted = console.FormatLineForTerminal(line);
                // viewport row = i（备用屏绝对坐标，与 SGR mouse 的 Cy-1 同一空间）
                vtInput.RecordLineRegions(formatted, i, line.Buttons, currentGen);
            }
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

        #endregion

        #region Terminal helpers

        private static int GetDisplayWidth(string str) => TerminalDisplayWidth.GetDisplayWidth(str);

        private static string PadToWidth(string text, int minWidth, out int actualWidth)
        {
            actualWidth = GetDisplayWidth(text);
            if (actualWidth < minWidth)
                return text + new string(' ', minWidth - actualWidth);
            return text;
        }

        #endregion

        protected override void OnInputRejected(string reason)
        {
            // 与 winforms 行为一致：静默忽略无效输入，不向终端输出提示
        }
    }
}
