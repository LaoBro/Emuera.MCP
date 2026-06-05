using System;
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
    private readonly SessionIO _io;
    private Thread? _gameThread;
    private readonly CancellationTokenSource _cts = new();

    public Session(SessionIO io)
    {
        _io = io;
        _ui = new HeadlessConsole();
        _console = new EmueraConsole(_ui);
        _protocol = new AgentJsonlProtocol(_console, _ui, io);
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
            _protocol.Run();

            // 保持会话运行直到协议停止或 IO 断开
            while (!_cts.IsCancellationRequested && !_protocol.IsStopped && _io.IsConnected)
            {
                Thread.Sleep(100);
            }
        }
        catch (Exception ex)
        {
            _io.WriteLine($"{{\"error\":\"{ex.Message}\",\"state\":\"Error\"}}");
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
        _cts.Cancel();
        _protocol.Stop();
        _io.Close();
        _console.Dispose();
    }
}
