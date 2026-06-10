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

    /// <summary>
    /// 带超时的读取行。
    /// timeoutMs &lt; 0：无限等待，走现有 ReadLine() 逻辑。
    /// timeoutMs == 0：只尝试 _inputQueue.TryDequeue；无数据返回 null。
    /// timeoutMs &gt; 0：使用 _inputEvent.WaitOne(timeoutMs) 等待；超时返回 null。
    /// Close() 后 _connected = false，后续 ReadLine() / ReadLine(timeoutMs) 必须尽快返回 null。
    /// </summary>
    public override string? ReadLine(int timeoutMs)
    {
        if (timeoutMs == 0)
        {
            if (_inputQueue.TryDequeue(out var line))
                return line;
            return null;
        }

        if (timeoutMs > 0)
        {
            while (_connected)
            {
                if (_inputQueue.TryDequeue(out var line))
                    return line;

                if (!_inputEvent.WaitOne(timeoutMs))
                    return null;
            }

            return null;
        }

        return ReadLine();
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
