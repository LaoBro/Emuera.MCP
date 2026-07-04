using System;
using System.IO;

namespace MinorShift.Emuera.Terminal.Platform;

internal sealed class PosixTerminalInput : ITerminalInput
{
    private readonly Stream _stdin;
    private bool _sgrMouseEnabled;
    private bool _disposed;

    public PosixTerminalInput()
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        _stdin = Console.OpenStandardInput();
    }

    public bool HasInputAvailable()
    {
        // Console.KeyAvailable 在 Unix 上通过 poll(fd, POLLIN, 0) 实现，
        // 可检测键盘和 VT 鼠标事件（都通过 stdin 传输）
        return Console.KeyAvailable;
    }

    public int ReadByte()
    {
        // 调用方约定：先检查 HasInputAvailable，再调用 ReadByte
        // 阻塞读取单字节
        int b = _stdin.ReadByte();
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
}
