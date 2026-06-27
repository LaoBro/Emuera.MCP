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
    public DateTimeOffset LastActivityAt { get; private set; }
    public bool IsRunning => _gameThread?.IsAlive == true;

    private readonly HeadlessConsole _ui;
    private readonly EmueraConsole _console;
    private readonly AgentJsonlProtocol _protocol;
    private readonly HttpSessionIO _io;
    private Thread? _gameThread;
    private readonly CancellationTokenSource _cts = new();
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
            _protocol.Stop();
            _console.Dispose();
        }
    }

    public void Touch() => LastActivityAt = DateTimeOffset.UtcNow;

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
