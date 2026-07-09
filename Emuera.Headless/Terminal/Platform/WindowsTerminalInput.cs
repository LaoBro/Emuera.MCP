using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;

namespace MinorShift.Emuera.Terminal.Platform;

internal sealed class WindowsTerminalInput : ITerminalInput
{
    private readonly IntPtr _stdinHandle;
    private readonly uint _originalInputMode;
    private bool _inputModeSet;
    private bool _sgrMouseEnabled;
    private bool _disposed;

    // ADR-0007：后台读取线程。控制台句柄的 ReadFile 是阻塞调用，且 WaitForSingleObject
    // 在 ConPTY 下会误报 signaled（phantom 事件 / 焦点 / 非键盘事件），导致 poll 循环中
    // ReadFile 阻塞、TINPUT 超时饿死。专用线程阻塞在 ReadFile 上将字节推入无锁队列，
    // poll 循环非阻塞地出队——既保证不丢字节，又让超时检查每轮都能执行。
    private readonly Thread _readerThread;
    private readonly ConcurrentQueue<byte> _readQueue = new();
    private volatile bool _stopping;

    public WindowsTerminalInput()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        if (Console.IsInputRedirected) throw new InvalidOperationException("stdin is redirected");

        _stdinHandle = GetStdHandle(STD_INPUT_HANDLE);
        if (_stdinHandle == IntPtr.Zero || _stdinHandle == INVALID_HANDLE_VALUE)
            throw new InvalidOperationException("Failed to get stdin handle");

        if (!GetConsoleMode(_stdinHandle, out _originalInputMode))
            throw new InvalidOperationException("GetConsoleMode failed");

        // 只启用 VT 输入。ConPTY/终端将键盘、SGR 鼠标、resize 全部以 VT 序列投递到 stdin，
        // 无需 legacy 事件标志（ENABLE_WINDOW_INPUT / ENABLE_MOUSE_INPUT）。
        uint newMode = (_originalInputMode
                        | ENABLE_VIRTUAL_TERMINAL_INPUT
                        | ENABLE_EXTENDED_FLAGS)
                      & ~ENABLE_PROCESSED_INPUT
                      & ~ENABLE_ECHO_INPUT
                      & ~ENABLE_LINE_INPUT
                      & ~ENABLE_QUICK_EDIT_MODE
                      & ~ENABLE_WINDOW_INPUT
                      & ~ENABLE_MOUSE_INPUT;

        if (!SetConsoleMode(_stdinHandle, newMode))
            throw new InvalidOperationException("SetConsoleMode failed");

        _inputModeSet = true;

        _readerThread = new Thread(ReaderLoop) { IsBackground = true, Name = "vt-stdin-reader" };
        _readerThread.Start();
    }

    private unsafe void ReaderLoop()
    {
        byte[] buf = new byte[1];
        while (!_stopping)
        {
            fixed (byte* p = buf)
            {
                if (!ReadFile(_stdinHandle, (IntPtr)p, 1, out int read, IntPtr.Zero) || read == 0)
                    break; // EOF 或错误
            }
            _readQueue.Enqueue(buf[0]);
        }
    }

    public bool HasInputAvailable() => !_readQueue.IsEmpty;

    public int ReadByte() => _readQueue.TryDequeue(out byte b) ? b : -1;

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
        _stopping = true;

        DisableSgrMouse();

        if (_inputModeSet && _stdinHandle != IntPtr.Zero && _stdinHandle != INVALID_HANDLE_VALUE)
        {
            // 恢复原始模式可能解除 ReadFile 阻塞（模式变更触发控制台刷新输入状态）。
            SetConsoleMode(_stdinHandle, _originalInputMode);
            _inputModeSet = false;
        }
        // 后台线程为 IsBackground=true，进程退出时自动终止；不 Join（ReadFile 可能仍阻塞）。
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
