using System;
using System.Threading;
using System.Threading.Tasks;

namespace MinorShift.Emuera.Server;

/// <summary>
/// 包装现有 stdin/stdout 的 SessionIO 实现，用于 JSONL 管道交互。
/// </summary>
internal sealed class ConsoleOutIO : SessionIO
{
    public static readonly ConsoleOutIO Instance = new();
    private ConsoleOutIO() { }

    /// <summary>
    /// 异步读取一行 stdin。管道模式 enableTimeout=false，ct=CancellationToken.None，
    /// 不需要真正支持取消。stdin 关闭时 ReadLineAsync 返回 null，RunLoopAsync 正常退出。
    /// </summary>
    public override async Task<string?> ReadLineAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return await Console.In.ReadLineAsync();
    }

    public override void WriteLine(string text) => Console.WriteLine(text);
    public override void Close() { }
    public override bool IsConnected => true;
}
