using System;
using System.Runtime.InteropServices;

namespace MinorShift.Emuera.Terminal.Platform;

internal sealed class PosixTerminalSetup : ITerminalSetup, IDisposable
{
    public bool IsAnsiEnabled { get; private set; } = true;

    public bool TryEnableAnsi()
    {
        IsAnsiEnabled = true;
        return true;
    }

    public bool TrySetConsoleSize(int cols, int rows)
    {
        if (cols <= 0 || rows <= 0) return false;
        try
        {
            Console.Write($"\x1b[8;{rows};{cols}t");
            return true;
        }
        catch { return false; }
    }

    public string? DetectFont() => null;

    public bool TryPrepareVtInput()
    {
        if (OperatingSystem.IsWindows()) return false;
        if (_rawModeActive) return true;

        IntPtr raw = IntPtr.Zero;
        try
        {
            raw = Marshal.AllocHGlobal(TermiosBufferSize);
            if (tcgetattr(STDIN_FILENO, raw) != 0)
                return false;

            _origTermios = new byte[TermiosBufferSize];
            Marshal.Copy(raw, _origTermios, 0, TermiosBufferSize);

            cfmakeraw(raw);
            if (tcsetattr(STDIN_FILENO, TCSANOW, raw) != 0)
            {
                _origTermios = null;
                return false;
            }

            _rawModeActive = true;
            RegisterCleanupHooks();
            return true;
        }
        finally
        {
            if (raw != IntPtr.Zero) Marshal.FreeHGlobal(raw);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        RestoreTermios();
    }

    private void RestoreTermios()
    {
        if (!_rawModeActive || _origTermios == null) return;
        IntPtr buf = Marshal.AllocHGlobal(TermiosBufferSize);
        try
        {
            Marshal.Copy(_origTermios, 0, buf, TermiosBufferSize);
            _ = tcsetattr(STDIN_FILENO, TCSANOW, buf);
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
        _origTermios = null;
        _rawModeActive = false;
    }

    private void RegisterCleanupHooks()
    {
        if (_hooksRegistered) return;
        _hooksRegistered = true;
        AppDomain.CurrentDomain.ProcessExit += (_, _) => RestoreTermios();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            RestoreTermios();
        };
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int tcgetattr(int fd, IntPtr termios);

    [DllImport("libc", SetLastError = true)]
    private static extern int tcsetattr(int fd, int action, IntPtr termios);

    [DllImport("libc")]
    private static extern void cfmakeraw(IntPtr termios);

    private const int STDIN_FILENO = 0;
    private const int TCSANOW = 0;
    private const int TermiosBufferSize = 80;

    private byte[]? _origTermios;
    private bool _rawModeActive;
    private bool _disposed;
    private bool _hooksRegistered;

    [StructLayout(LayoutKind.Sequential)]
    private struct pollfd
    {
        public int fd;
        public short events;
        public short revents;
    }
}
