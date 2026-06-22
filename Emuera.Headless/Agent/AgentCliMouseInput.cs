using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using MinorShift.Emuera.UI.Game;

namespace MinorShift.Emuera.GameView
{
    /// <summary>
    /// CLI 鼠标输入后端：GetNumberOfConsoleInputEvents 非阻塞轮询 + ReadConsoleInput 统一分派。
    /// 仅在 Windows + 交互式 conhost 下启用；pipe / redirected stdin 不启用。
    /// </summary>
    internal sealed class AgentCliMouseInput : IDisposable
    {
        private readonly AgentCliProtocol _host;
        private readonly IntPtr _stdinHandle;
        private readonly uint _originalInputMode;
        private readonly bool _enabled;
        private bool _disposed;

        private readonly List<ButtonTerminalRegion> _buttonRegions = new();

        private static readonly LogConfig s_log = new();

        private const uint ENABLE_MOUSE_INPUT = 0x0010;
        private const uint ENABLE_WINDOW_INPUT = 0x0008;
        private const uint ENABLE_EXTENDED_FLAGS = 0x0080;
        private const uint ENABLE_QUICK_EDIT_MODE = 0x0040;

        private const ushort KEY_EVENT = 0x0001;
        private const ushort MOUSE_EVENT = 0x0002;

        private const uint FROM_LEFT_1ST_BUTTON_PRESSED = 0x0001;
        private const uint DOUBLE_CLICK = 0x0002;

        private const uint SHIFT_PRESSED = 0x0010;
        private const uint LEFT_ALT_PRESSED = 0x0002;
        private const uint RIGHT_ALT_PRESSED = 0x0001;
        private const uint LEFT_CTRL_PRESSED = 0x0008;
        private const uint RIGHT_CTRL_PRESSED = 0x0004;

        private const ushort VK_PROCESSKEY = 0xE5; // IME 组合中间态

        private AgentCliMouseInput(AgentCliProtocol host, IntPtr stdinHandle, uint originalInputMode)
        {
            _host = host;
            _stdinHandle = stdinHandle;
            _originalInputMode = originalInputMode;
            _enabled = true;
        }

        internal static AgentCliMouseInput? TryCreate(AgentCliProtocol host)
        {
            if (!OperatingSystem.IsWindows()) return null;
            if (Console.IsInputRedirected) return null;

            IntPtr stdin = GetStdHandle(STD_INPUT_HANDLE);
            if (stdin == IntPtr.Zero || stdin == INVALID_HANDLE_VALUE) return null;

            if (!GetConsoleMode(stdin, out uint originalMode)) return null;

            uint newMode = (originalMode | ENABLE_MOUSE_INPUT | ENABLE_WINDOW_INPUT | ENABLE_EXTENDED_FLAGS)
                           & ~ENABLE_QUICK_EDIT_MODE;
            if (!SetConsoleMode(stdin, newMode)) return null;

            s_log.Write($"[mouse-input] enabled. originalMode=0x{originalMode:X8} newMode=0x{newMode:X8}");
            return new AgentCliMouseInput(host, stdin, originalMode);
        }

        internal bool IsEnabled => _enabled && !_disposed;

        private const int PollBatchSize = 16;

        internal void PollEvents()
        {
            if (!_enabled || _disposed) return;
            if (!GetNumberOfConsoleInputEvents(_stdinHandle, out uint count) || count == 0) return;

            var batch = new INPUT_RECORD[Math.Min((int)count, PollBatchSize)];
            if (!ReadConsoleInputW(_stdinHandle, batch, (uint)batch.Length, out uint read) || read == 0)
                return;

            for (int i = 0; i < read; i++)
                DispatchRecord(batch[i]);
        }

        private void DispatchRecord(INPUT_RECORD rec)
        {
            switch (rec.EventType)
            {
                case KEY_EVENT:  HandleKey(rec.KeyEvent);   break;
                case MOUSE_EVENT: HandleMouse(rec.MouseEvent); break;
            }
        }

        #region Keyboard

        private void HandleKey(KEY_EVENT_RECORD keyEvent)
        {
            s_log.Write($"[key] raw down={keyEvent.bKeyDown} repeat={keyEvent.wRepeatCount} vk=0x{keyEvent.wVirtualKeyCode:X2} sc=0x{keyEvent.wVirtualScanCode:X2} char=0x{(int)keyEvent.uChar:X4} state=0x{keyEvent.dwControlKeyState:X8}");

            if (keyEvent.bKeyDown == 0) return;

            // Ctrl+C 手动检测
            if (keyEvent.wVirtualKeyCode == (ushort)ConsoleKey.C
                && (keyEvent.dwControlKeyState & (LEFT_CTRL_PRESSED | RIGHT_CTRL_PRESSED)) != 0)
            {
                s_log.Write("[key] Ctrl+C detected, dispatching 0x03");
                _host.ProcessChar('\x03');
                return;
            }

            // IME 组合中间态，跳过
            if (keyEvent.wVirtualKeyCode == VK_PROCESSKEY)
            {
                s_log.Write("[key] skip IME process key");
                return;
            }

            ConsoleModifiers mods = MapControlKeyState(keyEvent.dwControlKeyState);
            var keyInfo = new ConsoleKeyInfo(
                keyEvent.uChar, (ConsoleKey)keyEvent.wVirtualKeyCode,
                mods.HasFlag(ConsoleModifiers.Shift),
                mods.HasFlag(ConsoleModifiers.Alt),
                mods.HasFlag(ConsoleModifiers.Control));

            s_log.Write($"[key] dispatch vk=0x{keyEvent.wVirtualKeyCode:X2} char=0x{(int)keyEvent.uChar:X4} mods={mods}");
            _host.ProcessKeyFromMouseInput(keyInfo);
        }

        private static ConsoleModifiers MapControlKeyState(uint state)
        {
            ConsoleModifiers mods = 0;
            if ((state & SHIFT_PRESSED) != 0) mods |= ConsoleModifiers.Shift;
            if ((state & (LEFT_ALT_PRESSED | RIGHT_ALT_PRESSED)) != 0) mods |= ConsoleModifiers.Alt;
            if ((state & (LEFT_CTRL_PRESSED | RIGHT_CTRL_PRESSED)) != 0) mods |= ConsoleModifiers.Control;
            return mods;
        }

        #endregion

        #region Mouse

        private void HandleMouse(MOUSE_EVENT_RECORD mouseEvent)
        {
            // 只接受左键按下（单击或双击），忽略移动/释放/滚轮
            bool leftDown = (mouseEvent.dwButtonState & FROM_LEFT_1ST_BUTTON_PRESSED) != 0;
            uint flags = mouseEvent.dwEventFlags;
            if (!leftDown || (flags != 0 && flags != DOUBLE_CLICK)) return;

            int mouseRow = mouseEvent.dwMousePosition.Y;
            int mouseCol = mouseEvent.dwMousePosition.X;

            s_log.Write($"[mouse] left-down bufferRow={mouseRow} bufferCol={mouseCol} regions={_buttonRegions.Count}");

            // 命中查找：从后往前（重叠时选最后记录的按钮）
            for (int i = _buttonRegions.Count - 1; i >= 0; i--)
            {
                var r = _buttonRegions[i];
                if (r.Row == mouseRow && mouseCol >= r.Left && mouseCol <= r.Right)
                {
                    s_log.Write($"[mouse] hit row={mouseRow} col={mouseCol} region={r.Left}-{r.Right} input={r.Button.Inputs}");
                    _host.DispatchMouseClick(r.Button);
                    return;
                }
            }

            s_log.Write($"[mouse] miss bufferRow={mouseRow} bufferCol={mouseCol}");
            _host.DispatchMouseMiss();
        }

        #endregion

        #region Button regions

        internal void ClearRegions() => _buttonRegions.Clear();

        internal void RecordLineRegions(string formattedLine, int bufferRow, ConsoleButtonString[]? buttons, long currentGeneration)
        {
            if (buttons == null || buttons.Length == 0 || string.IsNullOrEmpty(formattedLine)) return;

            int column = LeadingDisplayWidth(formattedLine);

            foreach (var btn in buttons)
            {
                if (btn == null) continue;

                string btnText = btn.ToString() ?? "";
                int segmentWidth = TerminalDisplayWidth.GetDisplayWidth(btnText);

                if (btn.IsButton && btn.Generation == currentGeneration && segmentWidth > 0)
                {
                    _buttonRegions.Add(new ButtonTerminalRegion(
                        bufferRow, column, column + segmentWidth - 1, btn, currentGeneration));

                    s_log.Write($"[region] row={bufferRow} col={column}-{column + segmentWidth - 1} input={btn.Inputs} label={btnText}");
                }

                column += segmentWidth;
            }
        }

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

        #endregion

        #region Lifecycle

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _buttonRegions.Clear();

            if (_stdinHandle != IntPtr.Zero && _stdinHandle != INVALID_HANDLE_VALUE)
            {
                SetConsoleMode(_stdinHandle, _originalInputMode);
                s_log.Write($"[mouse-input] restored inputMode=0x{_originalInputMode:X8}");
            }
        }

        #endregion

        #region ButtonTerminalRegion

        internal readonly record struct ButtonTerminalRegion(
            int Row,
            int Left,
            int Right,
            ConsoleButtonString Button,
            long Generation);

        #endregion

        #region P/Invoke

        private const int STD_INPUT_HANDLE = -10;
        private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

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
        private static extern bool ReadConsoleInputW(
            IntPtr hConsoleInput,
            [Out] INPUT_RECORD[] lpBuffer,
            uint nLength,
            out uint lpNumberOfEventsRead);

        [StructLayout(LayoutKind.Explicit)]
        private struct INPUT_RECORD
        {
            [FieldOffset(0)] public ushort EventType;
            [FieldOffset(4)] public KEY_EVENT_RECORD KeyEvent;
            [FieldOffset(4)] public MOUSE_EVENT_RECORD MouseEvent;
            [FieldOffset(4)] public WINDOW_BUFFER_SIZE_RECORD WindowBufferSizeEvent;
            [FieldOffset(4)] public MENU_EVENT_RECORD MenuEvent;
            [FieldOffset(4)] public FOCUS_EVENT_RECORD FocusEvent;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct KEY_EVENT_RECORD
        {
            [FieldOffset(0)]  public int    bKeyDown;
            [FieldOffset(4)]  public ushort wRepeatCount;
            [FieldOffset(6)]  public ushort wVirtualKeyCode;
            [FieldOffset(8)]  public ushort wVirtualScanCode;
            [FieldOffset(10)] public char   uChar;
            [FieldOffset(12)] public uint   dwControlKeyState;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MOUSE_EVENT_RECORD
        {
            public COORD dwMousePosition;
            public uint dwButtonState;
            public uint dwControlKeyState;
            public uint dwEventFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct COORD
        {
            public short X;
            public short Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WINDOW_BUFFER_SIZE_RECORD { public COORD dwSize; }

        [StructLayout(LayoutKind.Sequential)]
        private struct MENU_EVENT_RECORD { public uint dwCommandId; }

        [StructLayout(LayoutKind.Sequential)]
        private struct FOCUS_EVENT_RECORD { public int bSetFocus; } // Win32 BOOL = int

        #endregion

        #region Logging

        private sealed class LogConfig
        {
            public readonly bool Enabled;
            public readonly string? Path;

            public LogConfig()
            {
                try
                {
                    string? v = Environment.GetEnvironmentVariable("EMUERA_MOUSE_LOG");
                    Enabled = v == "1" || v == "true";
                }
                catch { Enabled = false; }

                if (Enabled)
                {
                    try
                    {
                        string exeDir = Program.ExeDir;
                        if (string.IsNullOrEmpty(exeDir)) exeDir = AppContext.BaseDirectory;
                        string dir = System.IO.Path.Combine(exeDir, "debug");
                        Directory.CreateDirectory(dir);
                        Path = System.IO.Path.Combine(dir, "mouse.log");
                    }
                    catch { Path = null; }
                }
            }

            public void Write(string message)
            {
                if (!Enabled || Path == null) return;
                try { File.AppendAllText(Path, $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}"); }
                catch { }
            }
        }

        #endregion
    }
}
