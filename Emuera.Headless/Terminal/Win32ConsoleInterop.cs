using System;
using System.Runtime.InteropServices;

namespace MinorShift.Emuera.GameView
{
    /// <summary>
    /// Win32 控制台互操作 P/Invoke 声明。
    /// 集中管理 kernel32.dll 句柄与 console mode 相关导入，供 Windows VT 输入后端使用。
    /// 所有调用方需自行通过 <see cref="OperatingSystem.IsWindows"/> 守卫。
    /// </summary>
    internal static class Win32ConsoleInterop
    {
        internal const int STD_INPUT_HANDLE = -10;
        internal static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

        // Console input mode flags
        internal const uint ENABLE_PROCESSED_INPUT = 0x0001;
        internal const uint ENABLE_LINE_INPUT = 0x0002;
        internal const uint ENABLE_ECHO_INPUT = 0x0004;
        internal const uint ENABLE_WINDOW_INPUT = 0x0008;
        internal const uint ENABLE_MOUSE_INPUT = 0x0010;
        internal const uint ENABLE_EXTENDED_FLAGS = 0x0080;
        internal const uint ENABLE_QUICK_EDIT_MODE = 0x0040;
        internal const uint ENABLE_VIRTUAL_TERMINAL_INPUT = 0x0200;

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern IntPtr GetStdHandle(int nStdHandle);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetNumberOfConsoleInputEvents(IntPtr hConsoleInput, out uint lpcNumberOfEvents);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ReadFile(IntPtr hFile, IntPtr lpBuffer, int nNumberOfBytesToRead, out int lpNumberOfBytesRead, IntPtr lpOverlapped);
    }
}
