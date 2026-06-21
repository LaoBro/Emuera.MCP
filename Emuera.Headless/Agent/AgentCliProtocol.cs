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

        /// <summary>倒计时行在终端中的行号（CursorTop），-1 表示无倒计时行。</summary>
        private int _countdownLineTop = -1;
        /// <summary>上次显示的倒计时文本，用于检测秒数变化。</summary>
        private string _lastCountdownText = "";
        /// <summary>上次倒计时文本的显示宽度（字符列数），用于空格覆盖。</summary>
        private int _lastCountdownWidth = 0;

        /// <summary>是否处于按钮选择模式。</summary>
        private bool _buttonMode;
        /// <summary>当前轮次按钮列表。</summary>
        private List<ConsoleButtonString> _currentButtons = [];
        /// <summary>当前选中按钮索引，-1 表示无选中。</summary>
        private int _selectedButtonIndex = -1;
        /// <summary>上次按钮选择提示文本的显示宽度，用于空格覆盖。</summary>
        private int _buttonPromptWidth = 0;

        private readonly bool _ansiEnabled;

        public AgentCliProtocol(EmueraConsole console, IConsoleUI ui)
            : base(console, ui)
        {
            _ansiEnabled = Program.AnsiEnabled || !OperatingSystem.IsWindows();
        }

        internal override string? GetInitialTurn() => null;

        internal override string? Step(string input) => null;

        /// <summary>
        /// 同步 CLI 主循环，由 Program.RunHeadless() 调用。
        /// </summary>
        internal void RunCliLoop()
        {
            FlushBuffer();

            if (Console.IsInputRedirected)
                RunPipeCliLoop(Console.In);
            else
                RunConsoleKeyLoop();

            FlushBuffer();
        }

        private void RunConsoleKeyLoop()
        {
            // 创建鼠标输入后端；非 Windows / pipe 模式返回 null，主循环回退到 Console.ReadKey
            using var mouseInput = AgentCliMouseInput.TryCreate(this);
            bool mouseEnabled = mouseInput != null && mouseInput.IsEnabled;

            while (!IsStopped)
            {
                if (console._needFullRefresh)
                {
                    console._needFullRefresh = false;
                    FlushBuffer();
                    FullRefresh();
                    ResetCountdown();
                    SyncButtonState();
                    if (mouseEnabled) RefreshButtonRegions(mouseInput!, force: true);
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
                    if (mouseEnabled) RefreshButtonRegions(mouseInput!);
                    continue;
                }

                // DisplayTime 倒计时更新
                if (console.IsDisplayTimeActive)
                {
                    string currentText = console.BuildCountdownText();
                    if (currentText != _lastCountdownText)
                    {
                        if (_countdownLineTop < 0)
                        {
                            try { _countdownLineTop = Console.CursorTop - 1; }
                            catch { _countdownLineTop = -1; }
                        }
                        OverwriteCountdownLine(currentText);
                    }
                }
                else if (_countdownLineTop >= 0)
                {
                    ResetCountdown();
                }

                if (mouseEnabled)
                {
                    // 方案 D：从 ReadConsoleInput 统一消费鼠标 + 键盘事件
                    mouseInput!.PollEvents();
                }
                else if (Console.KeyAvailable)
                {
                    var key = Console.ReadKey(true);
                    ProcessKey(key);
                }

                FlushBuffer();
                SyncButtonState();
                if (mouseEnabled) RefreshButtonRegions(mouseInput!);

                Thread.Sleep(PollIntervalMs);
            }
        }

        private void RunPipeCliLoop(TextReader input)
        {
            while (!IsStopped)
            {
                if (console._needFullRefresh)
                {
                    console._needFullRefresh = false;
                    FlushBuffer();
                    FullRefresh();
                }

                string? line = input.ReadLine();
                if (line == null)
                    break;

                foreach (char ch in line)
                    ProcessChar(ch);

                ProcessChar('\r');
                FlushBuffer();
            }
        }

        /// <summary>
        /// 终端全量刷新：清屏后从 displayLineList 重绘所有可见行。
        /// </summary>
        private void FullRefresh()
        {
            try { Console.Clear(); }
            catch { return; }

            var lines = console.DisplayLineList;
            if (lines.Count == 0)
                return;

            int consoleHeight;
            try { consoleHeight = Console.WindowHeight; }
            catch { consoleHeight = 25; }

            // 只输出最后 N 行（终端可见区域，留 1 行给输入提示）
            int visibleLines = Math.Max(consoleHeight - 1, 1);
            int startLine = Math.Max(0, lines.Count - visibleLines);

            for (int i = startLine; i < lines.Count; i++)
            {
                string formatted = console.FormatLineForTerminal(lines[i]);
                if (formatted.Length > 0)
                    Console.WriteLine(formatted);
                else
                    Console.WriteLine();
            }
        }

        /// <summary>
        /// 用新文本覆盖终端中的倒计时行，然后恢复光标位置。
        /// </summary>
        private void OverwriteCountdownLine(string newText)
        {
            if (_countdownLineTop < 0) return;

            int newWidth = GetDisplayWidth(newText);
            string padded = newText;
            if (newWidth < _lastCountdownWidth)
                padded += new string(' ', _lastCountdownWidth - newWidth);

            if (_ansiEnabled)
            {
                try
                {
                    Console.Write($"\x1b[s\x1b[{_countdownLineTop + 1};1H\x1b[2K{padded}\x1b[u");
                }
                catch { }
            }
            else
            {
                int savedLeft, savedTop;
                try
                {
                    savedLeft = Console.CursorLeft;
                    savedTop = Console.CursorTop;
                }
                catch { return; }

                try
                {
                    Console.SetCursorPosition(0, _countdownLineTop);
                    Console.Write(padded);
                }
                catch { }

                try { Console.SetCursorPosition(savedLeft, savedTop); }
                catch { }
            }

            _lastCountdownText = newText;
            _lastCountdownWidth = Math.Max(newWidth, _lastCountdownWidth);
        }

        /// <summary>
        /// 重置倒计时行状态。
        /// </summary>
        private void ResetCountdown()
        {
            _countdownLineTop = -1;
            _lastCountdownText = "";
            _lastCountdownWidth = 0;
        }

        private static int GetDisplayWidth(string str) => TerminalDisplayWidth.GetDisplayWidth(str);

        private void FlushBuffer()
        {
            // 先处理待擦除的终端行（CLEARLINE 产生的）
            EraseTerminalRows();

            string text = console.TakeAgentBuffer();
            if (text.Length > 0)
                Console.Write(text);
        }

        /// <summary>
        /// 擦除终端中由 CLEARLINE 标记的行数。
        /// 使用光标上移 + 空格覆盖的方式擦除，与输入回显擦除方式一致。
        /// </summary>
        private void EraseTerminalRows()
        {
            int rows = console._pendingEraseRows;
            if (rows <= 0) return;
            console._pendingEraseRows = 0;

            int savedLeft, savedTop;
            try
            {
                savedLeft = Console.CursorLeft;
                savedTop = Console.CursorTop;
            }
            catch { return; }

            int consoleWidth;
            try { consoleWidth = Console.WindowWidth; }
            catch { consoleWidth = 80; }

            for (int i = 0; i < rows; i++)
            {
                // 光标上移一行
                int targetTop = savedTop - 1 - i;
                if (targetTop < 0) break;

                try
                {
                    Console.SetCursorPosition(0, targetTop);
                    Console.Write(new string(' ', consoleWidth));
                }
                catch { break; }
            }

            // 恢复光标到被擦除区域的首行行首（内容已消失，新内容从此处开始）
            int newTop = Math.Max(savedTop - rows, 0);
            try { Console.SetCursorPosition(0, newTop); }
            catch { }
        }

        /// <summary>
        /// 处理字符输入。由 ProcessKey、RunPipeCliLoop、AgentCliMouseInput（Ctrl+C）共同复用。
        /// </summary>
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
            if (_buttonMode)
            {
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

        /// <summary>
        /// 由 AgentCliMouseInput 调用：将 KEY_EVENT_RECORD 转换后的 ConsoleKeyInfo 复用 ProcessKey 路径。
        /// 鼠标启用期间禁止 Console.ReadKey，键盘事件统一从 ReadConsoleInput 派发。
        /// </summary>
        internal void ProcessKeyFromMouseInput(ConsoleKeyInfo key) => ProcessKey(key);

        /// <summary>
        /// 由 AgentCliMouseInput 调用：鼠标命中按钮后提交对应输入。
        /// </summary>
        internal void DispatchMouseClick(ConsoleButtonString btn)
        {
            if (console.State != ConsoleState.WaitInput) return;

            // 非按钮模式（AnyKey/EnterKey）下，鼠标点击统一派发空输入推进游戏
            // 注意：不能传 "\n"，PressEnterKey 会按 \n split 成两个空串导致推进两次
            if (!_buttonMode)
            {
                DispatchInput("");
                return;
            }

            string input = btn.IsInteger ? btn.Input.ToString() : btn.Inputs;

            ClearButtonPrompt();
            _buttonMode = false;
            _currentButtons = [];
            _selectedButtonIndex = -1;
            _buttonPromptWidth = 0;

            DispatchInput(input);
        }

        /// <summary>
        /// 由 AgentCliMouseInput 调用：鼠标未命中任何按钮区域。
        /// 非按钮模式下派发回车推进游戏。
        /// </summary>
        internal void DispatchMouseMiss()
        {
            if (console.State != ConsoleState.WaitInput) return;
            if (_buttonMode) return;

            DispatchInput("");
        }

        private long _lastRegionGeneration = -1;

        /// <summary>
        /// 刷新按钮区域：在 FullRefresh + SyncButtonState 之后调用，
        /// 此时 WindowTop 已稳定，按 bufferRow = WindowTop + visibleRowIndex 记录区域。
        /// 居中偏移由 RecordLineRegions 从 formattedLine 的前导空格中提取。
        /// 仅在 generation 变化或强制刷新时重新记录，避免每 50ms 重复计算。
        /// </summary>
        private void RefreshButtonRegions(AgentCliMouseInput mouseInput, bool force = false)
        {
            long currentGen = console.LastButtonGeneration;
            if (!force && currentGen == _lastRegionGeneration) return;

            _lastRegionGeneration = currentGen;
            mouseInput.ClearRegions();

            var lines = console.DisplayLineList;
            if (lines == null || lines.Count == 0) return;

            int windowTop;
            int windowHeight;
            try
            {
                windowTop = Console.WindowTop;
                windowHeight = Console.WindowHeight;
            }
            catch { return; }

            int visibleLines = Math.Min(windowHeight - 1, lines.Count);
            int startLine = Math.Max(0, lines.Count - visibleLines);

            for (int i = 0; i < visibleLines; i++)
            {
                int lineIndex = startLine + i;
                int visibleRowIndex = i;
                int bufferRow = windowTop + visibleRowIndex;

                var line = lines[lineIndex];
                if (line?.Buttons == null || line.Buttons.Length == 0) continue;

                string formatted = console.FormatLineForTerminal(line);
                mouseInput.RecordLineRegions(formatted, bufferRow, line.Buttons, currentGen);
            }
        }

        /// <summary>
        /// 处理按钮选择模式下的按键。
        /// </summary>
        private void ProcessButtonModeKey(ConsoleKeyInfo key)
        {
            switch (key.Key)
            {
                case ConsoleKey.UpArrow:
                    if (_currentButtons.Count > 0)
                    {
                        _selectedButtonIndex = (_selectedButtonIndex - 1 + _currentButtons.Count) % _currentButtons.Count;
                        RenderButtonPrompt();
                    }
                    break;

                case ConsoleKey.DownArrow:
                    if (_currentButtons.Count > 0)
                    {
                        _selectedButtonIndex = (_selectedButtonIndex + 1) % _currentButtons.Count;
                        RenderButtonPrompt();
                    }
                    break;

                case ConsoleKey.Enter:
                    ConfirmButton();
                    break;
            }
        }

        /// <summary>
        /// 确认选中按钮，提交输入。
        /// </summary>
        private void ConfirmButton()
        {
            if (_selectedButtonIndex < 0 || _selectedButtonIndex >= _currentButtons.Count)
                return;

            var btn = _currentButtons[_selectedButtonIndex];
            string input = btn.IsInteger ? btn.Input.ToString() : btn.Inputs;

            ClearButtonPrompt();

            DispatchInput(input);
        }

        /// <summary>
        /// 渲染按钮选择提示行，覆盖当前输入行。
        /// 使用 SetCursorPosition + 空格覆盖，与倒计时覆盖方式一致。
        /// </summary>
        private void RenderButtonPrompt()
        {
            if (_selectedButtonIndex < 0 || _selectedButtonIndex >= _currentButtons.Count)
                return;

            var btn = _currentButtons[_selectedButtonIndex];
            string label = btn.ToString();
            string prompt = $"> [{_selectedButtonIndex + 1}/{_currentButtons.Count}] {label} | [Up/Dn] Switch  [Enter] OK";

            int newWidth = GetDisplayWidth(prompt);
            string padded = prompt;
            if (newWidth < _buttonPromptWidth)
                padded += new string(' ', _buttonPromptWidth - newWidth);
            WriteOutput("\r" + padded, false);
            _buttonPromptWidth = Math.Max(newWidth, _buttonPromptWidth);
        }

        /// <summary>
        /// 清除按钮选择提示行。
        /// 使用 SetCursorPosition 定位行首 + 空格覆盖到行尾，避免 GetDisplayWidth 对非 CJK 宽字符计算不准导致残留。
        /// </summary>
        private void ClearButtonPrompt()
        {
            if (_ansiEnabled)
            {
                WriteOutput("\x1b[2K\r", false);
            }
            else
            {
                FlushBuffer();
                try
                {
                    int top = Console.CursorTop;
                    Console.SetCursorPosition(0, top);
                    Console.Write(new string(' ', Console.WindowWidth - 1));
                    Console.SetCursorPosition(0, top);
                }
                catch { }
            }
            _buttonPromptWidth = 0;
        }

        private void ClearInputBuffer()
        {
            if (_buf.Length > 0)
            {
                WriteOutput("\r" + new string(' ', _buf.Length) + "\r", false);
                _buf.Clear();
            }
        }

        private static bool ButtonListEquals(List<ConsoleButtonString> a, List<ConsoleButtonString> b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
            {
                if (a[i].Generation != b[i].Generation) return false;
                if (a[i].IsInteger ? a[i].Input != b[i].Input : a[i].Inputs != b[i].Inputs)
                    return false;
            }
            return true;
        }

        private void SyncButtonState()
        {
            bool shouldHaveButtons = false;
            List<ConsoleButtonString> newButtons = [];

            if (console.State == ConsoleState.WaitInput)
            {
                var req = console.CurrentRequest;
                if (req != null && req.InputType != InputType.EnterKey && req.InputType != InputType.AnyKey)
                {
                    newButtons = console.CollectCurrentButtons();
                    if (newButtons.Count > 0)
                        shouldHaveButtons = true;
                }
            }

            if (!shouldHaveButtons)
            {
                if (_buttonMode)
                {
                    ClearButtonPrompt();
                    _buttonMode = false;
                    _currentButtons = [];
                    _selectedButtonIndex = -1;
                    _buttonPromptWidth = 0;
                }
            }
            else if (!_buttonMode)
            {
                _buttonMode = true;
                _currentButtons = newButtons;
                _selectedButtonIndex = 0;
                ClearInputBuffer();
                RenderButtonPrompt();
            }
            else
            {
                if (!ButtonListEquals(_currentButtons, newButtons))
                {
                    _currentButtons = newButtons;
                    _selectedButtonIndex = Math.Min(_selectedButtonIndex, newButtons.Count - 1);
                    if (_selectedButtonIndex < 0) _selectedButtonIndex = 0;
                    RenderButtonPrompt();
                }
            }
        }

        protected override void OnInputRejected(string reason)
        {
            WriteOutput($"[终端] {reason}");
        }
    }
}
