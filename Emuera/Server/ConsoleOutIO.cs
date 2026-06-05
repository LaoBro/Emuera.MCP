using System;

namespace MinorShift.Emuera.Server;

/// <summary>
/// 包装现有 stdin/stdout 的 SessionIO 实现，用于兼容原有的 --headless 管道模式。
/// </summary>
internal sealed class ConsoleOutIO : SessionIO
{
    public static readonly ConsoleOutIO Instance = new();
    private ConsoleOutIO() { }

    public override string? ReadLine() => Console.ReadLine();
    public override void WriteLine(string text) => Console.WriteLine(text);
    public override void Close() { }
    public override bool IsConnected => true;
}
