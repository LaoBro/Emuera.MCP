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
    /// <summary>
    /// v1.1 不支持普通 stdin 管道可靠 timeout，统一走 Console.ReadLine() 阻塞读取，忽略 timeout 参数。
    /// 禁止 Task.Run(() => Console.ReadLine()) 后 Task.Wait(timeout) 的实现，
    /// 原因是超时 task 可能稍后消费下一行输入，导致 stdin 输入串轮。
    /// </summary>
    public override string? ReadLine(int timeoutMs) => Console.ReadLine();
    public override void WriteLine(string text) => Console.WriteLine(text);
    public override void Close() { }
    public override bool IsConnected => true;
}
