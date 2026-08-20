using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Server;
using MinorShift.Emuera.Terminal.Platform;
using Xunit;

namespace MinorShift.Emuera.Tests;

/// <summary>
/// Session.WaitForTurnAsync 三分支单元测试（重构补测——原零覆盖）。
/// 不调用 Start()：WaitForTurnAsync 只依赖 HttpSessionIO 的 _output Channel，
/// 无需游戏循环；注入/关闭经真实 HttpSessionIO 完成。
/// 另含 GetDisplaySnapshot 的未初始化返回 null 断言（GET /snapshot 503 的触发条件）。
/// </summary>
public class SessionWaitForTurnTests
{
    private static (Session session, HttpSessionIO io) CreateSession()
    {
        var hub = new OutputHub();
        var io = new HttpSessionIO(hub);
        var session = new Session(io, new SessionRegistryTests.NullTerminalSetup(), new ConfigData());
        return (session, io);
    }

    [Fact]
    public async Task Wait_returns_turn_after_WriteLine()
    {
        var (session, io) = CreateSession();
        using var sessionCleanup = session;

        const string turn = "{\"type\":\"turn\",\"value\":\"Hello\"}";
        io.WriteLine(turn);

        var result = await session.WaitForTurnAsync(1000, CancellationToken.None);

        Assert.Equal(TurnWaitStatus.Turn, result.Status);
        Assert.Equal(turn, result.Turn);
    }

    [Fact]
    public async Task Wait_times_out_when_no_output()
    {
        var (session, _) = CreateSession();
        using var sessionCleanup = session;

        var result = await session.WaitForTurnAsync(50, CancellationToken.None);

        Assert.Equal(TurnWaitStatus.Timeout, result.Status);
        Assert.Null(result.Turn);
    }

    [Fact]
    public async Task Wait_returns_closed_after_io_close()
    {
        var (session, io) = CreateSession();
        using var sessionCleanup = session;

        io.Close();

        var result = await session.WaitForTurnAsync(1000, CancellationToken.None);

        Assert.Equal(TurnWaitStatus.Closed, result.Status);
        Assert.Null(result.Turn);
    }

    [Fact]
    public async Task Wait_consumes_turns_fifo()
    {
        var (session, io) = CreateSession();
        using var sessionCleanup = session;

        io.WriteLine("turn-1");
        io.WriteLine("turn-2");

        var first = await session.WaitForTurnAsync(1000, CancellationToken.None);
        var second = await session.WaitForTurnAsync(1000, CancellationToken.None);

        Assert.Equal("turn-1", first.Turn);
        Assert.Equal("turn-2", second.Turn);
    }

    /// <summary>GET /snapshot 503 的触发条件：session 未初始化（_displayState == null）时 GetDisplaySnapshot() 返回 null。</summary>
    [Fact]
    public void Uninitialized_session_snapshot_is_null()
    {
        var (session, _) = CreateSession();
        using var sessionCleanup = session;
        Assert.Null(session.GetDisplaySnapshot());
    }

    [Fact]
    public async Task Acquire_drains_backlog_so_next_wait_sees_only_new_turn()
    {
        var (session, io) = CreateSession();
        using var sessionCleanup = session;
        var agent = ControlIdentity.Agent("agent-1");

        Assert.Equal(ControlAcquireStatus.Acquired, session.AcquireControl(agent).Control.Status);
        io.WriteLine("old-1");
        io.WriteLine("old-2");

        var drained = session.AcquireControl(agent);
        Assert.Equal(ControlAcquireStatus.Acquired, drained.Control.Status);
        Assert.Equal(2, drained.TurnsAdvanced);
        Assert.Equal("old-2", drained.Turn);

        io.WriteLine("new-1");
        var next = await session.WaitForTurnAsync(1000, agent, CancellationToken.None);
        Assert.Equal(TurnWaitStatus.Turn, next.Status);
        Assert.Equal("new-1", next.Turn);
    }

    [Fact]
    public async Task In_flight_wait_returns_control_lost_immediately_on_steal()
    {
        var (session, _) = CreateSession();
        using var sessionCleanup = session;
        var agent = ControlIdentity.Agent("agent-1");
        session.AcquireControl(agent);

        var watch = Stopwatch.StartNew();
        var waitTask = session.WaitForTurnAsync(5000, agent, CancellationToken.None);
        await Task.Delay(80);
        session.AcquireControl(ControlIdentity.User);
        var result = await waitTask;

        Assert.Equal(TurnWaitStatus.ControlLost, result.Status);
        Assert.Equal("control_changed", result.Reason);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Steal_racing_a_queued_turn_leaves_that_turn_for_acquire_drain()
    {
        var (session, io) = CreateSession();
        using var sessionCleanup = session;
        var agent = ControlIdentity.Agent("agent-1");
        session.AcquireControl(agent);

        var waitTask = session.WaitForTurnAsync(5000, agent, CancellationToken.None);
        await Task.Delay(80);
        io.WriteLine("raced-turn");
        var stolen = session.AcquireControl(ControlIdentity.User);
        var waitResult = await waitTask;

        Assert.Equal(TurnWaitStatus.ControlLost, waitResult.Status);
        Assert.Equal(1, stolen.TurnsAdvanced);
        Assert.Equal("raced-turn", stolen.Turn);
    }

    [Fact]
    public async Task Dispose_mid_wait_returns_closed()
    {
        var (session, _) = CreateSession();
        using var sessionCleanup = session;

        var waitTask = session.WaitForTurnAsync(5000, CancellationToken.None);
        await Task.Delay(80);
        session.Dispose();
        var result = await waitTask;

        // 拆除期在途等待应稳定 Closed（404），而非与 ControlLost（409）竞争
        Assert.Equal(TurnWaitStatus.Closed, result.Status);
        Assert.Null(result.Turn);
    }
}
