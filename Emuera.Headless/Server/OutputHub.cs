using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;

namespace MinorShift.Emuera.Server;

/// <summary>
/// 输出广播中枢（Hub 旁路模式）。
///
/// 游戏循环 <c>HttpSessionIO.WriteLine(turn)</c> 时，turn 字符串既推入 HTTP 长轮询消费
/// 的 <c>_output</c> Channel，也经本 Hub 广播给所有经 <see cref="Subscribe"/> 挂上来的
/// 旁路传输（WebSocket 连接等观察者）。
///
/// 设计要点：
/// - 纯转发，不解析、不持有业务状态，也不依赖 <c>Session</c>/协议层/引擎层。
/// - 每个订阅者拿到一个独立、独占的 <see cref="ChannelReader{T}"/>（晚加入者只收后续 turn）。
/// - 线程安全：游戏循环（Publish）与 WS 处理线程（Subscribe/Unsubscribe）并发访问。
/// - <see cref="Complete"/> 在 session 结束时调用，完成所有订阅者 Channel，使 WS 发送循环
///   自然结束并下发关闭帧。
/// </summary>
internal sealed class OutputHub
{
    private readonly object _subscribersLock = new();
    private readonly List<(ChannelReader<string> Reader, ChannelWriter<string> Writer)> _subscribers = new();
    private bool _completed;

    /// <summary>
    /// 注册一个新观察者，返回其独占的 <see cref="ChannelReader{T}"/>。
    /// 晚加入者只收连接之后新产生的 turn（订阅 Channel 不回放历史）。
    /// 若 Hub 已 <see cref="Complete"/>（session 已结束），返回已完成的 reader。
    /// </summary>
    public ChannelReader<string> Subscribe()
    {
        var channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        lock (_subscribersLock)
        {
            if (_completed)
            {
                channel.Writer.TryComplete();
                return channel.Reader;
            }

            _subscribers.Add((channel.Reader, channel.Writer));
        }

        return channel.Reader;
    }

    /// <summary>
    /// 向所有当前订阅者各推送一次 turn。已 <see cref="Complete"/> 后无操作。
    /// 新订阅者在本次 Publish 快照之后注册，将只收后续 turn（可接受的最终一致性）。
    /// </summary>
    public void Publish(string turn)
    {
        if (turn == null)
            return;

        List<ChannelWriter<string>> writers;
        lock (_subscribersLock)
        {
            if (_completed)
                return;
            writers = _subscribers.ConvertAll(s => s.Writer);
        }

        foreach (var writer in writers)
            writer.TryWrite(turn);
    }

    /// <summary>
    /// 移除一个观察者（WS 断开 / 取消订阅时调用）。同时完成其 writer 以结束其发送循环。
    /// 未找到对应 reader 时安全无操作。
    /// </summary>
    public void Unsubscribe(ChannelReader<string> reader)
    {
        lock (_subscribersLock)
        {
            var idx = _subscribers.FindIndex(s => s.Reader == reader);
            if (idx < 0)
                return;

            _subscribers[idx].Writer.TryComplete();
            _subscribers.RemoveAt(idx);
        }
    }

    /// <summary>
    /// 结束整个 Hub：完成所有订阅者 Channel 并清空列表。幂等。
    /// 由 <c>HttpSessionIO.Close()</c> 在 session 结束时调用，
    /// 使所有 WS 发送循环自然结束并下发关闭帧。
    /// </summary>
    public void Complete()
    {
        List<ChannelWriter<string>> writers;
        lock (_subscribersLock)
        {
            if (_completed)
                return;
            _completed = true;
            writers = _subscribers.ConvertAll(s => s.Writer);
            _subscribers.Clear();
        }

        foreach (var writer in writers)
            writer.TryComplete();
    }
}
