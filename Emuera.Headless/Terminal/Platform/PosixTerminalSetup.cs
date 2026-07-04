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

    public bool TryProbeDa1()
    {
        if (OperatingSystem.IsWindows()) return false;
        if (_rawModeActive) return true;

        IntPtr raw = IntPtr.Zero;
        try
        {
            raw = Marshal.AllocHGlobal(TermiosBufferSize);
            if (tcgetattr(STDIN_FILENO, raw) != 0)
                return false;

            byte[] orig = new byte[TermiosBufferSize];
            Marshal.Copy(raw, orig, 0, TermiosBufferSize);

            cfmakeraw(raw);
            if (tcsetattr(STDIN_FILENO, TCSANOW, raw) != 0)
                return false;

            Console.Write("\x1b[c");

            var pfd = new pollfd { fd = STDIN_FILENO, events = POLLIN };
            byte[] response = new byte[256];
            int totalRead = 0;
            var deadline = DateTime.UtcNow.AddMilliseconds(Da1TimeoutMs);
            bool responded = false;

            while (DateTime.UtcNow < deadline)
            {
                int remaining = Math.Max(0, (int)(deadline - DateTime.UtcNow).TotalMilliseconds);
                int pr = poll(ref pfd, 1, remaining);
                if (pr <= 0) break;

                int n;
                unsafe
                {
                    fixed (byte* p = response)
                    {
                        n = read(STDIN_FILENO, (IntPtr)(p + totalRead), response.Length - totalRead);
                    }
                }
                if (n <= 0) break;

                totalRead += n;
                if (ContainsDa1Response(response, totalRead))
                {
                    responded = true;
                    break;
                }
            }

            if (responded)
            {
                _origTermios = new byte[TermiosBufferSize];
                Buffer.BlockCopy(orig, 0, _origTermios, 0, TermiosBufferSize);
                _rawModeActive = true;
                RegisterCleanupHooks();
                return true;
            }

            _ = tcsetattr(STDIN_FILENO, TCSANOW, raw);
            return false;
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

    [DllImport("libc", SetLastError = true)]
    private static extern int tcgetattr(int fd, IntPtr termios);

    [DllImport("libc", SetLastError = true)]
    private static extern int tcsetattr(int fd, int action, IntPtr termios);

    [DllImport("libc")]
    private static extern void cfmakeraw(IntPtr termios);

    [DllImport("libc", SetLastError = true)]
    private static extern int read(int fd, IntPtr buf, int count);

    [DllImport("libc", SetLastError = true)]
    private static extern int poll(ref pollfd fds, int nfds, int timeout);

    private const int STDIN_FILENO = 0;
    private const int TCSANOW = 0;
    private const short POLLIN = 1;
    private const int Da1TimeoutMs = 200;
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
