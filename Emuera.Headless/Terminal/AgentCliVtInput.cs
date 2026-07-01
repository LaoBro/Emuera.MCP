using System;
using System.Threading;
using MinorShift.Emuera.UI.Game;

namespace MinorShift.Emuera.GameView
{
    /// <summary>
    /// VT 输入后端抽象基类：raw stdin 读取 + VT 序列解析 + SGR mouse + 按钮区域追踪。
    /// 坐标全部为 viewport 坐标（0-based），备用屏下与 SGR mouse 的 Cy-1 使用同一坐标空间。
    /// 平台分支由子类实现 <see cref="HasInputAvailable"/> / <see cref="ReadByte"/>。
    /// </summary>
    internal abstract class AgentCliVtInput : IDisposable
    {
        private readonly AgentCliProtocol _host;
        private readonly ButtonRegionTracker _tracker = new();
        private readonly VtParser _parser;
        protected bool _disposed;

        protected AgentCliVtInput(AgentCliProtocol host)
        {
            _host = host;
            _parser = new VtParser(this);
        }

        /// <summary>非阻塞查询 stdin 是否有可用字节。</summary>
        internal abstract bool HasInputAvailable();

        /// <summary>从 stdin 读取单个 raw byte；无数据时返回 -1。</summary>
        internal abstract int ReadByte();

        /// <summary>启用 SGR mouse tracking（ESC[?1000h + ESC[?1006h）。</summary>
        internal abstract void EnableSgrMouse();

        /// <summary>禁用 SGR mouse tracking（ESC[?1006l + ESC[?1000l）。</summary>
        internal abstract void DisableSgrMouse();

        /// <summary>将一个 raw byte 喂入 VT 解析器，解析结果直接派发到 host。</summary>
        internal void Feed(byte b) => _parser.Feed(b);

        #region Button regions

        internal void ClearRegions() => _tracker.Clear();

        internal void RecordLineRegions(string formattedLine, int viewportRow, ConsoleButtonString[]? buttons, long currentGeneration)
            => _tracker.RecordLineRegions(formattedLine, viewportRow, buttons, currentGeneration);

        private ConsoleButtonString? HitTest(int row, int col) => _tracker.HitTest(row, col);

        #endregion

        #region Event dispatch (called by VtParser)

        internal void OnKeyEvent(ConsoleKey key, char ch)
        {
            // Ctrl+C：raw mode 下 Console.CancelKeyPress 不可靠，手动检测 0x03
            if (ch == '\x03')
            {
                Log.Write("[vt-input] Ctrl+C (0x03) detected, requesting exit");
                _host.RequestExit();
                return;
            }

            // 控制字符映射到 ConsoleKey（UTF-8 路径产生的 ch 可能是 \r \b 等）
            if (key == 0)
            {
                if (ch == '\r') key = ConsoleKey.Enter;
                else if (ch == '\b' || ch == '\x7F') key = ConsoleKey.Backspace;
                else if (ch == '\x1b') key = ConsoleKey.Escape;
            }

            if (_host.GameConsole.IsWaitingPrimitive)
            {
                int keycode = (int)key;
                int keydata = (int)ch;
                _host.GameConsole.PressPrimitiveKey(keycode, keydata, 0);
                Log.Write($"[vt-input] primitive key={key} ch=0x{(int)ch:X4}");
                return;
            }

            var keyInfo = new ConsoleKeyInfo(ch, key, false, false, false);
            Log.Write($"[vt-input] key={key} ch=0x{(int)ch:X4}");
            _host.ProcessKeyFromVt(keyInfo);
        }

        internal void OnMouseEvent(int row, int col, int buttonCode, bool isPress)
        {
            if (_host.GameConsole.IsWaitingPrimitive)
            {
                DispatchPrimitiveMouseKey(row, col, buttonCode, isPress);
                return;
            }

            // 按钮点击模式：只处理左键按下
            if (!isPress || buttonCode != 0) return;

            var hit = HitTest(row, col);
            if (hit != null)
            {
                Log.Write($"[vt-input] mouse hit row={row} col={col}");
                _host.DispatchMouseClick(hit);
            }
            else
            {
                Log.Write($"[vt-input] mouse miss row={row} col={col}");
                _host.DispatchMouseMiss();
            }
        }

        private void DispatchPrimitiveMouseKey(int row, int col, int buttonCode, bool isPress)
        {
            // VT SGR mouse wheel: cb=64 (up), cb=65 (down)
            if (buttonCode == 64 || buttonCode == 65)
            {
                int delta = buttonCode == 64 ? -120 : 120;
                _host.GameConsole.InputMouseKey(2, delta, col, row, 0, 0);
                Log.Write($"[vt-input] primitive wheel delta={delta} row={row} col={col}");
                return;
            }

            if (!isPress) return;

            // VT SGR mouse buttons → Windows MouseButtons enum
            int windowsButton = buttonCode switch
            {
                0 => 0x100000,  // MouseButtons.Left
                1 => 0x400000,  // MouseButtons.Middle
                2 => 0x200000,  // MouseButtons.Right
                _ => 0
            };

            if (windowsButton == 0) return;

            _host.GameConsole.InputMouseKey(1, windowsButton, col, row, 0, 0);
            Log.Write($"[vt-input] primitive mouse btn={buttonCode} row={row} col={col}");
        }

        #endregion

        protected static readonly AgentLog Log = AgentLog.Instance;

        public abstract void Dispose();
    }

    /// <summary>
    /// Windows VT 输入后端：GetNumberOfConsoleInputEvents 非阻塞查询 + ReadFile raw bytes。
    /// DA1 探测 + VT input mode + SGR mouse 生命周期管理。
    /// </summary>
    internal sealed class WindowsVtInput : AgentCliVtInput
    {
        private readonly IntPtr _stdinHandle;
        private readonly uint _originalInputMode;
        private bool _inputModeSet;
        private bool _sgrMouseEnabled;

        private WindowsVtInput(AgentCliProtocol host, IntPtr stdinHandle, uint originalInputMode)
            : base(host)
        {
            _stdinHandle = stdinHandle;
            _originalInputMode = originalInputMode;
        }

        /// <summary>
        /// 工厂方法：执行 DA1 探测 + VT input mode 设置。
        /// 失败时返回 null（调用方走降级路径）。
        /// </summary>
        internal static WindowsVtInput? TryCreate(AgentCliProtocol host)
        {
            if (!OperatingSystem.IsWindows()) return null;
            if (Console.IsInputRedirected) return null;

            IntPtr stdin = Win32ConsoleInterop.GetStdHandle(Win32ConsoleInterop.STD_INPUT_HANDLE);
            if (stdin == IntPtr.Zero || stdin == Win32ConsoleInterop.INVALID_HANDLE_VALUE) return null;

            if (!Win32ConsoleInterop.GetConsoleMode(stdin, out uint originalMode))
            {
                Log.Write("[vt-input] GetConsoleMode failed");
                return null;
            }

            Log.Write($"[vt-input] original input mode: 0x{originalMode:X8}");

            // DA1 探测
            if (!ProbeDa1(stdin, originalMode))
            {
                Log.Write("[vt-input] DA1 probe failed, degrading");
                return null;
            }

            Log.Write("[vt-input] DA1 probe passed, setting VT input mode");

            // 设置 VT input mode
            uint newMode = (originalMode
                            | Win32ConsoleInterop.ENABLE_VIRTUAL_TERMINAL_INPUT
                            | Win32ConsoleInterop.ENABLE_EXTENDED_FLAGS
                            | Win32ConsoleInterop.ENABLE_WINDOW_INPUT
                            | Win32ConsoleInterop.ENABLE_MOUSE_INPUT)
                          & ~Win32ConsoleInterop.ENABLE_PROCESSED_INPUT
                          & ~Win32ConsoleInterop.ENABLE_ECHO_INPUT
                          & ~Win32ConsoleInterop.ENABLE_LINE_INPUT
                          & ~Win32ConsoleInterop.ENABLE_QUICK_EDIT_MODE;

            if (!Win32ConsoleInterop.SetConsoleMode(stdin, newMode))
            {
                Log.Write($"[vt-input] SetConsoleMode failed, target=0x{newMode:X8}");
                return null;
            }

            Log.Write($"[vt-input] VT input mode set: 0x{newMode:X8}");

            return new WindowsVtInput(host, stdin, originalMode) { _inputModeSet = true };
        }

        internal override bool HasInputAvailable()
        {
            return Win32ConsoleInterop.GetNumberOfConsoleInputEvents(_stdinHandle, out uint count) && count > 0;
        }

        internal override unsafe int ReadByte()
        {
            byte[] buf = new byte[1];
            fixed (byte* p = buf)
            {
                if (!Win32ConsoleInterop.ReadFile(_stdinHandle, (IntPtr)p, 1, out int read, IntPtr.Zero) || read == 0)
                    return -1;
            }
            return buf[0];
        }

        internal override void EnableSgrMouse()
        {
            TerminalCursor.TryWrite("\x1b[?1000h\x1b[?1006h");
            _sgrMouseEnabled = true;
            Log.Write("[vt-input] SGR mouse enabled (1000h + 1006h)");
        }

        internal override void DisableSgrMouse()
        {
            if (!_sgrMouseEnabled) return;
            TerminalCursor.TryWrite("\x1b[?1006l\x1b[?1000l");
            _sgrMouseEnabled = false;
            Log.Write("[vt-input] SGR mouse disabled");
        }

        public override void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            // 严格顺序：禁用 SGR mouse → 恢复 input mode
            // （退出备用屏由 AgentCliVtScreen.Dispose 负责，在 VtInput 之后调用）
            DisableSgrMouse();

            if (_inputModeSet && _stdinHandle != IntPtr.Zero && _stdinHandle != Win32ConsoleInterop.INVALID_HANDLE_VALUE)
            {
                Win32ConsoleInterop.SetConsoleMode(_stdinHandle, _originalInputMode);
                _inputModeSet = false;
                Log.Write($"[vt-input] restored input mode: 0x{_originalInputMode:X8}");
            }

            ClearRegions();
        }

        #region DA1 探测

        private static bool ProbeDa1(IntPtr stdin, uint originalMode)
        {
            // 临时开启 VT input mode 以接收 DA1 响应（响应通过 stdin 返回）
            uint probeMode = (originalMode | Win32ConsoleInterop.ENABLE_VIRTUAL_TERMINAL_INPUT)
                             & ~Win32ConsoleInterop.ENABLE_PROCESSED_INPUT
                             & ~Win32ConsoleInterop.ENABLE_ECHO_INPUT
                             & ~Win32ConsoleInterop.ENABLE_LINE_INPUT;
            if (!Win32ConsoleInterop.SetConsoleMode(stdin, probeMode))
            {
                Log.Write("[vt-input] failed to set probe input mode");
                return false;
            }

            // 发送 DA1 查询
            TerminalCursor.TryWrite("\x1b[c");
            Log.Write("[vt-input] sent DA1 query ESC[c");

            // 轮询 200ms 等待响应
            byte[] buffer = new byte[256];
            int totalRead = 0;
            var deadline = DateTime.UtcNow.AddMilliseconds(Da1TimeoutMs);

            while (DateTime.UtcNow < deadline)
            {
                if (Win32ConsoleInterop.GetNumberOfConsoleInputEvents(stdin, out uint count) && count > 0)
                {
                    int n = ReadRawBytes(stdin, buffer, totalRead, buffer.Length - totalRead);
                    if (n > 0)
                    {
                        totalRead += n;
                        if (ContainsDa1Response(buffer, totalRead))
                        {
                            Log.Write($"[vt-input] DA1 response received ({totalRead} bytes)");
                            // 恢复原始 mode，后续 SetupVtInputMode 会重新设置
                            Win32ConsoleInterop.SetConsoleMode(stdin, originalMode);
                            return true;
                        }
                    }
                }
                Thread.Sleep(5);
            }

            // 超时
            Log.Write($"[vt-input] DA1 probe timeout (read {totalRead} bytes)");
            Win32ConsoleInterop.SetConsoleMode(stdin, originalMode);
            return false;
        }

        private const int Da1TimeoutMs = 200;

        private static bool ContainsDa1Response(byte[] buffer, int length)
        {
            // DA1 响应格式: ESC[?...c 或 ESC[...c
            for (int i = 0; i < length - 1; i++)
            {
                if (buffer[i] == 0x1B && buffer[i + 1] == (byte)'[')
                {
                    for (int j = i + 2; j < length; j++)
                    {
                        if (buffer[j] == (byte)'c')
                            return true;
                        // DA1 响应参数只包含数字、;、?
                        if (buffer[j] != ';' && buffer[j] != '?' && (buffer[j] < '0' || buffer[j] > '9'))
                            break;
                    }
                }
            }
            return false;
        }

        #endregion

        #region raw bytes 读取

        private static unsafe int ReadRawBytes(IntPtr stdin, byte[] buffer, int offset, int count)
        {
            // 用 ReadFile 读取 raw bytes，不经过 .NET Console 内部缓冲
            // P/Invoke 签名必须用 IntPtr lpBuffer + unsafe fixed，不能用 Span<byte>
            fixed (byte* p = &buffer[offset])
            {
                if (!Win32ConsoleInterop.ReadFile(stdin, (IntPtr)p, count, out int read, IntPtr.Zero))
                    return 0;
                return read;
            }
        }

        #endregion
    }
}
