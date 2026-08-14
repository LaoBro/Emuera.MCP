using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace MinorShift.Emuera.Server;

/// <summary>
/// 基于内存 Channel 的 SessionIO，用于 HTTP 长轮询模式。
/// input/output 双 Channel，支持 async 读取与同步写入。
/// </summary>
internal sealed class HttpSessionIO : SessionIO
{
    private readonly IOutputBroadcaster _broadcaster;
    private readonly Channel<string> _input = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false
    });
    private readonly Channel<string> _output = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
    {
        SingleReader = false,
        SingleWriter = false
    });
    private volatile bool _closed;

    /// <summary>
    /// 注入 <see cref="IOutputBroadcaster"/> 旁路广播：所有 <see cref="WriteLine"/> 的 turn
    /// 既写入 HTTP 长轮询消费的 <c>_output</c> Channel，也经 broadcaster 广播给 WS 等观察者。
    /// 接抽象而非 <see cref="OutputHub"/> 具体类型，使本类可在单测中注入 mock broadcaster
    /// （<see cref="OutputHub"/> 是 <c>internal sealed</c> 无法继承做 mock）。
    /// </summary>
    public HttpSessionIO(IOutputBroadcaster broadcaster)
    {
        _broadcaster = broadcaster ?? throw new ArgumentNullException(nameof(broadcaster));
    }

    /// <summary>
    /// 异步读取一行输入。
    /// Channel 关闭后返回 null（同步版 ReadLine 行为一致）。
    /// CancellationToken 取消时抛 OperationCanceledException，由调用方处理。
    /// </summary>
    public override async Task<string?> ReadLineAsync(CancellationToken ct)
    {
        try
        {
            return await _input.Reader.ReadAsync(ct);
        }
        catch (ChannelClosedException)
        {
            return null;
        }
    }

    public override void WriteLine(string text)
    {
        if (_closed)
            return;
        _output.Writer.TryWrite(text);
        // 旁路广播：同一份 turn 字符串广播给所有 WS 等观察者，无需任何转换
        // （turn 已是合法的 TurnRecord v2 JSON，与 GET /turn 响应体字节一致）。
        _broadcaster.Publish(text);
    }

    /// <summary>
    /// 关闭 IO。幂等：多次调用安全。
    /// 调用后 ReadLineAsync 返回 null，WriteLine 丢弃数据。
    /// 同时通知 <see cref="IOutputBroadcaster"/> 完成所有观察者 Channel，使 WS 连接收到关闭帧。
    /// </summary>
    public override void Close()
    {
        if (_closed)
            return;
        _closed = true;
        _input.Writer.TryComplete();
        _output.Writer.TryComplete();
        _broadcaster.Complete();
    }

    public override bool IsConnected => !_closed;

    /// <summary>供 HttpGameServer.POST /input 调用：入队输入并唤醒读取方。</summary>
    public void EnqueueInput(string line)
    {
        if (_closed)
            return;
        _input.Writer.TryWrite(line);
    }

    /// <summary>
    /// 供 Session.WaitForTurnAsync 调用：异步等待并读取一个 output turn。
    /// Channel 关闭且队列空时返回 null（语义与同步 TryDequeueOutput 关闭后返回 false 一致）。
    /// CancellationToken 取消时抛 OperationCanceledException，由调用方处理。
    /// 注意：output Channel 配置为 SingleReader=false，多 reader 并发安全。
    /// </summary>
    public List<string> DrainOutput()
    {
        var turns = new List<string>();
        while (_output.Reader.TryRead(out var turn))
            turns.Add(turn);
        return turns;
    }

    public async Task<string?> ReadOutputAsync(CancellationToken ct)
    {
        try
        {
            return await _output.Reader.ReadAsync(ct);
        }
        catch (ChannelClosedException)
        {
            return null;
        }
    }
}
