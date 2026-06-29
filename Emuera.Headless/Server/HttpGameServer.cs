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
                _ = Task.Run(() => HandleRequest(context));
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

    private void HandleRequest(HttpListenerContext ctx)
    {
        var req = ctx.Request;
        var resp = ctx.Response;
        try
        {
            var path = req.Url?.AbsolutePath ?? "/";
            var method = req.HttpMethod;

            if (method == "POST" && path == "/session") { HandleCreateSession(resp); return; }
            if (method == "GET" && path == "/turn") { HandleGetTurn(resp); return; }
            if (method == "POST" && path == "/input") { HandlePostInput(req, resp); return; }
            if (method == "GET" && path == "/state") { HandleGetState(resp); return; }
            if (method == "DELETE" && path == "/session") { HandleDeleteSession(resp); return; }

            WriteJson(resp, 404, new { error = "Not found" });
        }
        catch (Exception ex)
        {
            WriteJson(resp, 500, new { error = ex.Message });
        }
    }

    // POST /session → 创建会话。running 中返回 409；已结束自动覆盖。
    private void HandleCreateSession(HttpListenerResponse resp)
    {
        string sessionId;
        DateTimeOffset createdAt;
        string state;

        lock (_sessionLock)
        {
            if (_session != null && !_session.HasEnded)
            {
                WriteJson(resp, 409, new { error = "A session is already active" });
                return;
            }

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

        WriteJson(resp, 201, new { sessionId, createdAt, state });
    }

    // GET /turn → 长轮询获取回合。Quit/Error 后返回最终 turn 一次，之后 404。
    private void HandleGetTurn(HttpListenerResponse resp)
    {
        Session? session;
        lock (_sessionLock)
        {
            session = _session;
        }

        if (session == null)
        {
            WriteJson(resp, 404, new { error = "No active session" });
            return;
        }

        if (session.IsFinalTurnDelivered)
        {
            WriteJson(resp, 404, new { error = "Session ended" });
            return;
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(25))
        {
            if (session.TryTakeTurn(out var turn) && turn != null)
            {
                resp.ContentType = "application/json; charset=utf-8";
                var bytes = Encoding.UTF8.GetBytes(turn);
                resp.OutputStream.Write(bytes, 0, bytes.Length);
                resp.Close();
                return;
            }

            // 最终 turn 已被其他并发请求取走，或会话已结束且队列空
            if (session.IsFinalTurnDelivered || session.HasEnded)
            {
                WriteJson(resp, 404, new { error = "Session ended" });
                return;
            }
            Thread.Sleep(50);
        }
        WriteJson(resp, 204, new { });
    }

    // POST /input → 提交输入。请求体 {"value":"..."}，服务端包装成 JSONL 供 RunLoop 消费。
    private void HandlePostInput(HttpListenerRequest req, HttpListenerResponse resp)
    {
        Session? session;
        lock (_sessionLock)
        {
            session = _session;
        }

        if (session == null)
        {
            WriteJson(resp, 404, new { error = "No active session" });
            return;
        }

        string? value;
        using (var reader = new StreamReader(req.InputStream))
        {
            var body = reader.ReadToEnd();
            try
            {
                var input = JsonSerializer.Deserialize<HttpInput>(body);
                value = input?.value;
            }
            catch
            {
                WriteJson(resp, 400, new { error = "Invalid JSON, expected {\"value\":\"...\"}" });
                return;
            }
        }

        if (value == null)
        {
            WriteJson(resp, 400, new { error = "Missing 'value' field" });
            return;
        }

        var jsonl = JsonSerializer.Serialize(new { type = "input", value });
        session.IO.EnqueueInput(jsonl);
        WriteJson(resp, 200, new { received = true });
    }

    // GET /state → 查询状态。无 session 时返回 state="Idle"。
    private void HandleGetState(HttpListenerResponse resp)
    {
        Session? session;
        lock (_sessionLock)
        {
            session = _session;
        }

        if (session == null)
        {
            WriteJson(resp, 200, new { state = "Idle", isRunning = false });
            return;
        }

        WriteJson(resp, 200, new
        {
            state = session.StateString,
            isRunning = session.IsRunning,
            sessionId = session.Id,
            createdAt = session.CreatedAt
        });
    }

    // DELETE /session → 销毁会话。
    private void HandleDeleteSession(HttpListenerResponse resp)
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

        WriteJson(resp, removed ? 200 : 404, new { removed });
    }

    private static void WriteJson(HttpListenerResponse resp, int status, object obj)
    {
        resp.StatusCode = status;
        resp.ContentType = "application/json; charset=utf-8";
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(obj));
        resp.OutputStream.Write(bytes, 0, bytes.Length);
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
