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
    /// <summary>Scroll Mode 滚动热键动作（ADR-0006 Issue 003）。</summary>
    internal enum ScrollAction
    {
        Home,
        End,
        PageUp,
        PageDown,
    }

    internal sealed class AgentCliProtocol : AgentProtocolBase, IVtHost
    {
        private readonly StringBuilder _buf = new();

        // VT 模式状态（DA1 探测通过后非 null）
        private AgentCliVtScreen? _screen;
        private VtInputHandler? _vtInput;
        private readonly ITerminalInput _terminalInput;
        private readonly ITerminalSetup _terminalSetup;
        private bool _vtCleanupDone;
        private bool _vtHooksRegistered;

        // ADR-0009：scroll 状态由 ScrollController 持有（纯逻辑，构造时占位，RunVtLoop 内 UpdateVisibleLines）。
        private readonly ScrollController _scroll;

        // resize 检测
        private int _lastWindowWidth = -1;
        private int _lastWindowHeight = -1;

        private readonly ButtonSelectionMode _buttons;
        private readonly CountdownRenderer _countdown;
        private readonly TerminalRenderer _renderer;
        // ADR-0006：Scroll Status Bar 渲染器，独立于 TerminalRenderer。
        private ScrollStatusBarRenderer? _scrollStatusBar;

        // ADR-0007：CLI 模式 TINPUT 挂钟计时。记录进入 WaitInput 状态的时刻。
        // null 表示不在 WaitInput 或计时已失效。genericTimer stopwatch 在 CLI 不启动，改用此字段。
        private DateTime? _waitInputEnteredAt;

        /// <summary>是否在 primitive 输入等待状态。ADR-0009：通过 IVtHost 暴露给 VtInputHandler 门卫。</summary>
        bool IVtHost.IsWaitingPrimitive => console.IsWaitingPrimitive;

        /// <summary>ADR-0010：primitive 键盘输入分发，委托给 EmueraConsole。</summary>
        void IVtHost.PressPrimitiveKey(int keycode, int keydata, int keymod)
            => console.PressPrimitiveKey(keycode, keydata, keymod);

        /// <summary>ADR-0010：primitive 鼠标输入分发，委托给 EmueraConsole。</summary>
        void IVtHost.InputMouseKey(int type, int result1, int result2, int result3, int result4, long result5)
            => console.InputMouseKey(type, result1, result2, result3, result4, result5);

        /// <summary>当前 Scroll Offset（IVtHost 门卫读取）。ADR-0009：从 ScrollController 读。</summary>
        int IVtHost.ScrollOffset => _scroll.ScrollOffset;

        public AgentCliProtocol(EmueraConsole console, IConsoleUI ui, ITerminalSetup terminalSetup, ITerminalInput terminalInput)
            : base(console, ui)
        {
            _terminalSetup = terminalSetup;
            _terminalInput = terminalInput;
            // ADR-0009：ScrollController 构造占位（visibleLines=1），RunVtLoop 内 UpdateVisibleLines(WindowHeight-2)。
            _scroll = new ScrollController(scrollVisibleLines: 1);
            // ADR-0005：VT-only 后 ANSI 始终可用，_ansiEnabled 字段已删除。
            // ADR-0005 Issue 4：删除降级渲染分支后 _cursor/_requestFullRefresh 字段已移除，
            // 渲染器假设 _screen 在 VT 主循环内必非 null。
            _renderer = new TerminalRenderer(console, _scroll, () => _screen);
            _buttons = new ButtonSelectionMode(
                console,
                _scroll,
                () => _screen,
                input => DispatchInput(input),
                ClearInputBuffer);
            _countdown = new CountdownRenderer(console, () => _scroll.ScrollOffset, () => _screen);
            // ADR-0006：auto-follow 回调——FlushBuffer 检测到新行/ClearOp 且 offset>0 时
            // 归零 offset + FullRefresh 后调用，同步状态栏/倒计时/按钮区域。
            _renderer.OnScrollAutoFollow = () =>
            {
                _scrollStatusBar?.Render(0);
                _countdown.Reset();
                _buttons.RefreshButtonRegions(_vtInput!, force: true);
            };
            // ADR-0009：用户主动滚动（DispatchWheel/Scroll 路径）→ ScrollChanged 事件 → 4 步渲染反应。
            _scroll.ScrollChanged += OnScrollChanged;
        }

        /// <summary>ScrollChanged 订阅者：offset 变化后做 4 步渲染（FullRefresh → statusbar → countdown → buttons）。</summary>
        private void OnScrollChanged(int newOffset)
        {
            _renderer.FullRefresh();
            _scrollStatusBar?.Render(newOffset);
            // offset 从 >0 转回 0 时重新探测倒计时行位置（Scroll Mode 期间 CursorTop-1 失效）。
            if (newOffset == 0)
                _countdown.Reset();
            _buttons.RefreshButtonRegions(_vtInput!, force: true);
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

                // ADR-0005 Issue 4：删除外层 FlushBuffer——RunVtLoop 的循环末尾已刷新最终状态，
                // CleanupVt() 在 finally 中将 _screen 置 null 后再 FlushBuffer 会走到已删除的降级分支。
                RunVtLoop();
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
            _scrollStatusBar = new ScrollStatusBarRenderer(() => _screen);
            // ADR-0009：screen 就绪后更新 ScrollController 的视口高度（Scroll Mode = WindowHeight - 2）。
            _scroll.UpdateVisibleLines(Math.Max(1, _screen.WindowHeight - 2));
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
                        // ADR-0006 Issue 004：ConsumeNeedFullRefresh 归零 Scroll Offset 后再 FullRefresh。
                        ResetScrollIfActive();
                        // ADR-0007：auto-follow 路径触发后清空计时上下文，避免误触发上一轮超时。
                        _waitInputEnteredAt = null;
                        _renderer.FlushBuffer();
                        _renderer.FullRefresh();
                        _countdown.Reset();
                        _buttons.SyncButtonState();
                        _buttons.RefreshButtonRegions(_vtInput, force: true);
                        continue;
                    }

                    // ADR-0007：维护 WaitInputEnteredAt 生命周期。
                    // InputTimeoutMs == null 表示不在 WaitInput 或无超时设置 → 清空。
                    // 进入 WaitInput 且有超时但未记录 → 记录当前时刻。
                    var timeoutMs = console.InputTimeoutMs;
                    if (!timeoutMs.HasValue)
                    {
                        _waitInputEnteredAt = null;
                    }
                    else if (!_waitInputEnteredAt.HasValue && console.InputTimelimit > 0)
                    {
                        _waitInputEnteredAt = DateTime.UtcNow;
                    }

                    // ADR-0007：超时检查必须在输入处理之前，确保 poll 每轮都检查挂钟 elapsed，
                    // 不会被连续的输入读取饿死。
                    if (HandleTimeout())
                        continue;

                    // VT 输入轮询：有真实字节时连续读取并 Feed；无字节时更新 countdown 并短暂等待。
                    // ADR-0007：WindowsTerminalInput 用后台线程读 stdin 入队，HasInputAvailable
                    // 检查队列而非 WaitForSingleObject，避免 phantom 事件误报导致 ReadFile 阻塞。
                    if (_vtInput.HasInputAvailable())
                    {
                        int b = _vtInput.ReadByte();
                        if (b >= 0)
                        {
                            _vtInput.Feed((byte)b);
                        }
                    }
                    else
                    {
                        // ADR-0007：传入挂钟 elapsed 给 CountdownRenderer，绕过 stopwatch。
                        long elapsedMs = _waitInputEnteredAt.HasValue
                            ? (long)(DateTime.UtcNow - _waitInputEnteredAt.Value).TotalMilliseconds
                            : 0;
                        _countdown.Update(elapsedMs);
                        token.WaitHandle.WaitOne(PollIntervalMs);
                    }

                    // 末尾刷新：resize 检测 + FlushBuffer + SyncButtonState + RefreshButtonRegions
                    if (CheckResize())
                    {
                        // ADR-0006 Issue 004 + ADR-0009：resize 后更新 ScrollController 视口高度 + 钳位 offset。
                        // Clamp 若改变 offset 会 raise ScrollChanged → OnScrollChanged 做 4 步渲染。
                        // offset 不变时（Clamp no-op）不 raise，此处仍需 FullRefresh + statusbar + buttons（WindowHeight 变了）。
                        _scroll.UpdateVisibleLines(Math.Max(1, _screen!.WindowHeight - 2));
                        int oldOffset = _scroll.ScrollOffset;
                        _scroll.Clamp(console.DisplayLineList.Count);
                        // ADR-0007：auto-follow 路径触发后清空计时上下文。
                        _waitInputEnteredAt = null;
                        if (oldOffset == _scroll.ScrollOffset)
                        {
                            // offset 未变，OnScrollChanged 未触发——仍需重绘（resize 改了布局）。
                            _renderer.FullRefresh();
                            _scrollStatusBar?.Render(_scroll.ScrollOffset);
                            _buttons.RefreshButtonRegions(_vtInput, force: true);
                        }
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
        /// ADR-0006：SubmitTimeout 产生新输出后走 FlushBuffer 新行路径，auto-follow 归零 offset
        /// （由 TerminalRenderer.FlushBuffer 内部处理，此处无需显式 reset）。
        /// </summary>
        private bool HandleTimeout()
        {
            // ADR-0007：CLI 模式用挂钟计时，绕过失效的 genericTimer stopwatch。
            if (!_waitInputEnteredAt.HasValue) return false;
            long timelimit = console.InputTimelimit;
            if (timelimit <= 0) return false;
            var elapsed = (long)(DateTime.UtcNow - _waitInputEnteredAt.Value).TotalMilliseconds;
            if (elapsed < timelimit) return false;

            _countdown.Overwrite(console.TimeUpMessage ?? "");
            _countdown.Reset();
            ClearInputBuffer();
            console.SubmitTimeout();
            // SubmitTimeout 推进脚本后立即清空计时上下文，避免残留触发下一轮
            _waitInputEnteredAt = null;
            _renderer.FlushBuffer();
            _buttons.SyncButtonState();
            _buttons.RefreshButtonRegions(_vtInput!, force: false);
            return true;
        }

        #endregion

        #region Scroll dispatch (ADR-0006)

        /// <summary>滚轮事件分发（cb=64/65 → delta=±3）。由 VtInputHandler.OnMouseEvent 调用。
        /// ADR-0009：算术委托给 ScrollController，offset 变化时 ScrollChanged 事件触发 OnScrollChanged 做 4 步渲染。</summary>
        void IVtHost.DispatchWheel(int delta)
        {
            if (_screen == null) return;
            _scroll.ScrollBy(delta, console.DisplayLineList.Count);
        }

        /// <summary>键盘滚动热键分发（PgUp/PgDn/Home/End）。由 VtInputHandler.OnKeyEvent 调用。
        /// ADR-0009：算术委托给 ScrollController，offset 变化时 ScrollChanged 事件触发 OnScrollChanged 做 4 步渲染。
        /// PageUp/PageDown delta 用正常模式 visibleLines（WindowHeight - 1），maxOffset 用 Scroll Mode visibleLines（WindowHeight - 2）。</summary>
        void IVtHost.DispatchScroll(ScrollAction action)
        {
            var screen = _screen;
            if (screen == null) return;
            int lineCount = console.DisplayLineList.Count;
            // ADR-0009：PageUp/PageDown delta 用动态 visibleLines——offset==0 用正常模式（W-1），
            // offset>0 用 Scroll Mode（W-2）。保持与原 AgentCliVtScreen.GetVisibleLines() 行为零变化。
            int pageLines = _scroll.IsScrollMode
                ? Math.Max(1, screen.WindowHeight - 2)
                : Math.Max(1, screen.WindowHeight - 1);

            switch (action)
            {
                case ScrollAction.Home:
                    // 滚到顶：ScrollTo(int.MaxValue) 钳到 max。
                    _scroll.ScrollTo(int.MaxValue, lineCount);
                    break;
                case ScrollAction.End:
                    // 回底退出 Scroll Mode。
                    _scroll.ScrollTo(0, lineCount);
                    break;
                case ScrollAction.PageUp:
                    _scroll.ScrollBy(pageLines, lineCount);
                    break;
                case ScrollAction.PageDown:
                    _scroll.ScrollBy(-pageLines, lineCount);
                    break;
                default:
                    return;
            }
        }

        /// <summary>ConsumeNeedFullRefresh 路径调用：若 offset>0 则清旧状态栏 + 归零。
        /// ClearStatusBar 必须在 Reset 之前调用——此时屏幕仍是 offset>0 旧布局，
        /// row consoleHeight-2 是旧状态栏行；归零后的 FullRefresh 会重绘 offset=0 布局。
        /// ADR-0009：Reset() 静默不 raise 事件（系统归零由调用方编排渲染）。</summary>
        private void ResetScrollIfActive()
        {
            if (_scroll.ScrollOffset > 0)
            {
                _scrollStatusBar?.ClearStatusBar();
                _scroll.Reset();
            }
        }

        #endregion

        #region VT lifecycle helpers

        /// <summary>VT 路径请求退出（Ctrl+C / CancelKeyPress 触发）。</summary>
        void IVtHost.RequestExit() => Stop();

        /// <summary>VT 解析器输出的 ConsoleKeyInfo 入口，复用现有 ProcessKey 分支。</summary>
        void IVtHost.ProcessKeyFromVt(ConsoleKeyInfo key) => ProcessKey(key);

        /// <summary>检测终端尺寸变化，返回 true 时调用方触发 FullRefresh。ADR-0005 Issue 4：_screen 在 VT 主循环内必非 null。</summary>
        private bool CheckResize()
        {
            int w = _screen!.WindowWidth;
            int h = _screen!.WindowHeight;
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

        /// <summary>幂等清理：禁用 SGR mouse → 恢复 input mode → 退出备用屏。
        /// ADR-0006：退出前恢复光标可见（Scroll Mode 可能隐藏了光标）。</summary>
        private void CleanupVt()
        {
            if (_vtCleanupDone) return;
            _vtCleanupDone = true;

            try { _scrollStatusBar?.RestoreCursor(); }
            catch (Exception ex) { AgentLog.Instance.Write("scroll status bar restore cursor failed: " + ex); }

            try { _vtInput?.Dispose(); }
            catch (Exception ex) { AgentLog.Instance.Write("vt input dispose failed: " + ex); }

            try { _screen?.Dispose(); }
            catch (Exception ex) { AgentLog.Instance.Write("vt screen dispose failed: " + ex); }

            _vtInput = null;
            _screen = null;
            _scrollStatusBar = null;
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

        void IVtHost.DispatchMouseClick(ConsoleButtonString btn)
        {
            if (console.State != ConsoleState.WaitInput) return;

            // 鼠标点击退出按钮选择模式（若有），并执行点击命中
            if (_buttons.IsButtonMode) _buttons.ExitButtonMode();

            string input = btn.IsInteger ? btn.Input.ToString() : btn.Inputs;
            DispatchInput(input);
        }

        void IVtHost.DispatchMouseMiss()
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
