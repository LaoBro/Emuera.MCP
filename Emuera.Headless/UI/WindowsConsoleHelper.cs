using MinorShift.Emuera.Runtime.Config;
using System;
using System.Runtime.InteropServices;

namespace MinorShift.Emuera.UI;

internal static class WindowsConsoleHelper
{
    public static bool AnsiEnabled { get; private set; }

    public static void Setup()
    {
        if (!OperatingSystem.IsWindows()) return;
        if (Console.IsInputRedirected) return;
        TryEnableVirtualTerminal();
    }

    private static void TryEnableVirtualTerminal()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            IntPtr hOut = GetStdHandle(STD_OUTPUT_HANDLE);
            if (hOut == IntPtr.Zero || hOut == INVALID_HANDLE_VALUE) return;
            if (!GetConsoleMode(hOut, out uint mode)) return;
            if ((mode & ENABLE_VIRTUAL_TERMINAL_PROCESSING) != 0)
            {
                AnsiEnabled = true;
                return;
            }
            if (SetConsoleMode(hOut, mode | ENABLE_VIRTUAL_TERMINAL_PROCESSING))
                AnsiEnabled = true;
        }
        catch (Exception) { /* P/Invoke 兜底，失败则 ANSI 不启用 */ }
    }

    public static void TrySetConsoleSize()
    {
        if (Console.IsInputRedirected) return;
        try
        {
            int charWidth = Math.Max(Config.FontSize / 2, 1);
            int gameColumns = Config.DrawableWidth / charWidth;
            int gameRows = Config.WindowY / Config.LineHeight;

            if (gameColumns <= 0 || gameRows <= 0) return;

            if (IsWindowsTerminal())
            {
                int rows = gameRows + 4;
                Console.Write($"\x1b[8;{rows};{gameColumns}t");
                return;
            }

            if (Console.BufferWidth < gameColumns)
                Console.BufferWidth = gameColumns;
            if (Console.BufferHeight < gameRows + 10)
                Console.BufferHeight = gameRows + 10;

            Console.WindowWidth = Math.Min(gameColumns, Console.LargestWindowWidth);
            Console.WindowHeight = Math.Min(gameRows + 4, Console.LargestWindowHeight);
        }
        catch (Exception) { /* 控制台尺寸设置失败，忽略 */ }
    }

    public static bool IsWindowsTerminal()
        => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WT_SESSION"));

    public static void DetectConsoleFont()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            IntPtr hOut = CreateFileW("CONOUT$", GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            bool openedConout = hOut != IntPtr.Zero && hOut != INVALID_HANDLE_VALUE;

            if (!openedConout)
            {
                hOut = GetStdHandle(STD_OUTPUT_HANDLE);
                if (hOut == IntPtr.Zero || hOut == INVALID_HANDLE_VALUE)
                {
                    Console.Error.WriteLine("[terminal] Console font: failed to get console handle");
                    return;
                }
            }

            var info = new CONSOLE_FONT_INFO_EX();
            info.cbSize = (uint)Marshal.SizeOf<CONSOLE_FONT_INFO_EX>();
            if (!GetCurrentConsoleFontEx(hOut, false, ref info))
            {
                int err = Marshal.GetLastWin32Error();
                Console.Error.WriteLine($"[terminal] Console font: GetCurrentConsoleFontEx failed, error={err}");
                if (openedConout) CloseHandle(hOut);
                return;
            }
            Console.Error.WriteLine($"[terminal] Console font: {info.FaceName}, size={info.dwFontSizeX}x{info.dwFontSizeY}");
            Console.Error.WriteLine("[terminal] (注意: Windows Terminal/Git Bash 下字体名可能不准确，以宽度探测结果为准)");

            if (openedConout) CloseHandle(hOut);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[terminal] Console font: exception - {ex.Message}");
        }
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetCurrentConsoleFontEx(IntPtr hConsoleOutput, bool bMaximumWindow, ref CONSOLE_FONT_INFO_EX lpConsoleCurrentFontEx);

    private const int STD_OUTPUT_HANDLE = -11;
    private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);
    private const uint ENABLE_VIRTUAL_TERMINAL_PROCESSING = 0x0004;
    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ = 0x00000001;
    private const uint FILE_SHARE_WRITE = 0x00000002;
    private const uint OPEN_EXISTING = 3;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CONSOLE_FONT_INFO_EX
    {
        public uint cbSize;
        public uint nFont;
        public short dwFontSizeX;
        public short dwFontSizeY;
        public uint FontFamily;
        public uint FontWeight;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string FaceName;
    }
}
