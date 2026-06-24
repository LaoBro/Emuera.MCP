using System;
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
        private readonly ButtonSelectionMode _buttons;
        private readonly CountdownRenderer _countdown;
        private readonly TerminalRenderer _renderer;

        public AgentCliProtocol(EmueraConsole console, IConsoleUI ui)
            : base(console, ui)
        {
            _ansiEnabled = Program.AnsiEnabled || !OperatingSystem.IsWindows();
            _cursor = new TerminalCursor(_ansiEnabled);
            _renderer = new TerminalRenderer(console, () => _screen, _cursor);
            _buttons = new ButtonSelectionMode(
                console,
                () => _screen,
                _renderer.FullRefresh,
                input => DispatchInput(input),
                ClearInputBuffer);
            _countdown = new CountdownRenderer(console, () => _screen, _cursor, _ansiEnabled);
        }

        internal override string? GetInitialTurn() => null;
        internal override string? Step(string input) => null;

        internal void RunCliLoop()
        {
            _renderer.FlushBuffer();
            if (Console.IsInputRedirected)
                RunPipeCliLoop(Console.In);
            else if (!TryRunVtLoop())
                RunConsoleKeyLoop();
            _renderer.FlushBuffer();
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
                if (console.ConsumeNeedFullRefresh())
                {
                    _renderer.FlushBuffer();
                    _renderer.FullRefresh();
                    _countdown.Reset();
                    _buttons.SyncButtonState();
                    _buttons.RefreshButtonRegions(vtInput, force: true);
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
                        _countdown.Overwrite(console.TimeUpMessage ?? "");
                        _countdown.Reset();
                        ClearInputBuffer();
                        console.SubmitTimeout();
                        _renderer.FlushBuffer();
                        _buttons.SyncButtonState();
                        _buttons.RefreshButtonRegions(vtInput);
                        continue;
                    }
                    _countdown.Update();
                    token.WaitHandle.WaitOne(PollIntervalMs);
                }

                if (CheckResize())
                {
                    _renderer.FullRefresh();
                    _buttons.RefreshButtonRegions(vtInput, force: true);
                }

                _renderer.FlushBuffer();
                _buttons.SyncButtonState();
                _buttons.RefreshButtonRegions(vtInput);
            }
        }

        private void RunConsoleKeyLoop()
        {
            var token = StopToken;

            while (!token.IsCancellationRequested)
            {
                if (console.ConsumeNeedFullRefresh())
                {
                    _renderer.FlushBuffer();
                    _renderer.FullRefresh();
                    _countdown.Reset();
                    _buttons.SyncButtonState();
                    continue;
                }

                var timeoutMs = console.InputTimeoutMs;
                if (timeoutMs.HasValue && timeoutMs.Value <= 0)
                {
                    _countdown.Overwrite(console.TimeUpMessage ?? "");
                    _countdown.Reset();
                    ClearInputBuffer();
                    console.SubmitTimeout();
                    _renderer.FlushBuffer();
                    _buttons.SyncButtonState();
                    continue;
                }

                _countdown.Update();

                if (Console.KeyAvailable)
                    ProcessKey(Console.ReadKey(true));

                _renderer.FlushBuffer();
                _buttons.SyncButtonState();

                token.WaitHandle.WaitOne(PollIntervalMs);
            }
        }

        private void RunPipeCliLoop(TextReader input)
        {
            var token = StopToken;
            while (!token.IsCancellationRequested)
            {
                if (console.ConsumeNeedFullRefresh())
                {
                    _renderer.FlushBuffer();
                    _renderer.FullRefresh();
                }

                string? line = input.ReadLine();
                if (line == null) break;

                foreach (char ch in line)
                    ProcessChar(ch);
                ProcessChar('\r');
                _renderer.FlushBuffer();
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
            // 按钮选择模式优先处理方向键/Enter，未消费则走常规输入
            if (_buttons.HandleKey(key))
                return;

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
            if (_buttons.IsButtonMode) _buttons.ExitButtonMode();

            string input = btn.IsInteger ? btn.Input.ToString() : btn.Inputs;
            DispatchInput(input);
        }

        internal void DispatchMouseMiss()
        {
            if (console.State != ConsoleState.WaitInput) return;
            if (_buttons.IsButtonMode) return;

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

        private void ClearInputBuffer()
        {
            if (_buf.Length > 0)
            {
                WriteOutput("\r" + new string(' ', _buf.Length) + "\r", false);
                _buf.Clear();
            }
        }

        protected override void OnInputRejected(string reason)
        {
            // 与 winforms 行为一致：静默忽略无效输入，不向终端输出提示
        }
    }
}
