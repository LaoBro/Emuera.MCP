using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MinorShift.Emuera.Runtime;
using MinorShift.Emuera.Runtime.Config;
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

    internal sealed class AgentCliProtocol : AgentProtocolBase, IVtHost, IInputTimer
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

        private readonly ButtonSelectionMode _buttons;
        private readonly CountdownRenderer _countdown;
        private readonly TerminalRenderer _renderer;
        private readonly CliRedrawCoordinator _redraw;
        // Phase 4-2：CLI 自有 DisplayState（Q7）。AgentCliProtocol 构造持有，FlushBuffer 帧级 TryUpdate。
        // 与 server 路径的 DisplayState（Session 持有、BuildTurn 回合级 TryUpdate）独立。
        private readonly DisplayState _displayState;
        // ADR-0006：Scroll Status Bar 渲染器，独立于 TerminalRenderer。
        private ScrollStatusBarRenderer? _scrollStatusBar;

        private CliGameLoop _gameLoop = null!;

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

        public AgentCliProtocol(EmueraConsole console, IConsoleUI ui, ITerminalSetup terminalSetup, ITerminalInput terminalInput, ConfigData configData)
            : base(console, ui)
        {
            _terminalSetup = terminalSetup;
            _terminalInput = terminalInput;
            // ADR-0009：ScrollController 构造占位（visibleLines=1），RunVtLoop 内 UpdateVisibleLines(WindowHeight-2)。
            _scroll = new ScrollController(scrollVisibleLines: 1);
            // Phase 4-2（Q7/R4）：CLI 自有 DisplayState——从 ConfigData 取 defaultFontName，
            // 与 Session.cs:83 同一取值表达式。TerminalRenderer + ButtonSelectionMode 均注入此实例。
            var defaultFontName = configData.GetConfigValue<string>(ConfigCode.FontName) ?? "";
            _displayState = new DisplayState(console, defaultFontName);
            // ADR-0005：VT-only 后 ANSI 始终可用，_ansiEnabled 字段已删除。
            // ADR-0005 Issue 4：删除降级渲染分支后 _cursor/_requestFullRefresh 字段已移除，
            // 渲染器假设 _screen 在 VT 主循环内必非 null。
            // Phase 4-1：TerminalRenderer 数据源换成 DisplayState.Current.lines。
            _renderer = new TerminalRenderer(console, _scroll, null, _displayState);
            // Phase 3-3b：ButtonSelectionMode 注入 DisplayState，RefreshButtonRegionsFromSnapshot 启用。
            _buttons = new ButtonSelectionMode(
                console,
                _scroll,
                () => _screen,
                input => DispatchInput(input),
                ClearInputBuffer,
                _displayState);
            _countdown = new CountdownRenderer(console, () => _scroll.ScrollOffset, () => _screen, () => _renderer.LastDrawnRows);
            _redraw = new CliRedrawCoordinator(_renderer, _scroll, () => _scrollStatusBar, _countdown, _buttons, () => _vtInput);
            _renderer.OnScrollAutoFollow = () => _redraw.Redraw(RedrawKind.ChromeOnly, true);
        }

        private void CreateGameLoop()
        {
            _gameLoop = new CliGameLoop(
                console,
                this,
                _screen!,
                _scroll,
                _scrollStatusBar,
                _redraw,
                _vtInput!,
                _countdown,
                _screen!.WindowWidth,
                _screen!.WindowHeight);
        }

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
            _renderer.Screen = _screen;
            _scrollStatusBar = new ScrollStatusBarRenderer(() => _screen);
            _scroll.UpdateVisibleLines(Math.Max(1, _screen.WindowHeight - 2));
            RegisterVtCleanupHooks();
            CreateGameLoop();

            try
            {
                _screen.EnterAlternateScreen();
                _vtInput.EnableSgrMouse();
                _gameLoop.RunLoop(StopToken);
            }
            finally
            {
                CleanupVt();
            }
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

        #endregion

        #region VT lifecycle helpers

        /// <summary>VT 路径请求退出（Ctrl+C / CancelKeyPress 触发）。</summary>
        void IVtHost.RequestExit() => Stop();

        /// <summary>VT 解析器输出的 ConsoleKeyInfo 入口，复用现有 ProcessKey 分支。</summary>
        void IVtHost.ProcessKeyFromVt(ConsoleKeyInfo key) => ProcessKey(key);

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
            _renderer.Screen = null;
            _scrollStatusBar = null;
        }

        #endregion

        #region IInputTimer

        bool IInputTimer.IsWaitingInput => console.State == ConsoleState.WaitInput;
        long IInputTimer.InputTimelimit => console.InputTimelimit;
        string? IInputTimer.TimeUpMessage => console.TimeUpMessage;
        void IInputTimer.SubmitTimeout() => console.SubmitTimeout();
        void IInputTimer.ClearInputBuffer() => ClearInputBuffer();

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
                    Echo("\b \b");
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
                Echo(ch.ToString());
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

        void IVtHost.DispatchMouseClick(object value, bool isInteger)
        {
            if (console.State != ConsoleState.WaitInput) return;

            // 鼠标点击退出按钮选择模式（若有），并执行点击命中
            if (_buttons.IsButtonMode) _buttons.ExitButtonMode();

            // Phase 3-3：value-based dispatch——isInteger 时 value 是 long 装箱，
            // 否则是 string。Generation 校验由服务端 ConsoleInputHandler 兜底。
            string input = isInteger ? value.ToString()! : (string)value;
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
            Echo("\r" + new string(' ', _buf.Length) + "\r");
        }

        /// <summary>输入回显：优先经 VT 备用屏（与光标/滚动状态一致），屏幕不可用时降级直写 Console。</summary>
        private void Echo(string text)
        {
            if (_screen != null) _screen.WriteRaw(text);
            else Console.Write(text);
        }

        protected override void OnInputRejected(string reason)
        {
            // 与 winforms 行为一致：静默忽略无效输入，不向终端输出提示
        }
    }
}
