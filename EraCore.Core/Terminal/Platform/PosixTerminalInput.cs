using System;
using System.Runtime.InteropServices;

namespace MinorShift.Emuera.Terminal.Platform;

internal sealed class PosixTerminalInput : ITerminalInput
{
    private bool _sgrMouseEnabled;
    private bool _disposed;

    public PosixTerminalInput()
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
    }

    public bool HasInputAvailable()
    {
        var pfd = new pollfd { fd = STDIN_FILENO, events = POLLIN };
        int ret = poll(ref pfd, 1, 0);
        return ret > 0;
    }

    public int ReadByte()
    {
        byte b = 0;
        unsafe
        {
            int ret = read(STDIN_FILENO, (IntPtr)(&b), 1);
            if (ret <= 0) return -1;
        }
        return b;
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
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int read(int fd, IntPtr buf, int count);

    [DllImport("libc", SetLastError = true)]
    private static extern int poll(ref pollfd fds, int nfds, int timeout);

    private const int STDIN_FILENO = 0;
    private const short POLLIN = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct pollfd
    {
        public int fd;
        public short events;
        public short revents;
    }
}
