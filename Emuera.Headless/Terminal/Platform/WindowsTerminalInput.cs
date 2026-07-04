using System;
using System.Runtime.InteropServices;

namespace MinorShift.Emuera.Terminal.Platform;

internal sealed class WindowsTerminalInput : ITerminalInput
{
    private readonly IntPtr _stdinHandle;
    private readonly uint _originalInputMode;
    private bool _inputModeSet;
    private bool _sgrMouseEnabled;
    private bool _disposed;

    public WindowsTerminalInput()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        if (Console.IsInputRedirected) throw new InvalidOperationException("stdin is redirected");

        _stdinHandle = GetStdHandle(STD_INPUT_HANDLE);
        if (_stdinHandle == IntPtr.Zero || _stdinHandle == INVALID_HANDLE_VALUE)
            throw new InvalidOperationException("Failed to get stdin handle");

        if (!GetConsoleMode(_stdinHandle, out _originalInputMode))
            throw new InvalidOperationException("GetConsoleMode failed");

        uint newMode = (_originalInputMode
                        | ENABLE_VIRTUAL_TERMINAL_INPUT
                        | ENABLE_EXTENDED_FLAGS
                        | ENABLE_WINDOW_INPUT
                        | ENABLE_MOUSE_INPUT)
                      & ~ENABLE_PROCESSED_INPUT
                      & ~ENABLE_ECHO_INPUT
                      & ~ENABLE_LINE_INPUT
                      & ~ENABLE_QUICK_EDIT_MODE;

        if (!SetConsoleMode(_stdinHandle, newMode))
            throw new InvalidOperationException("SetConsoleMode failed");

        _inputModeSet = true;
    }

    public bool HasInputAvailable()
        => GetNumberOfConsoleInputEvents(_stdinHandle, out uint count) && count > 0;

    public unsafe int ReadByte()
    {
        byte[] buf = new byte[1];
        fixed (byte* p = buf)
        {
            if (!ReadFile(_stdinHandle, (IntPtr)p, 1, out int read, IntPtr.Zero) || read == 0)
                return -1;
        }
        return buf[0];
    }

    public void EnableSgrMouse()
    {
        if (_sgrMouseEnabled) return;
        Console.Write("\x1b[?1000h\x1b[?1006h");
        _sgrMouseEnabled = true;
    }

    public void DisableSgrMouse()
    {
        if (!_sgrMouseEnabled) return;
        Console.Write("\x1b[?1006l\x1b[?1000l");
        _sgrMouseEnabled = false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        DisableSgrMouse();

        if (_inputModeSet && _stdinHandle != IntPtr.Zero && _stdinHandle != INVALID_HANDLE_VALUE)
        {
            SetConsoleMode(_stdinHandle, _originalInputMode);
            _inputModeSet = false;
        }
    }

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
}
