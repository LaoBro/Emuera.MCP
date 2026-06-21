using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using MinorShift.Emuera.UI.Game;

namespace MinorShift.Emuera.GameView
{
    /// <summary>
    /// CLI 鼠标输入后端：方案 D（GetNumberOfConsoleInputEvents 非阻塞轮询 + ReadConsoleInput 统一分派）。
    /// 负责 P/Invoke、console input mode 生命周期、INPUT_RECORD 分派（鼠标 + 键盘）、
    /// 按钮区域记录与命中、KEY_EVENT_RECORD → ConsoleKeyInfo 映射、文件调试日志。
    /// 仅在 Windows + 交互式 conhost 下启用；pipe / redirected stdin 不启用。
    /// </summary>
    internal sealed class AgentCliMouseInput : IDisposable
    {
        private readonly AgentCliProtocol _host;
        private readonly IntPtr _stdinHandle;
        private readonly uint _originalInputMode;
        private readonly bool _enabled;
        private bool _disposed;

        // 按钮区域：conhost buffer 坐标
        private readonly List<ButtonTerminalRegion> _buttonRegions = new();

        // 调试日志：必须先解析 s_logEnabled，再解析 s_logPath（后者依赖前者）
        private static readonly bool s_logEnabled = ResolveLogEnabled();
        private static readonly string? s_logPath = ResolveLogPath();

        private const uint ENABLE_MOUSE_INPUT = 0x0010;
        private const uint ENABLE_WINDOW_INPUT = 0x0008;
        private const uint ENABLE_EXTENDED_FLAGS = 0x0080;
        private const uint ENABLE_QUICK_EDIT_MODE = 0x0040;

        private const ushort KEY_EVENT = 0x0001;
        private const ushort MOUSE_EVENT = 0x0002;
        private const ushort WINDOW_BUFFER_SIZE_EVENT = 0x0004;
        private const ushort MENU_EVENT = 0x0008;
        private const ushort FOCUS_EVENT = 0x0010;

        private const uint FROM_LEFT_1ST_BUTTON_PRESSED = 0x0001;
        private const uint RIGHTMOST_BUTTON_PRESSED = 0x0002;
        private const uint FROM_LEFT_2ND_BUTTON_PRESSED = 0x0004;
        private const uint FROM_LEFT_3RD_BUTTON_PRESSED = 0x0008;
        private const uint FROM_LEFT_4TH_BUTTON_PRESSED = 0x0010;

        private const uint SHIFT_PRESSED = 0x0010;
        private const uint LEFT_ALT_PRESSED = 0x0002;
        private const uint RIGHT_ALT_PRESSED = 0x0001;
        private const uint LEFT_CTRL_PRESSED = 0x0008;
        private const uint RIGHT_CTRL_PRESSED = 0x0004;

        private const uint DOUBLE_CLICK = 0x0002;
        private const uint MOUSE_WHEELED = 0x0004;
        private const uint MOUSE_HWHEELED = 0x0008;

        private AgentCliMouseInput(AgentCliProtocol host, IntPtr stdinHandle, uint originalInputMode)
        {
            _host = host;
            _stdinHandle = stdinHandle;
            _originalInputMode = originalInputMode;
            _enabled = true;
        }

        /// <summary>
        /// 尝试为 host 创建鼠标输入后端。
        /// 仅在 Windows + 交互式 stdin 下创建；否则返回 null。
        /// 创建时即调整 input mode（启用 MOUSE_INPUT/WINDOW_INPUT/EXTENDED_FLAGS，禁用 QUICK_EDIT）。
        /// </summary>
        internal static AgentCliMouseInput? TryCreate(AgentCliProtocol host)
        {
            if (!OperatingSystem.IsWindows()) return null;
            if (Console.IsInputRedirected) return null;

            IntPtr stdin = GetStdHandle(STD_INPUT_HANDLE);
            if (stdin == IntPtr.Zero || stdin == INVALID_HANDLE_VALUE) return null;

            if (!GetConsoleMode(stdin, out uint originalMode)) return null;

            // 启用鼠标 + 窗口事件 + 扩展标志，禁用 Quick Edit
            uint newMode = (originalMode | ENABLE_MOUSE_INPUT | ENABLE_WINDOW_INPUT | ENABLE_EXTENDED_FLAGS)
                           & ~ENABLE_QUICK_EDIT_MODE;
            if (!SetConsoleMode(stdin, newMode))
            {
                // 设置失败则不启用鼠标，静默 fallback 到键盘
                return null;
            }

            Log($"[mouse-input] enabled. originalMode=0x{originalMode:X8} newMode=0x{newMode:X8}");
            return new AgentCliMouseInput(host, stdin, originalMode);
        }

        internal bool IsEnabled => _enabled && !_disposed;

        /// <summary>
        /// 主循环每次轮询调用：用 GetNumberOfConsoleInputEvents 查询队列，
        /// &gt;0 时逐条 ReadConsoleInput 并按 EventType 分派。
        /// 返回是否处理了任何事件（用于决定是否跳过 Thread.Sleep）。
        /// </summary>
        internal bool PollEvents()
        {
            if (!_enabled || _disposed) return false;

            if (!GetNumberOfConsoleInputEvents(_stdinHandle, out uint count))
                return false;

            if (count == 0) return false;

            for (int i = 0; i < count; i++)
            {
                if (!ReadConsoleInputW(_stdinHandle, out INPUT_RECORD rec, 1, out uint read))
                    break;
                if (read == 0) break;

                DispatchRecord(rec);
            }
            return true;
        }

        private void DispatchRecord(INPUT_RECORD rec)
        {
            switch (rec.EventType)
            {
                case KEY_EVENT:
                    HandleKey(rec.KeyEvent);
                    break;
                case MOUSE_EVENT:
                    HandleMouse(rec.MouseEvent);
                    break;
                case WINDOW_BUFFER_SIZE_EVENT:
                    // 忽略：conhost 绘图阶段会触发，生产路径不处理
                    break;
                case MENU_EVENT:
                case FOCUS_EVENT:
                    // 忽略
                    break;
                default:
                    break;
            }
        }

        // ===== 键盘事件映射 =====

        private void HandleKey(KEY_EVENT_RECORD keyEvent)
        {
            // 诊断日志：在所有过滤之前打印原始字段，便于定位 marshalling 问题
            Log($"[key] raw down={keyEvent.bKeyDown} repeat={keyEvent.wRepeatCount} vk=0x{keyEvent.wVirtualKeyCode:X2} sc=0x{keyEvent.wVirtualScanCode:X2} char=0x{(int)keyEvent.uChar:X4} state=0x{keyEvent.dwControlKeyState:X8}");

            // 释放事件一律忽略，避免重复触发
            if (keyEvent.bKeyDown == 0) return;

            // Ctrl+C 手动检测（不再依赖 Console.CancelKeyPress）
            if (keyEvent.wVirtualKeyCode == (ushort)ConsoleKey.C
                && (keyEvent.dwControlKeyState & (LEFT_CTRL_PRESSED | RIGHT_CTRL_PRESSED)) != 0)
            {
                Log("[key] Ctrl+C detected, dispatching 0x03");
                _host.ProcessChar('\x03');
                return;
            }

            char keyChar = keyEvent.uChar;
            ConsoleKey key = (ConsoleKey)keyEvent.wVirtualKeyCode;
            ConsoleModifiers mods = MapControlKeyState(keyEvent.dwControlKeyState);

            // IME 组合中间态：VK_PROCESSKEY (0xE5)，由 IME 发送表示正在组合中，此时跳过
            // 注意：方向键/PgUp/PgDn/Home/End/Insert/Delete/F1-F24 等控制键的 uChar 也是 0，
            // 但它们有合法的 wVirtualKeyCode，必须放行给 ProcessKey 处理
            if (keyEvent.wVirtualKeyCode == VK_PROCESSKEY)
            {
                Log($"[key] skip IME process key");
                return;
            }

            var keyInfo = new ConsoleKeyInfo(
                keyChar, key,
                mods.HasFlag(ConsoleModifiers.Shift),
                mods.HasFlag(ConsoleModifiers.Alt),
                mods.HasFlag(ConsoleModifiers.Control));

            Log($"[key] dispatch vk=0x{keyEvent.wVirtualKeyCode:X2} char=0x{(int)keyChar:X4} mods={mods}");
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

        // VK 常量
        private const ushort VK_PROCESSKEY = 0xE5;  // IME 组合中间态

        // ===== 鼠标事件处理 =====

        private void HandleMouse(MOUSE_EVENT_RECORD mouseEvent)
        {
            // 只接受左键按下；允许单击和双击，忽略移动/释放/滚轮
            if ((mouseEvent.dwButtonState & FROM_LEFT_1ST_BUTTON_PRESSED) == 0
                || (mouseEvent.dwEventFlags != 0 && mouseEvent.dwEventFlags != DOUBLE_CLICK))
            {
                return;
            }

            int mouseRow = mouseEvent.dwMousePosition.Y;
            int mouseCol = mouseEvent.dwMousePosition.X;

            Log($"[mouse] left-down bufferRow={mouseRow} bufferCol={mouseCol} regions={_buttonRegions.Count}");

            // 命中查找：从后往前（重叠时选最后记录的按钮）
            for (int i = _buttonRegions.Count - 1; i >= 0; i--)
            {
                var region = _buttonRegions[i];
                if (region.Row == mouseRow && mouseCol >= region.Left && mouseCol <= region.Right)
                {
                    Log($"[mouse] hit bufferRow={mouseRow} bufferCol={mouseCol} regionRow={region.Row} regionCol={region.Left}-{region.Right} input={region.Button.Inputs}");
                    _host.DispatchMouseClick(region.Button);
                    return;
                }
            }

            // miss：非按钮模式下派发回车推进游戏
            Log($"[mouse] miss bufferRow={mouseRow} bufferCol={mouseCol}");
            _host.DispatchMouseMiss();
        }

        // ===== 按钮区域记录 =====

        /// <summary>
        /// 清空当前按钮区域。每次刷新前调用。
        /// </summary>
        internal void ClearRegions()
        {
            _buttonRegions.Clear();
        }

        /// <summary>
        /// 为一行格式化后的显示文本记录按钮区域。
        /// 调用方必须传入 console.FormatLineForTerminal(line) 后的字符串，
        /// 且 bufferRow 必须是 Console.WindowTop + visibleRowIndex 推导出的 buffer 行号。
        /// </summary>
        internal void RecordLineRegions(string formattedLine, int bufferRow, ConsoleButtonString[]? buttons, long currentGeneration)
        {
            if (buttons == null || buttons.Length == 0) return;
            if (string.IsNullOrEmpty(formattedLine)) return;

            // leadingOffset 来自调用方（FullRefresh 记录的 CursorLeft），
            // 但 FullRefresh 在 Console.Clear() 后每行起始 CursorLeft=0，
            // 实际居中偏移在 formattedLine 的前导空格中。
            // 直接从 formattedLine 数前导空格作为真实偏移。
            int actualOffset = CountLeadingSpaces(formattedLine);

            int column = actualOffset;
            foreach (var btn in buttons)
            {
                if (btn == null) continue;

                string btnText = btn.ToString() ?? "";
                int segmentWidth = TerminalDisplayWidth.GetDisplayWidth(btnText);

                if (btn.IsButton && btn.Generation == currentGeneration && segmentWidth > 0)
                {
                    _buttonRegions.Add(new ButtonTerminalRegion
                    {
                        Row = bufferRow,
                        Left = column,
                        Right = column + segmentWidth - 1,
                        Button = btn,
                        Generation = currentGeneration,
                    });

                    Log($"[region] row={bufferRow} col={column}-{column + segmentWidth - 1} input={btn.Inputs} label={btnText}");
                }

                // 所有段（包括非按钮文本）都占据列宽，必须推进 column
                column += segmentWidth;
            }
        }

        // ===== 生命周期 =====

        /// <summary>
        /// 数 formattedLine 的前导空格数。
        /// FormatLineForTerminal 对 CENTER/RIGHT 行生成 "   " + styledText，
        /// 前导空格在 ANSI 转义码之前，直接数即可得到屏幕上的起始列。
        /// </summary>
        private static int CountLeadingSpaces(string s)
        {
            int count = 0;
            foreach (char c in s)
            {
                if (c == ' ') count++;
                else break;
            }
            return count;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _buttonRegions.Clear();

            // 恢复原始 input mode（含 Quick Edit）
            if (_stdinHandle != IntPtr.Zero && _stdinHandle != INVALID_HANDLE_VALUE)
            {
                SetConsoleMode(_stdinHandle, _originalInputMode);
                Log($"[mouse-input] restored inputMode=0x{_originalInputMode:X8}");
            }
        }

        // ===== 按钮区域数据结构 =====

        internal sealed class ButtonTerminalRegion
        {
            public int Row;       // 0-based conhost buffer row
            public int Left;      // 0-based conhost buffer column, inclusive
            public int Right;     // 0-based conhost buffer column, inclusive
            public ConsoleButtonString Button = null!;
            public long Generation;
        }

        // ===== P/Invoke =====

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
            out INPUT_RECORD lpBuffer,
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

        [StructLayout(LayoutKind.Sequential)]
        private struct KEY_EVENT_RECORD
        {
            public int bKeyDown;           // Win32 BOOL = int(4 bytes)，避免 bool marshalling 嫌疑
            public ushort wRepeatCount;
            public ushort wVirtualKeyCode;
            public ushort wVirtualScanCode;
            public char uChar;             // UnicodeChar，直接用 char 避免 union marshalling
            public uint dwControlKeyState;
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
        private struct WINDOW_BUFFER_SIZE_RECORD
        {
            public COORD dwSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MENU_EVENT_RECORD
        {
            public uint dwCommandId;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct FOCUS_EVENT_RECORD
        {
            public int bSetFocus;          // Win32 BOOL = int
        }

        // ===== 调试日志 =====

        private static bool ResolveLogEnabled()
        {
            try
            {
                string? v = Environment.GetEnvironmentVariable("EMUERA_MOUSE_LOG");
                return v == "1" || v == "true";
            }
            catch { return false; }
        }

        private static string? ResolveLogPath()
        {
            if (!s_logEnabled) return null;
            try
            {
                string exeDir = Program.ExeDir;
                if (string.IsNullOrEmpty(exeDir))
                    exeDir = AppContext.BaseDirectory;
                string dir = Path.Combine(exeDir, "debug");
                Directory.CreateDirectory(dir);
                return Path.Combine(dir, "mouse.log");
            }
            catch { return null; }
        }

        private static void Log(string message)
        {
            if (!s_logEnabled || s_logPath == null) return;
            try
            {
                File.AppendAllText(s_logPath,
                    $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
            catch { }
        }
    }
}
