using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MinorShift.Emuera.Runtime;
using MinorShift.Emuera.Terminal.Platform;
using MinorShift.Emuera.UI.Game;

namespace MinorShift.Emuera.GameView
{
    internal sealed class AgentCliProtocol : AgentProtocolBase
    {
        private readonly StringBuilder _buf = new();

        // VT 模式状态（DA1 探测通过后非 null）
        private AgentCliVtScreen? _screen;
        private VtInputHandler? _vtInput;
        private readonly ITerminalInput _terminalInput;
        private readonly ITerminalSetup _terminalSetup;
        private bool _vtCleanupDone;
        private bool _vtHooksRegistered;

        // resize 检测
        private int _lastWindowWidth = -1;
        private int _lastWindowHeight = -1;

        private readonly TerminalCursor _cursor;
        private readonly ButtonSelectionMode _buttons;
        private readonly CountdownRenderer _countdown;
        private readonly TerminalRenderer _renderer;

        internal EmueraConsole GameConsole => console;

        public AgentCliProtocol(EmueraConsole console, IConsoleUI ui, ITerminalSetup terminalSetup, ITerminalInput terminalInput)
            : base(console, ui)
        {
            _terminalSetup = terminalSetup;
            _terminalInput = terminalInput;
            // ADR-0005：VT-only 后 ANSI 始终可用，_ansiEnabled 字段已删除。
            _cursor = new TerminalCursor();
            _renderer = new TerminalRenderer(console, () => _screen, _cursor);
            _buttons = new ButtonSelectionMode(
                console,
                () => _screen,
                _renderer.FullRefresh,
                input => DispatchInput(input),
                ClearInputBuffer);
            _countdown = new CountdownRenderer(console, () => _screen, _cursor);
        }

        internal override Task<string?> GetInitialTurnAsync() => Task.FromResult<string?>(null);
        internal override Task<string?> StepAsync(string input) => Task.FromResult<string?>(null);

        internal void RunCliLoop()
        {
            // 顶层异常边界：覆盖 HandleTimeout / ProcessChar / DispatchMouseClick /
            // DispatchMouseMiss / Initialize 等所有调 RunEmueraProgram 的路径。
            // 脚本运行期异常一律 fatal——Process 内部状态不可逆，恢复无意义。
            // VT 终端恢复由 RegisterVtCleanupHooks（AppDomain.UnhandledException /
            // ProcessExit）的多钩子保障，finally 块只负责 VT 路径。
            // ADR-0005：CLI 协议层改为 VT-only。VT 初始化失败抛 HeadlessFatalException，
            // 由 HeadlessRunner 捕获并非零退出——不再静默降级到残缺体验。
            try
            {
                // 注意：不在 VT 路径启用前 FlushBuffer。
                // Initialize() 阶段 @SYSTEM_TITLE 产生的 SetBgOp 等 pendingOps
                // 必须等到 _screen 就绪后由 RunAgentLoop 内部首次 FlushBuffer 消费，
                // 否则 VT 路径下 SetBgOp 会被提前消费（_screen==null）导致转义不发。
                if (!_terminalSetup.TryPrepareVtInput())
                {
                    throw new HeadlessFatalException(
                        "VT 终端初始化失败：ANSI 未启用或 stdin 被重定向。" +
                        "请在真实交互式终端运行（如 Windows Terminal / ConPTY / 现代 SSH），" +
                        "或改用 --server 模式。");
                }

                Console.Error.WriteLine("[headless] 终端路径: VT（备用屏 + SGR mouse + DA1 探测）");
                Console.Error.Flush();

                RunVtLoop();
                _renderer.FlushBuffer();
            }
            catch (Exception ex)
            {
                // HeadlessFatalException 不在此处记录——向上传播到 HeadlessRunner 统一处理。
                if (ex is HeadlessFatalException)
                {
                    Stop();
                    throw;
                }
                AgentLog.Instance.Write("cli fatal: " + ex);
                Stop();
            }
        }

        #region Main loops

        /// <summary>
        /// 启动 VT 路径主循环：DA1 探测 → 备用屏 → SGR mouse → 主循环轮询。
        /// 调用前 <see cref="ITerminalSetup.TryPrepareVtInput"/> 必须已返回 true（VT-only）。
        /// VT 终端恢复由 RegisterVtCleanupHooks（AppDomain.UnhandledException /
        /// ProcessExit）的多钩子保障，finally 块负责禁用 mouse + 退出备用屏。
        /// ADR-0005：原 LoopStrategy 策略模式已删除，主循环逻辑直接内联（VT-only）。
        /// </summary>
        private void RunVtLoop()
        {
            _vtInput = new VtInputHandler(this, _terminalInput);
            _screen = new AgentCliVtScreen();
            RegisterVtCleanupHooks();

            try
            {
                // 进入备用屏 → 启用 SGR mouse
                _screen.EnterAlternateScreen();
                _vtInput.EnableSgrMouse();

                // resize 检测基线
                _lastWindowWidth = _screen.WindowWidth;
                _lastWindowHeight = _screen.WindowHeight;

                var token = StopToken;
                // 游戏进入 Quit/Error 后立即退出循环，避免 CLI 空转等待永远不会到来的输入。
                while (!token.IsCancellationRequested && !IsGameExited())
                {
                    if (console.ConsumeNeedFullRefresh())
                    {
                        _renderer.FlushBuffer();
                        _renderer.FullRefresh();
                        _countdown.Reset();
                        _buttons.SyncButtonState();
                        _buttons.RefreshButtonRegions(_vtInput, force: true);
                        continue;
                    }

                    // VT 输入轮询：有输入时连续读取并 Feed（不等待/不更新 countdown）；
                    // 无输入时检查超时、更新 countdown、短暂等待。
                    if (_vtInput.HasInputAvailable())
                    {
                        int b = _vtInput.ReadByte();
                        if (b >= 0)
                            _vtInput.Feed((byte)b);
                    }
                    else
                    {
                        if (HandleTimeout())
                            continue;
                        _countdown.Update();
                        token.WaitHandle.WaitOne(PollIntervalMs);
                    }

                    // 末尾刷新：resize 检测 + FlushBuffer + SyncButtonState + RefreshButtonRegions
                    if (CheckResize())
                    {
                        _renderer.FullRefresh();
                        _buttons.RefreshButtonRegions(_vtInput, force: true);
                    }

                    _renderer.FlushBuffer();
                    _buttons.SyncButtonState();
                    _buttons.RefreshButtonRegions(_vtInput, force: false);
                }
            }
            finally
            {
                // 严格顺序：禁用 SGR mouse → 恢复 input mode → 退出备用屏
                // VtInput.Dispose 负责前两步，VtScreen.Dispose 负责第三步
                CleanupVt();
            }
        }

        /// <summary>游戏是否已进入终止状态（Quit/Error），用于主循环退出判定。</summary>
        private bool IsGameExited() =>
            console.State is ConsoleState.Quit or ConsoleState.Error;

        /// <summary>
        /// 超时处理（TINPUT）。返回 true 表示已处理（调用方 continue），false 表示未超时。
        /// ADR-0005：VT-only 后 _vtInput 必非 null，移除原可空参数。
        /// </summary>
        private bool HandleTimeout()
        {
            var timeoutMs = console.InputTimeoutMs;
            if (!timeoutMs.HasValue || timeoutMs.Value > 0) return false;

            _countdown.Overwrite(console.TimeUpMessage ?? "");
            _countdown.Reset();
            ClearInputBuffer();
            console.SubmitTimeout();
            _renderer.FlushBuffer();
            _buttons.SyncButtonState();
            _buttons.RefreshButtonRegions(_vtInput, force: false);
            return true;
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
            catch (Exception ex) { AgentLog.Instance.Write("vt cleanup hook registration failed: " + ex); }
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
            catch (Exception ex) { AgentLog.Instance.Write("vt input dispose failed: " + ex); }

            try { _screen?.Dispose(); }
            catch (Exception ex) { AgentLog.Instance.Write("vt screen dispose failed: " + ex); }

            _vtInput = null;
            _screen = null;
        }

        #endregion

        #region Keyboard / character input

        internal void ProcessChar(char ch)
        {
            if (ch == '\r' || ch == '\n')
            {
                EraseInputLine();
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
                EraseInputLine();
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
                EraseInputLine();
                _buf.Clear();
            }
        }

        /// <summary>擦除终端上当前输入行的显示内容（不含缓冲区清除）。</summary>
        private void EraseInputLine()
        {
            WriteOutput("\r" + new string(' ', _buf.Length) + "\r", false);
        }

        private static void WriteOutput(string text, bool newLine = true)
        {
            if (newLine) Console.WriteLine(text);
            else Console.Write(text);
        }

        protected override void OnInputRejected(string reason)
        {
            // 与 winforms 行为一致：静默忽略无效输入，不向终端输出提示
        }
    }
}
