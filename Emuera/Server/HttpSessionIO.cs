using System;
using System.Collections.Concurrent;
using System.Threading;

namespace MinorShift.Emuera.Server;

/// <summary>
/// 基于内存队列的 SessionIO，用于 HTTP 长轮询模式。
/// </summary>
internal sealed class HttpSessionIO : SessionIO
{
    private readonly ConcurrentQueue<string> _inputQueue = new();
    private readonly ConcurrentQueue<string> _outputQueue = new();
    private readonly AutoResetEvent _inputEvent = new(false);
    private volatile bool _connected = true;

    public string? SessionId { get; set; }

    public override string? ReadLine()
    {
        while (_connected)
        {
            if (_inputQueue.TryDequeue(out var line))
                return line;
            _inputEvent.WaitOne(100);
        }
        return null;
    }

    public override void WriteLine(string text)
    {
        if (_connected)
            _outputQueue.Enqueue(text);
    }

    public override void Close() => _connected = false;
    public override bool IsConnected => _connected;

    public void EnqueueInput(string line)
    {
        _inputQueue.Enqueue(line);
        _inputEvent.Set();
    }

    public bool TryDequeueOutput(out string? text) => _outputQueue.TryDequeue(out text);
}
