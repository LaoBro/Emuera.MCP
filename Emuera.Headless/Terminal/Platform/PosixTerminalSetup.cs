using System;

namespace MinorShift.Emuera.Terminal.Platform;

internal sealed class PosixTerminalSetup : ITerminalSetup
{
    public bool IsAnsiEnabled { get; private set; } = true;

    public bool TryEnableAnsi()
    {
        // POSIX 终端默认支持 VT/ANSI 序列
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
        catch (Exception) { return false; }
    }

    public string? DetectFont()
    {
        // POSIX 无可用的字体检测 API
        return null;
    }

    public bool TryProbeDa1()
    {
        // POSIX 终端通常支持 VT，假设 DA1 通过
        // 完整实现需发送 ESC[c + 非阻塞读取响应，留待后续
        return true;
    }
}
