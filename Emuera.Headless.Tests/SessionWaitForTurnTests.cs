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
}
