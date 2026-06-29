using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MinorShift.Emuera.Server;

internal sealed class HttpGameServer : IDisposable
{
    /// <summary>GET /turn 长轮询超时（毫秒）。超时返回 204。</summary>
    private const int TurnWaitTimeoutMs = 25000;

    private readonly HttpListener _listener;
    private Session? _session;
    private readonly object _sessionLock = new();
    private readonly CancellationTokenSource _cts = new();

    public HttpGameServer(int port)
    {
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://localhost:{port}/");
    }

    public void Start()
    {
        _listener.Start();
        Console.Error.WriteLine($"[server] HTTP 监听已启动于 {_listener.Prefixes.First()}");
        _ = Task.Run(RunLoop);
    }

    private async Task RunLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var context = await _listener.GetContextAsync();
                // fire-and-forget：async handler 在 await 时自动释放线程，无需 Task.Run 包裹
                _ = HandleRequestAsync(context, _cts.Token);
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (HttpListenerException)
            {
                break;
            }
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext ctx, CancellationToken serverCt)
    {
        var req = ctx.Request;
        var resp = ctx.Response;
        try
        {
            var path = req.Url?.AbsolutePath ?? "/";
            var method = req.HttpMethod;

            if (method == "POST" && path == "/session") { await HandleCreateSessionAsync(resp, serverCt); return; }
            if (method == "GET" && path == "/turn") { await HandleGetTurnAsync(resp, serverCt); return; }
            if (method == "POST" && path == "/input") { await HandlePostInputAsync(req, resp, serverCt); return; }
            if (method == "GET" && path == "/state") { await HandleGetStateAsync(resp, serverCt); return; }
            if (method == "DELETE" && path == "/session") { await HandleDeleteSessionAsync(resp, serverCt); return; }

            await WriteJsonAsync(resp, 404, new { error = "Not found" }, serverCt);
        }
        catch (OperationCanceledException) when (serverCt.IsCancellationRequested)
        {
            // 服务器关闭，静默退出，不写 response
        }
        catch (Exception ex)
        {
            // serverCt 可能已取消（导致异常），用 CancellationToken.None 确保错误响应能写出
            try { await WriteJsonAsync(resp, 500, new { error = ex.Message }, CancellationToken.None); }
            catch { /* 响应已断开，忽略 */ }
        }
    }

    // POST /session → 创建会话。running 中返回 409；已结束自动覆盖。
    private async Task HandleCreateSessionAsync(HttpListenerResponse resp, CancellationToken serverCt)
    {
        bool conflict;
        string? sessionId = null;
        DateTimeOffset createdAt = default;
        string? state = null;

        lock (_sessionLock)
        {
            if (_session != null && !_session.HasEnded)
            {
                conflict = true;
            }
            else
            {
                conflict = false;
                if (_session != null)
                {
                    _session.Dispose();
                    _session = null;
                }

                var io = new HttpSessionIO();
                _session = new Session(io);
                _session.Start();
                sessionId = _session.Id;
                createdAt = _session.CreatedAt;
                state = _session.StateString;
            }
        }

        if (conflict)
            await WriteJsonAsync(resp, 409, new { error = "A session is already active" }, serverCt);
        else
            await WriteJsonAsync(resp, 201, new { sessionId, createdAt, state }, serverCt);
    }

    // GET /turn → 长轮询获取回合。Quit/Error 后返回最终 turn 一次，之后 404。
    private async Task HandleGetTurnAsync(HttpListenerResponse resp, CancellationToken serverCt)
    {
        Session? session;
        lock (_sessionLock)
        {
            session = _session;
        }

        if (session == null)
        {
            await WriteJsonAsync(resp, 404, new { error = "No active session" }, serverCt);
            return;
        }

        // 预检查：finalTurn 已交付 → 404（快路径，避免无谓 await）
        if (session.IsFinalTurnDelivered)
        {
            await WriteJsonAsync(resp, 404, new { error = "Session ended" }, serverCt);
            return;
        }

        var result = await session.WaitForTurnAsync(TurnWaitTimeoutMs, serverCt);
        switch (result.Status)
        {
            case TurnWaitStatus.Turn:
                // turn 已是 JSON 字符串，直接写 raw bytes 避免双重序列化
                await WriteRawJsonAsync(resp, 200, result.Turn!, serverCt);
                return;
            case TurnWaitStatus.Timeout:
                await WriteJsonAsync(resp, 204, new { }, serverCt);
                return;
            case TurnWaitStatus.Closed:
                await WriteJsonAsync(resp, 404, new { error = "Session ended" }, serverCt);
                return;
        }
    }

    // POST /input → 提交输入。请求体 {"value":"..."}，服务端包装成 JSONL 供 RunLoop 消费。
    private async Task HandlePostInputAsync(HttpListenerRequest req, HttpListenerResponse resp, CancellationToken serverCt)
    {
        Session? session;
        lock (_sessionLock)
        {
            session = _session;
        }

        if (session == null)
        {
            await WriteJsonAsync(resp, 404, new { error = "No active session" }, serverCt);
            return;
        }

        string? value;
        using (var reader = new StreamReader(req.InputStream))
        {
            var body = await reader.ReadToEndAsync(serverCt);
            try
            {
                var input = JsonSerializer.Deserialize<HttpInput>(body);
                value = input?.value;
            }
            catch
            {
                await WriteJsonAsync(resp, 400, new { error = "Invalid JSON, expected {\"value\":\"...\"}" }, serverCt);
                return;
            }
        }

        if (value == null)
        {
            await WriteJsonAsync(resp, 400, new { error = "Missing 'value' field" }, serverCt);
            return;
        }

        var jsonl = JsonSerializer.Serialize(new { type = "input", value });
        session.IO.EnqueueInput(jsonl);
        await WriteJsonAsync(resp, 200, new { received = true }, serverCt);
    }

    // GET /state → 查询状态。无 session 时返回 state="Idle"。
    private async Task HandleGetStateAsync(HttpListenerResponse resp, CancellationToken serverCt)
    {
        Session? session;
        lock (_sessionLock)
        {
            session = _session;
        }

        if (session == null)
        {
            await WriteJsonAsync(resp, 200, new { state = "Idle", isRunning = false }, serverCt);
            return;
        }

        await WriteJsonAsync(resp, 200, new
        {
            state = session.StateString,
            isRunning = session.IsRunning,
            sessionId = session.Id,
            createdAt = session.CreatedAt
        }, serverCt);
    }

    // DELETE /session → 销毁会话。
    private async Task HandleDeleteSessionAsync(HttpListenerResponse resp, CancellationToken serverCt)
    {
        bool removed;
        lock (_sessionLock)
        {
            if (_session != null)
            {
                _session.Dispose();
                _session = null;
                removed = true;
            }
            else
            {
                removed = false;
            }
        }

        await WriteJsonAsync(resp, removed ? 200 : 404, new { removed }, serverCt);
    }

    private static async Task WriteJsonAsync(HttpListenerResponse resp, int status, object obj, CancellationToken ct)
    {
        resp.StatusCode = status;
        resp.ContentType = "application/json; charset=utf-8";
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(obj));
        await resp.OutputStream.WriteAsync(bytes, 0, bytes.Length, ct);
        resp.Close();
    }

    /// <summary>写入已经是 JSON 字符串的响应（避免双重序列化）。用于 GET /turn 返回 turn。</summary>
    private static async Task WriteRawJsonAsync(HttpListenerResponse resp, int status, string json, CancellationToken ct)
    {
        resp.StatusCode = status;
        resp.ContentType = "application/json; charset=utf-8";
        var bytes = Encoding.UTF8.GetBytes(json);
        await resp.OutputStream.WriteAsync(bytes, 0, bytes.Length, ct);
        resp.Close();
    }

    private sealed class HttpInput
    {
        public string? value { get; set; }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Close();
        lock (_sessionLock)
        {
            _session?.Dispose();
            _session = null;
        }
    }
}
