using System;
using System.Runtime.InteropServices;
using System.Text;
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
                s_log.Write("[vt-input] Ctrl+C (0x03) detected, requesting exit");
                _host.RequestExit();
                return;
            }

            // 控制字符映射到 ConsoleKey（UTF-8 路径产生的 ch 可能是 \r \b 等）
            if (key == 0)
            {
                if (ch == '\r') key = ConsoleKey.Enter;
                else if (ch == '\b') key = ConsoleKey.Backspace;
                else if (ch == '\x1b') key = ConsoleKey.Escape;
            }

            var keyInfo = new ConsoleKeyInfo(ch, key, false, false, false);
            s_log.Write($"[vt-input] key={key} ch=0x{(int)ch:X4}");
            _host.ProcessKeyFromVt(keyInfo);
        }

        internal void OnMouseEvent(int row, int col, bool isPress)
        {
            // 只处理左键按下（Cb==0, M 终结符）
            if (!isPress) return;

            var hit = HitTest(row, col);
            if (hit != null)
            {
                s_log.Write($"[vt-input] mouse hit row={row} col={col}");
                _host.DispatchMouseClick(hit);
            }
            else
            {
                s_log.Write($"[vt-input] mouse miss row={row} col={col}");
                _host.DispatchMouseMiss();
            }
        }

        #endregion

        protected static readonly AgentLog s_log = AgentLog.Instance;

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

            IntPtr stdin = GetStdHandle(STD_INPUT_HANDLE);
            if (stdin == IntPtr.Zero || stdin == INVALID_HANDLE_VALUE) return null;

            if (!GetConsoleMode(stdin, out uint originalMode))
            {
                s_log.Write("[vt-input] GetConsoleMode failed");
                return null;
            }

            s_log.Write($"[vt-input] original input mode: 0x{originalMode:X8}");

            // DA1 探测
            if (!ProbeDa1(stdin, originalMode))
            {
                s_log.Write("[vt-input] DA1 probe failed, degrading");
                return null;
            }

            s_log.Write("[vt-input] DA1 probe passed, setting VT input mode");

            // 设置 VT input mode
            uint newMode = (originalMode
                            | ENABLE_VIRTUAL_TERMINAL_INPUT
                            | ENABLE_EXTENDED_FLAGS
                            | ENABLE_WINDOW_INPUT
                            | ENABLE_MOUSE_INPUT)
                          & ~ENABLE_PROCESSED_INPUT
                          & ~ENABLE_ECHO_INPUT
                          & ~ENABLE_LINE_INPUT
                          & ~ENABLE_QUICK_EDIT_MODE;

            if (!SetConsoleMode(stdin, newMode))
            {
                s_log.Write($"[vt-input] SetConsoleMode failed, target=0x{newMode:X8}");
                return null;
            }

            s_log.Write($"[vt-input] VT input mode set: 0x{newMode:X8}");

            return new WindowsVtInput(host, stdin, originalMode) { _inputModeSet = true };
        }

        internal override bool HasInputAvailable()
        {
            return GetNumberOfConsoleInputEvents(_stdinHandle, out uint count) && count > 0;
        }

        internal override unsafe int ReadByte()
        {
            byte[] buf = new byte[1];
            fixed (byte* p = buf)
            {
                if (!ReadFile(_stdinHandle, (IntPtr)p, 1, out int read, IntPtr.Zero) || read == 0)
                    return -1;
            }
            return buf[0];
        }

        internal override void EnableSgrMouse()
        {
            TryWrite("\x1b[?1000h\x1b[?1006h");
            _sgrMouseEnabled = true;
            s_log.Write("[vt-input] SGR mouse enabled (1000h + 1006h)");
        }

        internal override void DisableSgrMouse()
        {
            if (!_sgrMouseEnabled) return;
            TryWrite("\x1b[?1006l\x1b[?1000l");
            _sgrMouseEnabled = false;
            s_log.Write("[vt-input] SGR mouse disabled");
        }

        public override void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            // 严格顺序：禁用 SGR mouse → 恢复 input mode
            // （退出备用屏由 AgentCliVtScreen.Dispose 负责，在 VtInput 之后调用）
            DisableSgrMouse();

            if (_inputModeSet && _stdinHandle != IntPtr.Zero && _stdinHandle != INVALID_HANDLE_VALUE)
            {
                SetConsoleMode(_stdinHandle, _originalInputMode);
                _inputModeSet = false;
                s_log.Write($"[vt-input] restored input mode: 0x{_originalInputMode:X8}");
            }

            ClearRegions();
        }

        #region DA1 探测

        private static bool ProbeDa1(IntPtr stdin, uint originalMode)
        {
            // 临时开启 VT input mode 以接收 DA1 响应（响应通过 stdin 返回）
            uint probeMode = (originalMode | ENABLE_VIRTUAL_TERMINAL_INPUT)
                             & ~ENABLE_PROCESSED_INPUT
                             & ~ENABLE_ECHO_INPUT
                             & ~ENABLE_LINE_INPUT;
            if (!SetConsoleMode(stdin, probeMode))
            {
                s_log.Write("[vt-input] failed to set probe input mode");
                return false;
            }

            // 发送 DA1 查询
            TryWrite("\x1b[c");
            s_log.Write("[vt-input] sent DA1 query ESC[c");

            // 轮询 200ms 等待响应
            byte[] buffer = new byte[256];
            int totalRead = 0;
            var deadline = DateTime.UtcNow.AddMilliseconds(Da1TimeoutMs);

            while (DateTime.UtcNow < deadline)
            {
                if (GetNumberOfConsoleInputEvents(stdin, out uint count) && count > 0)
                {
                    int n = ReadRawBytes(stdin, buffer, totalRead, buffer.Length - totalRead);
                    if (n > 0)
                    {
                        totalRead += n;
                        if (ContainsDa1Response(buffer, totalRead))
                        {
                            s_log.Write($"[vt-input] DA1 response received ({totalRead} bytes)");
                            // 恢复原始 mode，后续 SetupVtInputMode 会重新设置
                            SetConsoleMode(stdin, originalMode);
                            return true;
                        }
                    }
                }
                Thread.Sleep(5);
            }

            // 超时
            s_log.Write($"[vt-input] DA1 probe timeout (read {totalRead} bytes)");
            SetConsoleMode(stdin, originalMode);
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
                if (!ReadFile(stdin, (IntPtr)p, count, out int read, IntPtr.Zero))
                    return 0;
                return read;
            }
        }

        #endregion

        private static void TryWrite(string text)
        {
            try { Console.Write(text); } catch { }
        }

        #region P/Invoke

        private const int STD_INPUT_HANDLE = -10;
        private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

        private const uint ENABLE_PROCESSED_INPUT = 0x0001;
        private const uint ENABLE_LINE_INPUT = 0x0002;
        private const uint ENABLE_ECHO_INPUT = 0x0004;
        private const uint ENABLE_WINDOW_INPUT = 0x0008;
        private const uint ENABLE_MOUSE_INPUT = 0x0010;
        private const uint ENABLE_EXTENDED_FLAGS = 0x0080;
        private const uint ENABLE_QUICK_EDIT_MODE = 0x0040;
        private const uint ENABLE_VIRTUAL_TERMINAL_INPUT = 0x0200;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetStdHandle(int nStdHandle);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetNumberOfConsoleInputEvents(IntPtr hConsoleInput, out uint lpcNumberOfEvents);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ReadFile(IntPtr hFile, IntPtr lpBuffer, int nNumberOfBytesToRead, out int lpNumberOfBytesRead, IntPtr lpOverlapped);

        #endregion
    }

    /// <summary>
    /// Unix VT 输入后端：本次不实现，HasInputAvailable 返回 false 触发降级路径。
    /// 后续版本用 poll + read 实现。
    /// </summary>
    internal sealed class UnixVtInput : AgentCliVtInput
    {
        private UnixVtInput(AgentCliProtocol host) : base(host) { }

        internal static UnixVtInput? TryCreate(AgentCliProtocol host)
        {
            // 本次不实现 Unix termios 路径
            return null;
        }

        internal override bool HasInputAvailable() => false;
        internal override int ReadByte() => -1;
        internal override void EnableSgrMouse() { }
        internal override void DisableSgrMouse() { }

        public override void Dispose() { }
    }

    /// <summary>
    /// VT 输入解析状态机：接收 raw bytes，输出 KeyEvent / MouseEvent。
    /// 状态：GROUND → ESC → CSI → SGR_MOUSE。
    /// DA1 响应在探测阶段消费，主循环中若偶发到达则按 CSI 序列忽略。
    /// </summary>
    internal sealed class VtParser
    {
        private readonly AgentCliVtInput _owner;

        private enum State { Ground, Esc, Csi, SgrMouse }

        private State _state = State.Ground;
        private readonly StringBuilder _params = new();
        private readonly byte[] _utf8Buffer = new byte[4];
        private int _utf8Len;

        internal VtParser(AgentCliVtInput owner)
        {
            _owner = owner;
        }

        internal void Feed(byte b)
        {
            switch (_state)
            {
                case State.Ground: HandleGround(b); break;
                case State.Esc: HandleEsc(b); break;
                case State.Csi: HandleCsi(b); break;
                case State.SgrMouse: HandleSgrMouse(b); break;
            }
        }

        private void HandleGround(byte b)
        {
            if (b == 0x1B) // ESC
            {
                _state = State.Esc;
                return;
            }

            if (b == 0x03) // Ctrl+C
            {
                _owner.OnKeyEvent((ConsoleKey)0, '\x03');
                return;
            }

            // UTF-8 缓冲
            if (_utf8Len == 0)
            {
                int expected = ExpectedUtf8Length(b);
                if (expected < 0)
                {
                    // 无效 UTF-8 首字节，作为单字节 Latin-1 派发
                    _owner.OnKeyEvent((ConsoleKey)0, (char)b);
                    return;
                }
                _utf8Buffer[0] = b;
                _utf8Len = 1;
                if (expected == 1)
                {
                    DispatchUtf8();
                }
                return;
            }

            // 收集后续字节（0x80-0xBF）
            if (b >= 0x80 && b <= 0xBF)
            {
                _utf8Buffer[_utf8Len++] = b;
                int expected = ExpectedUtf8Length(_utf8Buffer[0]);
                if (_utf8Len >= expected)
                    DispatchUtf8();
                return;
            }

            // 后续字节不是 continuation byte，先派发已缓冲的（可能不完整），再处理当前字节
            DispatchUtf8();
            Feed(b);
        }

        private void HandleEsc(byte b)
        {
            if (b == (byte)'[')
            {
                _state = State.Csi;
                _params.Clear();
                return;
            }

            // ESC 单独作为按键
            _owner.OnKeyEvent(ConsoleKey.Escape, '\x1b');
            _state = State.Ground;

            // 重新处理当前字节
            HandleGround(b);
        }

        private void HandleCsi(byte b)
        {
            if (b == (byte)'<')
            {
                _state = State.SgrMouse;
                _params.Clear();
                return;
            }

            // 终结字节（0x40-0x7E）
            if (b >= 0x40 && b <= 0x7E)
            {
                HandleCsiTerminator(b);
                _state = State.Ground;
                return;
            }

            // 收集参数
            _params.Append((char)b);
        }

        private void HandleCsiTerminator(byte b)
        {
            switch (b)
            {
                case (byte)'A': // ↑
                    _owner.OnKeyEvent(ConsoleKey.UpArrow, '\0');
                    break;
                case (byte)'B': // ↓
                    _owner.OnKeyEvent(ConsoleKey.DownArrow, '\0');
                    break;
                case (byte)'C': // →
                    _owner.OnKeyEvent(ConsoleKey.RightArrow, '\0');
                    break;
                case (byte)'D': // ←
                    _owner.OnKeyEvent(ConsoleKey.LeftArrow, '\0');
                    break;
                case (byte)'H': // Home
                    _owner.OnKeyEvent(ConsoleKey.Home, '\0');
                    break;
                case (byte)'F': // End
                    _owner.OnKeyEvent(ConsoleKey.End, '\0');
                    break;
                // 'c' = DA1 响应，忽略；其他 CSI 序列忽略
            }
        }

        private void HandleSgrMouse(byte b)
        {
            if (b == (byte)'M' || b == (byte)'m')
            {
                // 解析 SGR mouse: ESC[<Cb;Cx;Cy M/m
                string[] parts = _params.ToString().Split(';');
                if (parts.Length == 3
                    && int.TryParse(parts[0], out int cb)
                    && int.TryParse(parts[1], out int cx)
                    && int.TryParse(parts[2], out int cy))
                {
                    // 1-based → 0-based viewport 坐标
                    int row = cy - 1;
                    int col = cx - 1;
                    bool isPress = b == (byte)'M';

                    // 只报告左键（cb=0 按下, cb=2 释放）
                    if (cb == 0 || cb == 2)
                        _owner.OnMouseEvent(row, col, isPress);
                }
                _state = State.Ground;
                return;
            }

            _params.Append((char)b);
        }

        private void DispatchUtf8()
        {
            if (_utf8Len == 0) return;
            try
            {
                string s = Encoding.UTF8.GetString(_utf8Buffer, 0, _utf8Len);
                if (s.Length > 0)
                    _owner.OnKeyEvent((ConsoleKey)0, s[0]);
            }
            catch
            {
                // 解码失败，丢弃
            }
            _utf8Len = 0;
        }

        private static int ExpectedUtf8Length(byte first)
        {
            if (first < 0x80) return 1;
            if ((first & 0xE0) == 0xC0) return 2;
            if ((first & 0xF0) == 0xE0) return 3;
            if ((first & 0xF8) == 0xF0) return 4;
            return -1; // 无效首字节
        }
    }
}
