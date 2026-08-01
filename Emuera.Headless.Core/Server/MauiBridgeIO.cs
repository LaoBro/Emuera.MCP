using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace MinorShift.Emuera.Server;

/// <summary>
/// MAUI 同进程桥接 <see cref="SessionIO"/> 实现——issue 04 / spec ID3。
/// 替代 HTTP 环回：游戏循环写入的 turn JSON 经 <see cref="WriteLine"/> 走 <c>_onTurn</c> 回调
/// 直达 MAUI <c>BridgeHost</c>（由其在回调内 <c>Dispatcher.Dispatch</c> 切 UI 线程投递给 WebView）；
/// WebView 端 Vue postMessage 的输入经 <see cref="EnqueueInput"/> 入 <c>_input</c> Channel，
/// 由游戏循环 <see cref="ReadLineAsync"/> 异步消费。
/// </summary>
/// <remarks>
/// 零 MAUI/Android/Windows 引用——仅依赖 <see cref="System.Threading.Channels"/> + <see cref="System"/>。
/// 放 <c>Emuera.Headless.Core</c> 而非 MAUI 项目，使 MAUI 引 Core 即得桥接 IO，且单测可直测。
/// <see cref="WriteLine"/> 的 <c>_onTurn</c> 回调在游戏循环线程执行，调用方需自行切 UI 线程。
/// </remarks>
internal sealed class MauiBridgeIO : SessionIO
{
    private readonly Channel<string> _input = Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly Action<string> _onTurn;
    private volatile bool _closed;

    /// <param name="onTurn">
    /// turn 回调——每次 <see cref="WriteLine"/> 在未关闭时同步调用，参数为 turn JSON 字符串。
    /// 在游戏循环线程执行，调用方（MAUI <c>BridgeHost</c>）需在回调内 <c>Dispatcher.Dispatch</c> 切 UI 线程。
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="onTurn"/> 为 null。</exception>
    public MauiBridgeIO(Action<string> onTurn)
    {
        _onTurn = onTurn ?? throw new ArgumentNullException(nameof(onTurn));
    }

    /// <summary>
    /// 异步读取一行输入。
    /// Channel 关闭后返回 null（同步 EOF 语义）；CancellationToken 取消时抛 <see cref="OperationCanceledException"/>。
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

    /// <summary>
    /// 同步输出一个 turn——未关闭时调 <c>_onTurn</c>，关闭后丢弃。
    /// 不入 Channel：MAUI 单 WebView 场景无广播需求，回调直投避免不必要的中转。
    /// </summary>
    public override void WriteLine(string text)
    {
        if (_closed)
            return;
        _onTurn(text);
    }

    /// <summary>
    /// 关闭 IO。幂等：多次调用安全。
    /// 调用后 <see cref="ReadLineAsync"/> 返回 null，<see cref="WriteLine"/> 丢弃数据，
    /// <see cref="EnqueueInput"/> 静默丢弃（<see cref="ChannelWriter{T}.TryWrite"/> 返 false 不抛）。
    /// </summary>
    public override void Close()
    {
        if (_closed)
            return;
        _closed = true;
        _input.Writer.TryComplete();
    }

    public override bool IsConnected => !_closed;

    /// <summary>
    /// 供 <c>IJsBridge.InputReceived</c> 调用：入队 Vue 提交的输入并唤醒 <see cref="ReadLineAsync"/>。
    /// 关闭后静默丢弃（<see cref="ChannelWriter{T}.TryWrite"/> 返 false 不抛）。
    /// </summary>
    public void EnqueueInput(string line)
    {
        if (_closed)
            return;
        _input.Writer.TryWrite(line);
    }
}
