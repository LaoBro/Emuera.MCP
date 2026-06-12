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
            while (!IsStopped)
            {
                if (console._needFullRefresh)
                {
                    console._needFullRefresh = false;
                    FlushBuffer();
                    FullRefresh();
                    ResetCountdown();
                    continue;
                }

                var timeoutMs = console.InputTimeoutMs;
                if (timeoutMs.HasValue && timeoutMs.Value <= 0)
                {
                    // 超时：覆盖倒计时行为 TimeUpMes
                    OverwriteCountdownLine(console.TimeUpMessage ?? "");
                    ResetCountdown();
                    if (_buf.Length > 0)
                    {
                        WriteOutput("\r" + new string(' ', _buf.Length) + "\r", false);
                        _buf.Clear();
                    }
                    if (_buttonMode)
                    {
                        ClearButtonPrompt();
                        _buttonMode = false;
                        _currentButtons = [];
                        _selectedButtonIndex = -1;
                    }
                    console.SubmitTimeout();
                    FlushBuffer();
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
                            // presetTimer 刚输出倒计时行，记录其位置
                            // 光标在倒计时行下一行的行首，所以倒计时行 = CursorTop - 1
                            try { _countdownLineTop = Console.CursorTop - 1; }
                            catch { _countdownLineTop = -1; }
                        }
                        OverwriteCountdownLine(currentText);
                    }
                }
                else if (_countdownLineTop >= 0)
                {
                    // 不再是 DisplayTime 状态（可能游戏已推进），重置
                    ResetCountdown();
                }

                if (Console.KeyAvailable)
                {
                    var key = Console.ReadKey(true);
                    ProcessKey(key);
                }

                FlushBuffer();

                // 游戏状态变化后重置按钮选择模式
                if (_buttonMode && console.State != ConsoleState.WaitInput)
                {
                    ClearButtonPrompt();
                    _buttonMode = false;
                    _currentButtons = [];
                    _selectedButtonIndex = -1;
                }

                // 有按钮时自动进入按钮选择模式（EnterKey/AnyKey 不需要按钮选择）
                if (!_buttonMode && console.State == ConsoleState.WaitInput)
                {
                    var req = console.CurrentRequest;
                    if (req != null && req.InputType != InputType.EnterKey && req.InputType != InputType.AnyKey)
                    {
                        var buttons = console.CollectCurrentButtons();
                        if (buttons.Count > 0)
                        {
                            _buttonMode = true;
                            _currentButtons = buttons;
                            _selectedButtonIndex = 0;
                            if (_buf.Length > 0)
                            {
                                WriteOutput("\r" + new string(' ', _buf.Length) + "\r", false);
                                _buf.Clear();
                            }
                            RenderButtonPrompt();
                        }
                    }
                }

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

        /// <summary>
        /// 计算字符串在终端中的显示宽度（中日韩字符占2列，其余占1列）。
        /// 与 EmueraConsole.AgentBridge 中的 GetDisplayWidth 逻辑一致。
        /// </summary>
        private static int GetDisplayWidth(string str)
        {
            int width = 0;
            foreach (char c in str)
            {
                width += IsWideChar(c) ? 2 : 1;
            }
            return width;
        }

        private static bool IsWideChar(char c)
        {
            return (c >= 0x2E80 && c <= 0x9FFF)
                || (c >= 0xAC00 && c <= 0xD7AF)
                || (c >= 0xF900 && c <= 0xFAFF)
                || (c >= 0xFF01 && c <= 0xFF60)
                || (c >= 0xFFE0 && c <= 0xFFE6);
        }

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

        private void ProcessChar(char ch)
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
            _buttonMode = false;
            _currentButtons = [];
            _selectedButtonIndex = -1;

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

        protected override void DispatchInput(string input)
        {
            if (console.State != ConsoleState.WaitInput)
            {
                WriteOutput("[终端] 当前不在等待输入状态，输入已忽略");
                return;
            }

            var req = console.CurrentRequest;
            if (req == null) return;

            switch (req.InputType)
            {
                case InputType.EnterKey:
                case InputType.AnyKey:
                case InputType.StrValue:
                case InputType.IntButton:
                case InputType.StrButton:
                    console.PressEnterKey(false, input, false);
                    break;

                case InputType.IntValue:
                case InputType.AnyValue:
                    if (long.TryParse(input, out _))
                        console.PressEnterKey(false, input, false);
                    else
                        WriteOutput("[终端] 当前需要整数输入，请重试");
                    break;

                case InputType.PrimitiveMouseKey:
                    WriteOutput("[终端] 当前等待原始鼠标/键盘事件，终端无法模拟，请在窗口中操作");
                    break;

                default:
                    WriteOutput($"[终端] 未处理的输入类型: {req.InputType}");
                    break;
            }
        }
    }
}
