using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MinorShift.Emuera.Runtime;
using MinorShift.Emuera.UI;
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

        internal EmueraConsole GameConsole => console;

        public AgentCliProtocol(EmueraConsole console, IConsoleUI ui)
            : base(console, ui)
        {
            _ansiEnabled = WindowsConsoleHelper.AnsiEnabled || !OperatingSystem.IsWindows();
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

        internal override Task<string?> GetInitialTurnAsync() => Task.FromResult<string?>(null);
        internal override Task<string?> StepAsync(string input) => Task.FromResult<string?>(null);

        internal void RunCliLoop()
        {
            // 顶层异常边界：覆盖 HandleTimeout / ProcessChar / DispatchMouseClick /
            // DispatchMouseMiss / Initialize 等所有调 RunEmueraProgram 的路径。
            // 脚本运行期异常一律 fatal——Process 内部状态不可逆，恢复无意义。
            // VT 终端恢复由 RegisterVtCleanupHooks（AppDomain.UnhandledException /
            // ProcessExit）的多钩子保障，finally 块只负责 VT 路径。
            try
            {
                _renderer.FlushBuffer();
                if (Console.IsInputRedirected)
                    RunAgentLoop(new PipeLoopStrategy(this, Console.In));
                else if (!TryRunVtLoop())
                    RunAgentLoop(new ConsoleKeyLoopStrategy(this));
                _renderer.FlushBuffer();
            }
            catch (Exception ex)
            {
                AgentLog.Instance.Write("cli fatal: " + ex);
                try { Console.Error.WriteLine($"[fatal] {ex.Message}"); } catch { }
                Stop();
            }
        }

        #region Main loops

        /// <summary>
        /// 尝试启动 VT 路径：DA1 探测 → 备用屏 → SGR mouse → 主循环轮询。
        /// DA1 探测失败时返回 false，调用方走降级路径。
        /// </summary>
        private bool TryRunVtLoop()
        {
            _vtInput = WindowsVtInput.TryCreate(this);
            if (_vtInput == null)
                return false;

            _screen = new AgentCliVtScreen();
            RegisterVtCleanupHooks();

            try
            {
                // 进入备用屏 → 启用 SGR mouse
                _screen.EnterAlternateScreen();
                _vtInput.EnableSgrMouse();

                RunAgentLoop(new VtLoopStrategy(this));
            }
            finally
            {
                // 严格顺序：禁用 SGR mouse → 恢复 input mode → 退出备用屏
                // VtInput.Dispose 负责前两步，VtScreen.Dispose 负责第三步
                CleanupVt();
            }

            return true;
        }

        /// <summary>
        /// 通用主循环模板：处理 NeedFullRefresh、末尾刷新，输入读取与超时/等待策略由 <paramref name="strategy"/> 注入。
        /// VT 与非 VT 路径共享此模板，差异通过策略封装。
        /// </summary>
        private void RunAgentLoop(LoopStrategy strategy)
        {
            var token = StopToken;
            strategy.Initialize();

            // 游戏进入 Quit/Error 后立即退出循环，避免 CLI 空转等待永远不会到来的输入。
            // 顶部检查顺带覆盖 Initialize 失败（state 已是 Error）的场景。
            while (!token.IsCancellationRequested && !IsGameExited())
            {
                if (console.ConsumeNeedFullRefresh())
                {
                    _renderer.FlushBuffer();
                    _renderer.FullRefresh();
                    _countdown.Reset();
                    _buttons.SyncButtonState();
                    strategy.RefreshButtonRegionsAfterRefresh(force: true);
                    continue;
                }

                if (strategy.Poll(token) == PollOutcome.Continue)
                    continue;

                if (strategy.CheckResize())
                {
                    _renderer.FullRefresh();
                    strategy.RefreshButtonRegionsAfterRefresh(force: true);
                }

                _renderer.FlushBuffer();
                _buttons.SyncButtonState();
                strategy.RefreshButtonRegionsAfterRefresh(force: false);
            }
        }

        /// <summary>游戏是否已进入终止状态（Quit/Error），用于主循环退出判定。</summary>
        private bool IsGameExited() =>
            console.State is ConsoleState.Quit or ConsoleState.Error;

        /// <summary>
        /// 超时处理（TINPUT）。返回 true 表示已处理（调用方 continue），false 表示未超时。
        /// vtInput 为 null 时跳过 RefreshButtonRegions（非 VT 路径）。
        /// </summary>
        private bool HandleTimeout(AgentCliVtInput? vtInput)
        {
            var timeoutMs = console.InputTimeoutMs;
            if (!timeoutMs.HasValue || timeoutMs.Value > 0) return false;

            _countdown.Overwrite(console.TimeUpMessage ?? "");
            _countdown.Reset();
            ClearInputBuffer();
            console.SubmitTimeout();
            _renderer.FlushBuffer();
            _buttons.SyncButtonState();
            _buttons.RefreshButtonRegions(vtInput, force: false);
            return true;
        }

        /// <summary>策略轮询结果。</summary>
        private enum PollOutcome
        {
            /// <summary>需要模板执行末尾刷新（CheckResize + FlushBuffer + SyncButtonState + RefreshButtonRegions）。</summary>
            NeedTailRefresh,
            /// <summary>策略已自行完成本轮所有处理（含末尾刷新或超时），模板直接 continue。</summary>
            Continue,
        }

        /// <summary>
        /// 主循环策略基类：封装 VT/非 VT 路径的输入读取、超时检查、countdown/等待时机差异。
        /// </summary>
        private abstract class LoopStrategy
        {
            protected readonly AgentCliProtocol _owner;
            protected LoopStrategy(AgentCliProtocol owner) { _owner = owner; }

            /// <summary>循环开始前的初始化（如 resize 检测基线）。</summary>
            public abstract void Initialize();
            /// <summary>
            /// 处理一轮输入读取、超时检查、countdown 更新与等待。
            /// 返回 <see cref="PollOutcome.NeedTailRefresh"/> 时模板执行末尾刷新；
            /// 返回 <see cref="PollOutcome.Continue"/> 时模板直接 continue。
            /// </summary>
            public abstract PollOutcome Poll(System.Threading.CancellationToken token);
            /// <summary>检测终端尺寸变化，返回 true 时模板触发 FullRefresh。</summary>
            public virtual bool CheckResize() => false;
            /// <summary>在 NeedFullRefresh/超时/末尾刷新后同步 VT 鼠标命中区域。非 VT 路径为空操作。</summary>
            public virtual void RefreshButtonRegionsAfterRefresh(bool force) { }
        }

        /// <summary>VT 路径策略：有输入时连续处理（不等待/不更新 countdown），无输入时检查超时并等待；末尾由模板刷新。</summary>
        private sealed class VtLoopStrategy : LoopStrategy
        {
            public VtLoopStrategy(AgentCliProtocol owner) : base(owner) { }

            public override void Initialize()
            {
                var screen = _owner._screen!;
                _owner._lastWindowWidth = screen.WindowWidth;
                _owner._lastWindowHeight = screen.WindowHeight;
            }

            public override PollOutcome Poll(System.Threading.CancellationToken token)
            {
                var vtInput = _owner._vtInput!;
                if (vtInput.HasInputAvailable())
                {
                    int b = vtInput.ReadByte();
                    if (b >= 0)
                        vtInput.Feed((byte)b);
                }
                else
                {
                    if (_owner.HandleTimeout(vtInput))
                        return PollOutcome.Continue;
                    _owner._countdown.Update();
                    token.WaitHandle.WaitOne(PollIntervalMs);
                }
                return PollOutcome.NeedTailRefresh;
            }

            public override bool CheckResize() => _owner.CheckResize();

            public override void RefreshButtonRegionsAfterRefresh(bool force)
                => _owner._buttons.RefreshButtonRegions(_owner._vtInput, force);
        }

        /// <summary>非 VT 路径策略：每轮检查超时、更新 countdown、读取按键、末尾刷新与等待均在 Poll 内完成。</summary>
        private sealed class ConsoleKeyLoopStrategy : LoopStrategy
        {
            public ConsoleKeyLoopStrategy(AgentCliProtocol owner) : base(owner) { }

            public override void Initialize() { }

            public override PollOutcome Poll(System.Threading.CancellationToken token)
            {
                if (_owner.HandleTimeout(null))
                    return PollOutcome.Continue;
                _owner._countdown.Update();
                if (Console.KeyAvailable)
                    _owner.ProcessKey(Console.ReadKey(true));
                _owner._renderer.FlushBuffer();
                _owner._buttons.SyncButtonState();
                token.WaitHandle.WaitOne(PollIntervalMs);
                return PollOutcome.Continue;
            }
        }

        /// <summary>stdin 管道路径策略：逐行读取并喂给 ProcessChar，EOF 时停止。
        /// 不处理 TINPUT 超时（管道模式无可靠 timeout），与 JSONL 管道行为一致。
        /// NeedFullRefresh 由模板统一处理；输入处理完返回 Continue，跳过模板末尾刷新。</summary>
        private sealed class PipeLoopStrategy : LoopStrategy
        {
            private readonly TextReader _input;

            public PipeLoopStrategy(AgentCliProtocol owner, TextReader input) : base(owner)
            {
                _input = input;
            }

            public override void Initialize() { }

            public override PollOutcome Poll(System.Threading.CancellationToken token)
            {
                string? line = _input.ReadLine();
                if (line == null)
                {
                    _owner.Stop();
                    return PollOutcome.Continue;
                }

                foreach (char ch in line)
                    _owner.ProcessChar(ch);
                _owner.ProcessChar('\r');
                _owner._renderer.FlushBuffer();
                return PollOutcome.Continue;
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

        protected override void OnInputRejected(string reason)
        {
            // 与 winforms 行为一致：静默忽略无效输入，不向终端输出提示
        }
    }
}
