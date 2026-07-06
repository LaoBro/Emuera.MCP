using System;
using System.Runtime.InteropServices;

namespace MinorShift.Emuera.Terminal.Platform;

internal sealed class WindowsTerminalSetup : ITerminalSetup
{
    public bool IsAnsiEnabled { get; private set; }

    public bool TryEnableAnsi()
    {
        if (!OperatingSystem.IsWindows()) return false;
        if (Console.IsInputRedirected) return false;
        try
        {
            IntPtr hOut = GetStdHandle(STD_OUTPUT_HANDLE);
            if (hOut == IntPtr.Zero || hOut == INVALID_HANDLE_VALUE) return false;
            if (!GetConsoleMode(hOut, out uint mode)) return false;
            if ((mode & ENABLE_VIRTUAL_TERMINAL_PROCESSING) != 0)
            {
                IsAnsiEnabled = true;
                return true;
            }
            if (SetConsoleMode(hOut, mode | ENABLE_VIRTUAL_TERMINAL_PROCESSING))
            {
                IsAnsiEnabled = true;
                return true;
            }
            return false;
        }
        catch (Exception) { return false; }
    }

    public bool TrySetConsoleSize(int cols, int rows)
    {
        if (Console.IsInputRedirected) return false;
        try
        {
            if (cols <= 0 || rows <= 0) return false;

            if (IsWindowsTerminal())
            {
                Console.Write($"\x1b[8;{rows};{cols}t");
                return true;
            }

            if (Console.BufferWidth < cols)
                Console.BufferWidth = cols;
            if (Console.BufferHeight < rows + 10)
                Console.BufferHeight = rows + 10;

            Console.WindowWidth = Math.Min(cols, Console.LargestWindowWidth);
            Console.WindowHeight = Math.Min(rows + 4, Console.LargestWindowHeight);
            return true;
        }
        catch (Exception) { return false; }
    }

    public string? DetectFont()
    {
        if (!OperatingSystem.IsWindows()) return null;
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
                    return null;
                }
            }

            var info = new CONSOLE_FONT_INFO_EX();
            info.cbSize = (uint)Marshal.SizeOf<CONSOLE_FONT_INFO_EX>();
            if (!GetCurrentConsoleFontEx(hOut, false, ref info))
            {
                int err = Marshal.GetLastWin32Error();
                Console.Error.WriteLine($"[terminal] Console font: GetCurrentConsoleFontEx failed, error={err}");
                if (openedConout) CloseHandle(hOut);
                return null;
            }
            Console.Error.WriteLine($"[terminal] Console font: {info.FaceName}, size={info.dwFontSizeX}x{info.dwFontSizeY}");
            Console.Error.WriteLine("[terminal] (注意: Windows Terminal/Git Bash 下字体名可能不准确，以宽度探测结果为准)");

            if (openedConout) CloseHandle(hOut);
            return info.FaceName;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[terminal] Console font: exception - {ex.Message}");
            return null;
        }
    }

    public bool TryProbeDa1()
    {
        if (!OperatingSystem.IsWindows()) return false;
        if (Console.IsInputRedirected) return false;

        // VT 处理已由 TryEnableAnsi 成功启用（SetConsoleMode 成功通过），
        // 跳过 DA1 探测。ConPTY 下 DA1 无响应但 VT 完全可用，以
        // SetConsoleMode 成功作为 VT 能力判定依据更可靠。
        if (IsAnsiEnabled)
            return true;

        IntPtr stdin = GetStdHandle(STD_INPUT_HANDLE);
        if (stdin == IntPtr.Zero || stdin == INVALID_HANDLE_VALUE) return false;

        if (!GetConsoleMode(stdin, out uint originalMode)) return false;

        uint probeMode = (originalMode | ENABLE_VIRTUAL_TERMINAL_INPUT)
                         & ~ENABLE_PROCESSED_INPUT
                         & ~ENABLE_ECHO_INPUT
                         & ~ENABLE_LINE_INPUT;
        if (!SetConsoleMode(stdin, probeMode)) return false;

        Console.Write("\x1b[c");

        byte[] buffer = new byte[256];
        int totalRead = 0;
        var deadline = DateTime.UtcNow.AddMilliseconds(Da1TimeoutMs);

        while (DateTime.UtcNow < deadline)
        {
            int remaining = Math.Max(1, (int)(deadline - DateTime.UtcNow).TotalMilliseconds);
            uint waitResult = WaitForSingleObject(stdin, Math.Min((uint)remaining, 50u));
            if (waitResult == WAIT_OBJECT_0)
            {
                int n = ReadRawBytes(stdin, buffer, totalRead, buffer.Length - totalRead);
                if (n > 0)
                {
                    totalRead += n;
                    if (ContainsDa1Response(buffer, totalRead))
                        break;
                }
            }
            else if (waitResult != WAIT_TIMEOUT)
            {
                break;
            }
        }

        SetConsoleMode(stdin, originalMode);
        return true;
    }

    private const int Da1TimeoutMs = 200;

    private static bool IsWindowsTerminal()
        => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WT_SESSION"));

    private static bool ContainsDa1Response(byte[] buffer, int length)
    {
        for (int i = 0; i < length - 1; i++)
        {
            if (buffer[i] == 0x1B && buffer[i + 1] == (byte)'[')
            {
                for (int j = i + 2; j < length; j++)
                {
                    if (buffer[j] == (byte)'c')
                        return true;
                    if (buffer[j] != ';' && buffer[j] != '?' && (buffer[j] < '0' || buffer[j] > '9'))
                        break;
                }
            }
        }
        return false;
    }

    private static unsafe int ReadRawBytes(IntPtr stdin, byte[] buffer, int offset, int count)
    {
        fixed (byte* p = &buffer[offset])
        {
            if (!ReadFile(stdin, (IntPtr)p, count, out int read, IntPtr.Zero))
                return 0;
            return read;
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
    private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadFile(IntPtr hFile, IntPtr lpBuffer, int nNumberOfBytesToRead, out int lpNumberOfBytesRead, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetCurrentConsoleFontEx(IntPtr hConsoleOutput, bool bMaximumWindow, ref CONSOLE_FONT_INFO_EX lpConsoleCurrentFontEx);

    private const int STD_OUTPUT_HANDLE = -11;
    private const int STD_INPUT_HANDLE = -10;
    private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);
    private const uint WAIT_OBJECT_0 = 0;
    private const uint WAIT_TIMEOUT = 0x00000102;
    private const uint ENABLE_VIRTUAL_TERMINAL_PROCESSING = 0x0004;
    private const uint ENABLE_VIRTUAL_TERMINAL_INPUT = 0x0200;
    private const uint ENABLE_PROCESSED_INPUT = 0x0001;
    private const uint ENABLE_LINE_INPUT = 0x0002;
    private const uint ENABLE_ECHO_INPUT = 0x0004;
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
