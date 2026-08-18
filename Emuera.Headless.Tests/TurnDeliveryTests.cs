using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MinorShift.Emuera.Server;
using Xunit;

namespace MinorShift.Emuera.Tests;

/// <summary>
/// TurnDelivery 深模块单测：注入 fake ITurnSink + IControlSignal，用 TCS 确定性编排分支。
/// 可确定性钳制的分支在此覆盖；依赖真实调度交叠的「读 turn 同时易主」类竞态保留在
/// SessionWaitForTurnTests（真实对象接线回归）兜底——两层互补。
/// </summary>
public class TurnDeliveryTests
{
    private static (TurnDelivery delivery, FakeTurnSink sink, FakeSignal signal) Create()
    {
        var sink = new FakeTurnSink();
        var signal = new FakeSignal();
        return (new TurnDelivery(sink, signal), sink, signal);
    }

    [Fact]
    public async Task Wait_returns_queued_turn()
    {
        var (delivery, sink, _) = Create();
        sink.ProvideTurn("T");

        var r = await delivery.WaitForTurnAsync(1000, CancellationToken.None);

        Assert.Equal(TurnWaitStatus.Turn, r.Status);
        Assert.Equal("T", r.Turn);
    }

    [Fact]
    public async Task Wait_times_out_when_no_output()
    {
        var (delivery, _, _) = Create();

        var r = await delivery.WaitForTurnAsync(50, CancellationToken.None);

        Assert.Equal(TurnWaitStatus.Timeout, r.Status);
        Assert.Null(r.Turn);
    }

    [Fact]
    public async Task Wait_returns_closed_when_stream_ends_with_null()
    {
        var (delivery, sink, _) = Create();
        sink.ProvideTurn(null);

        var r = await delivery.WaitForTurnAsync(1000, CancellationToken.None);

        Assert.Equal(TurnWaitStatus.Closed, r.Status);
    }

    [Fact]
    public async Task Not_controller_fails_fast_without_waiting()
    {
        var (delivery, _, signal) = Create();
        signal.Current = ControlIdentity.Agent("other");

        var r = await delivery.WaitForTurnAsync(1000, ControlIdentity.Agent("me"), CancellationToken.None);

        Assert.Equal(TurnWaitStatus.ControlLost, r.Status);
        Assert.Equal("not_controller", r.Reason);
    }

    [Fact]
    public async Task Owner_changed_while_parked_returns_control_lost()
    {
        var (delivery, _, signal) = Create();
        signal.Current = ControlIdentity.Agent("agent-1");

        var wait = delivery.WaitForTurnAsync(5000, ControlIdentity.Agent("agent-1"), CancellationToken.None);
        await Task.Yield();
        signal.FireOwnerChanged();
        var r = await wait;

        Assert.Equal(TurnWaitStatus.ControlLost, r.Status);
        Assert.Equal("control_changed", r.Reason);
    }

    [Fact]
    public async Task External_cancellation_propagates()
    {
        var (delivery, _, _) = Create();
        using var cts = new CancellationTokenSource();

        var wait = delivery.WaitForTurnAsync(5000, cts.Token);
        await Task.Yield();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await wait);
    }

    [Fact]
    public async Task End_of_game_delivers_final_turn_to_parked_waiter()
    {
        var (delivery, sink, _) = Create();

        var wait = delivery.WaitForTurnAsync(5000, CancellationToken.None);
        await Task.Yield();
        delivery.EndOfGame("FINAL");
        var r = await wait;

        Assert.Equal(TurnWaitStatus.Turn, r.Status);
        Assert.Equal("FINAL", r.Turn);
        Assert.True(delivery.IsFinalTurnDelivered);
        Assert.True(sink.SinkClosed);
    }

    [Fact]
    public async Task Final_turn_is_delivered_exactly_once()
    {
        var (delivery, sink, _) = Create();
        delivery.EndOfGame("FINAL");

        // 首次等待消费 final turn
        var first = await delivery.WaitForTurnAsync(50, CancellationToken.None);
        Assert.Equal(TurnWaitStatus.Turn, first.Status);
        Assert.Equal("FINAL", first.Turn);

        // delivered 旗标已置位后，再到的多余 turn 被丢弃（防御性 continue → 超时）
        sink.ProvideTurn("EXTRA");
        var second = await delivery.WaitForTurnAsync(50, CancellationToken.None);

        Assert.Equal(TurnWaitStatus.Timeout, second.Status);
        Assert.True(delivery.IsFinalTurnDelivered);
    }

    [Fact]
    public void Drain_on_acquire_returns_backlog_fifo()
    {
        var (delivery, sink, _) = Create();

        sink.WriteLine("a");
        sink.WriteLine("b");
        var (advanced, last) = delivery.DrainOnAcquire();

        Assert.Equal(2, advanced);
        Assert.Equal("b", last);
    }

    [Fact]
    public async Task End_of_game_closes_sink_and_signals_ended()
    {
        var (delivery, sink, signal) = Create();

        delivery.EndOfGame(null);

        Assert.True(sink.SinkClosed);
        Assert.Equal("game_ended", signal.LastEndedType);
    }

    private sealed class FakeTurnSink : ITurnSink
    {
        private readonly ConcurrentQueue<string?> _available = new();
        private TaskCompletionSource<string?>? _pending;
        public List<string> UnreadLog { get; } = new();
        public bool SinkClosed { get; private set; }

        private void Push(string? value)
        {
            var pending = _pending;
            if (pending != null)
            {
                _pending = null;
                pending.TrySetResult(value);
            }
            else
            {
                _available.Enqueue(value);
            }
        }

        public Task<string?> ReadOutputAsync(CancellationToken ct)
        {
            if (_available.TryDequeue(out var value))
                return Task.FromResult(value);
            var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending = tcs;
            ct.Register(() => tcs.TrySetCanceled(ct));
            return tcs.Task;
        }

        public Task? PendingRead => _pending?.Task;

        public void ProvideTurn(string? turn) => Push(turn);

        public void UnreadOutput(string turn)
        {
            UnreadLog.Add(turn);
            Push(turn);
        }

        public void WriteLine(string text) => Push(text);

        public List<string> DrainOutput()
        {
            var list = new List<string>();
            while (_available.TryDequeue(out var v))
                if (v != null)
                    list.Add(v);
            return list;
        }

        public void Close() => SinkClosed = true;
    }

    private sealed class FakeSignal : IControlSignal
    {
        private TaskCompletionSource _ownerChanged = New();
        public ControlIdentity? Current { get; set; }
        public string? LastEndedType { get; private set; }

        public ControlWaitSnapshot CaptureWaitSnapshot() => new(Current, _ownerChanged.Task);

        public bool IsCurrent(ControlIdentity identity) => Current.HasValue && Current.Value.Matches(identity);

        public ControlEvent End(string type)
        {
            LastEndedType = type;
            var info = Current is { } c ? new ControllerInfo(c.KindName, c.Token, null) : null;
            FireOwnerChanged();
            return new ControlEvent(type, null, DateTimeOffset.UtcNow, info, "ended");
        }

        public void FireOwnerChanged()
        {
            var old = _ownerChanged;
            _ownerChanged = New();
            old.TrySetResult();
        }

        public static TaskCompletionSource New() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}