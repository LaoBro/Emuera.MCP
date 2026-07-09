using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MinorShift.Emuera.GameView;
using MinorShift.Emuera.Terminal.Platform;
using MinorShift.Emuera.UI.Game;

namespace MinorShift.Emuera.Server;

internal sealed class Session : IDisposable
{
    public string Id { get; } = Guid.NewGuid().ToString("N")[..8];
    public DateTimeOffset CreatedAt { get; } = DateTimeOffset.UtcNow;
    public bool IsRunning => _gameTask != null && !_gameTask.IsCompleted;
    public bool HasEnded { get; private set; }
    public HttpSessionIO IO => _io;
    public string StateString => _console.State.ToString();
    public bool IsFinalTurnDelivered => _finalTurnDelivered;

    private readonly HeadlessConsole _ui;
    private readonly EmueraConsole _console;
    private readonly AgentJsonlProtocol _protocol;
    private readonly HttpSessionIO _io;
    private Task? _gameTask;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _turnLock = new();
    private bool _finalTurnReady;
    private bool _finalTurnDelivered;
    private bool _disposed;

    public Session(HttpSessionIO io, ITerminalSetup terminalSetup)
    {
        _io = io;
        _ui = new HeadlessConsole();
        _console = new EmueraConsole(_ui, terminalSetup);
        _protocol = new AgentJsonlProtocol(_console, _ui, io);
        _console.SetAgentBridge(_protocol);
    }

    public void Start()
    {
        _gameTask = Task.Run(GameLoopAsync);
    }

    private async Task GameLoopAsync()
    {
        // ADR-0008：会话作用域 scope——Current 仅在此游戏 task 的 async 上下文存活。
        // scope.Dispose 自动 Pfc.Dispose + Current 归 null，替代旧的 Reset()。
        using var scope = GlobalStatic.OpenScope();
        Program.LoadFonts();
        try
        {
            await _console.Initialize();
            await _protocol.RunLoopAsync(enableTimeout: true, _cts.Token);
        }
        catch (GameExitException)
        {
            // 脚本 QUIT/EXIT：正常终结 session，走 finally 清理（BuildFinalTurn/IO.Close）
        }
        catch (Exception ex)
        {
            AgentLog.Instance.Write("session game loop exception: " + ex);
            _io.WriteLine(JsonSerializer.Serialize(new { error = ex.ToString(), state = _console.State.ToString() }));
        }
        finally
        {
            // 先标记最终 turn 就绪，再写入队列，避免竞态下漏标记 delivered
            lock (_turnLock)
            {
                _finalTurnReady = true;
            }
            try
            {
                var finalTurn = _protocol.BuildFinalTurn();
                if (finalTurn != null)
                    _io.WriteLine(finalTurn);
            }
            catch (Exception ex)
            {
                // 最终 turn 生成失败不阻断 Dispose；HasEnded 会让 GET /turn 返回 404
                AgentLog.Instance.Write("final turn build failed: " + ex.Message);
            }
            _protocol.Stop();
            _console.Dispose();
            // 关闭 IO：Complete output Channel 后 TryTakeTurn 仍能 TryRead 已写入数据，
            // 读完后返回 false；同时 Complete input Channel 防止后续 EnqueueInput。
            _io.Close();
            HasEnded = true;
            // scope 在 using 块退出时自动 Dispose（Pfc.Dispose + Current 归 null）。
            // 旧 Reset() 已删除——RAII 单一归属。
        }
    }

    /// <summary>
    /// 异步等待一个 turn。用于 GET /turn 长轮询。
    /// - 拿到 turn（含 finalTurn）→ 返回 Turn，finalTurn 交付后标记 _finalTurnDelivered
    /// - 25s 超时 → 返回 Timeout（HTTP 204）
    /// - Channel 关闭（session 结束）→ 返回 Closed（HTTP 404）
    /// - externalCt 取消（服务器关闭）→ 抛 OperationCanceledException 向上传播
    /// 
    /// finalTurn 一次性交付语义：依赖 Channel 多 reader 原子性 + _turnLock 内检查 _finalTurnReady。
    /// 与原 TryTakeTurn 语义一致：_finalTurnReady=true 时，下一个取出的 turn 标记为已交付。
    /// </summary>
    public async Task<TurnWaitResult> WaitForTurnAsync(int timeoutMs, CancellationToken externalCt)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(externalCt);
        linkedCts.CancelAfter(timeoutMs);
        var ct = linkedCts.Token;

        while (true)
        {
            string? turn;
            try
            {
                turn = await _io.ReadOutputAsync(ct);
            }
            catch (OperationCanceledException)
            {
                // 服务器关闭：向上传播，调用方不应写 response
                if (externalCt.IsCancellationRequested)
                    throw;
                // 超时：返回 204
                return new TurnWaitResult(TurnWaitStatus.Timeout, null);
            }

            // Channel 关闭且队列空：session 已结束
            if (turn == null)
                return new TurnWaitResult(TurnWaitStatus.Closed, null);

            // 拿到 turn，在 lock 内检查 finalTurn 标记
            lock (_turnLock)
            {
                if (_finalTurnDelivered)
                    continue; // 防御性：已交付，丢弃多余的 turn（理论上不会发生）
                if (_finalTurnReady)
                    _finalTurnDelivered = true;
                return new TurnWaitResult(TurnWaitStatus.Turn, turn);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _cts.Cancel();
        _protocol?.Stop();
        _io?.Close();
        _console?.Dispose();

        // 等待游戏循环（含其 finally 中的清理）彻底结束，避免旧 loop 仍在使用 scope 时
        // 提前释放/复用。scope 在 GameLoopAsync 的 using 块退出时自动 Dispose，
        // join 确保游戏循环一定先退出，scope 释放时无 dangling 读取。
        try
        {
            _gameTask?.GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            AgentLog.Instance.Write("session game loop ended with error during dispose: " + ex);
        }
    }
}

/// <summary>WaitForTurnAsync 的等待结果状态。</summary>
internal enum TurnWaitStatus
{
    /// <summary>拿到一个 turn（含 finalTurn）。</summary>
    Turn,
    /// <summary>等待超时（HTTP 204）。</summary>
    Timeout,
    /// <summary>Channel 关闭，session 已结束（HTTP 404）。</summary>
    Closed
}

/// <summary>WaitForTurnAsync 的返回值：状态 + turn 内容（仅 Status=Turn 时非 null）。</summary>
internal readonly record struct TurnWaitResult(TurnWaitStatus Status, string? Turn);
