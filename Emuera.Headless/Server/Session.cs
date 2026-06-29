using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MinorShift.Emuera.GameView;
using MinorShift.Emuera.UI.Game;

namespace MinorShift.Emuera.Server;

internal sealed class Session : IDisposable
{
    public string Id { get; } = Guid.NewGuid().ToString("N")[..8];
    public DateTimeOffset CreatedAt { get; } = DateTimeOffset.UtcNow;
    public bool IsRunning => _gameThread?.IsAlive == true;
    public bool HasEnded { get; private set; }
    public HttpSessionIO IO => _io;
    public string StateString => _console.State.ToString();
    public bool IsFinalTurnDelivered => _finalTurnDelivered;

    private readonly HeadlessConsole _ui;
    private readonly EmueraConsole _console;
    private readonly AgentJsonlProtocol _protocol;
    private readonly HttpSessionIO _io;
    private Thread? _gameThread;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _turnLock = new();
    private bool _finalTurnReady;
    private bool _finalTurnDelivered;
    private bool _disposed;

    public Session(HttpSessionIO io)
    {
        _io = io;
        _ui = new HeadlessConsole();
        _console = new EmueraConsole(_ui);
        _protocol = new AgentJsonlProtocol(_console, _ui, io);
        _console.SetAgentBridge(_protocol);
    }

    public void Start()
    {
        _gameThread = new Thread(GameLoop)
        {
            IsBackground = true,
            Name = $"Session-{Id}"
        };
        _gameThread.Start();
    }

    private void GameLoop()
    {
        try
        {
            _console.Initialize().Wait();
            _protocol.RunLoop(enableTimeout: true, _cts.Token);
        }
        catch (Exception ex)
        {
            _io.WriteLine(JsonSerializer.Serialize(new { error = ex.Message, state = _console.State.ToString() }));
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
            catch
            {
                // 最终 turn 生成失败不阻断 Dispose；HasEnded 会让 GET /turn 返回 404
            }
            _protocol.Stop();
            _console.Dispose();
            HasEnded = true;
        }
    }

    /// <summary>
    /// 原子地从 IO 队列取出 turn。若是最终 turn（_finalTurnReady），标记为已交付，
    /// 后续调用返回 false。用于 GET /turn 实现“返回最终 turn 一次后 404”。
    /// </summary>
    public bool TryTakeTurn(out string? turn)
    {
        lock (_turnLock)
        {
            if (_finalTurnDelivered)
            {
                turn = null;
                return false;
            }

            if (_io.TryDequeueOutput(out turn) && turn != null)
            {
                if (_finalTurnReady)
                    _finalTurnDelivered = true;
                return true;
            }

            turn = null;
            return false;
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
    }
}
