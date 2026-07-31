using System;
using System.Threading;
using System.Threading.Tasks;
using MinorShift.Emuera.Server;
using Xunit;

namespace MinorShift.Emuera.Tests;

/// <summary>
/// MauiBridgeIO 单测——issue 04 Seam 1。
/// 覆盖 Channel 读写 + close 语义。零 MAUI 依赖（MauiBridgeIO 本身只引 System.Threading.Channels）。
/// </summary>
public class MauiBridgeIOTests
{
    /// <summary>
    /// 用例 1：ReadLineAsync 阻塞 → EnqueueInput 唤醒返回入队 line。
    /// 验证 Channel 的 reader/writer 协作：reader 先 await（阻塞），writer 入队后 reader 唤醒。
    /// </summary>
    [Fact]
    public async Task ReadLineAsync_blocks_until_EnqueueInput_wakes_it()
    {
        var io = new MauiBridgeIO(_ => { });

        var readTask = io.ReadLineAsync(TestAsyncTimeoutCancellationToken());
        Assert.False(readTask.IsCompleted, "reader should block on empty channel");

        io.EnqueueInput("hello");

        var line = await readTask;
        Assert.Equal("hello", line);
    }

    /// <summary>
    /// 用例 2：Close() 后 ReadLineAsync 返回 null（ChannelClosedException 路径）。
    /// 验证 close 语义：已 await 的 reader 收到 channel 完成信号，返回 null 表示 EOF。
    /// </summary>
    [Fact]
    public async Task ReadLineAsync_returns_null_after_Close()
    {
        var io = new MauiBridgeIO(_ => { });

        var readTask = io.ReadLineAsync(TestAsyncTimeoutCancellationToken());
        Assert.False(readTask.IsCompleted);

        io.Close();

        var line = await readTask;
        Assert.Null(line);
    }

    /// <summary>
    /// 用例 3：Close() 后 WriteLine 不调 _onTurn。
    /// 验证 close 后输出丢弃——_onTurn 回调不应被触发。
    /// </summary>
    [Fact]
    public void WriteLine_does_not_invoke_onTurn_after_Close()
    {
        var invoked = false;
        var io = new MauiBridgeIO(_ => invoked = true);

        io.Close();
        io.WriteLine("anything");

        Assert.False(invoked, "_onTurn must not be invoked after Close");
    }

    /// <summary>
    /// 用例 4：Close() 幂等（多次调用安全）。
    /// 验证 volatile _closed 守卫：第二次及后续 Close 调用不应抛异常，也不应重复完成 Channel。
    /// </summary>
    [Fact]
    public void Close_is_idempotent()
    {
        var io = new MauiBridgeIO(_ => { });

        io.Close();
        io.Close();
        io.Close();

        Assert.False(io.IsConnected);
    }

    /// <summary>
    /// 用例 5：IsConnected 状态变化——构造时 true，Close 后 false。
    /// </summary>
    [Fact]
    public void IsConnected_reflects_closed_state()
    {
        var io = new MauiBridgeIO(_ => { });

        Assert.True(io.IsConnected, "freshly constructed IO must be connected");

        io.Close();

        Assert.False(io.IsConnected, "IsConnected must be false after Close");
    }

    /// <summary>
    /// 用例 6：EnqueueInput 在 Close() 后丢弃（TryWrite 返 false 不抛）。
    /// 验证 close 后入队安全：Channel writer 已完成，TryWrite 返 false 但不抛异常。
    /// </summary>
    [Fact]
    public void EnqueueInput_is_silently_dropped_after_Close()
    {
        var io = new MauiBridgeIO(_ => { });

        io.Close();

        var ex = Record.Exception(() => io.EnqueueInput("dropped"));
        Assert.Null(ex);
    }

    /// <summary>
    /// 用例 7：WriteLine 调用 _onTurn 传入完整文本（happy path）。
    /// 验证未关闭时回调被触发且收到原始字符串。
    /// </summary>
    [Fact]
    public void WriteLine_invokes_onTurn_with_text_when_connected()
    {
        string? received = null;
        var io = new MauiBridgeIO(text => received = text);

        io.WriteLine("turn-json-payload");

        Assert.Equal("turn-json-payload", received);
    }

    /// <summary>
    /// 用例 8：构造函数 null 检查——onTurn 为 null 时抛 ArgumentNullException。
    /// </summary>
    [Fact]
    public void Constructor_rejects_null_onTurn()
    {
        Assert.Throws<ArgumentNullException>(() => new MauiBridgeIO(null!));
    }

    /// <summary>
    /// 用例 9：WriteMessage 调用 _onMessage 传入完整文本（无限循环确认弹窗推送路径）。
    /// 缺省 onMessage 参数时为空实现，不抛异常。
    /// </summary>
    [Fact]
    public void WriteMessage_invokes_onMessage_with_text_when_connected()
    {
        string? received = null;
        var io = new MauiBridgeIO(_ => { }, text => received = text);

        io.WriteMessage("infinite-loop-prompt-json");

        Assert.Equal("infinite-loop-prompt-json", received);
    }

    /// <summary>
    /// 用例 10：Close() 后 WriteMessage 不调 _onMessage（与 WriteLine 的丢弃语义一致）。
    /// </summary>
    [Fact]
    public void WriteMessage_does_not_invoke_onMessage_after_Close()
    {
        var invoked = false;
        var io = new MauiBridgeIO(_ => { }, _ => invoked = true);

        io.Close();
        io.WriteMessage("anything");

        Assert.False(invoked, "_onMessage must not be invoked after Close");
    }

    /// <summary>
    /// 用例 11：缺省 onMessage 时 WriteMessage 是安全 no-op（无回调不抛）。
    /// </summary>
    [Fact]
    public void WriteMessage_is_safe_noop_without_onMessage()
    {
        var io = new MauiBridgeIO(_ => { });

        var ex = Record.Exception(() => io.WriteMessage("anything"));
        Assert.Null(ex);
    }

    /// <summary>
    /// 用例 12：MAUI 桥接是交互通道——SupportsInteractivePrompt 为 true（无限循环确认弹窗可用）。
    /// </summary>
    [Fact]
    public void MauiBridgeIO_supports_interactive_prompt()
    {
        var io = new MauiBridgeIO(_ => { });
        Assert.True(io.SupportsInteractivePrompt);
    }

    /// <summary>
    /// 创建 5 秒超时的 CancellationToken——防止测试挂死（Channel 语义 bug 时 reader 永久阻塞）。
    /// 测试预期内的事件应在毫秒级完成，5 秒是宽松上限。
    /// </summary>
    private static CancellationToken TestAsyncTimeoutCancellationToken() =>
        new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token;
}
