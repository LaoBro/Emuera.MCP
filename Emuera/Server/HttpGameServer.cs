using System;
using System.Collections.Concurrent;
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
    private readonly SessionManager _sessions;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<string, HttpSessionIO> _ioMap = new();

    public HttpGameServer(int port)
    {
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://localhost:{port}/");
        _sessions = new SessionManager();
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

            // POST /sessions → 创建会话
            if (method == "POST" && path == "/sessions")
            {
                var io = new HttpSessionIO();
                var session = _sessions.Create(io);
                io.SessionId = session.Id;
                _ioMap[session.Id] = io;
                session.Touch();
                WriteJson(resp, 201, new { sessionId = session.Id, createdAt = session.CreatedAt });
                return;
            }

            // GET /sessions/{id}/turn → 长轮询获取回合（必须放在 /sessions/{id} 之前）
            if (method == "GET" && path.StartsWith("/sessions/") && path.EndsWith("/turn"))
            {
                var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 3 && parts[0] == "sessions" && parts[2] == "turn")
                {
                    var id = parts[1];
                    if (!_ioMap.TryGetValue(id, out var io))
                    {
                        WriteJson(resp, 404, new { error = "Session not found" });
                        return;
                    }
                    var session = _sessions.Get(id);
                    session?.Touch();

                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    while (sw.Elapsed < TimeSpan.FromSeconds(25))
                    {
                        if (io.TryDequeueOutput(out var turn) && turn != null)
                        {
                            resp.ContentType = "application/json; charset=utf-8";
                            var bytes = Encoding.UTF8.GetBytes(turn);
                            resp.OutputStream.Write(bytes, 0, bytes.Length);
                            resp.Close();
                            return;
                        }
                        Thread.Sleep(50);
                    }
                    WriteJson(resp, 204, new { });
                    return;
                }
            }

            // POST /sessions/{id}/input → 提交输入（必须放在 /sessions/{id} 之前）
            if (method == "POST" && path.StartsWith("/sessions/") && path.EndsWith("/input"))
            {
                var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 3 && parts[0] == "sessions" && parts[2] == "input")
                {
                    var id = parts[1];
                    if (!_ioMap.TryGetValue(id, out var io))
                    {
                        WriteJson(resp, 404, new { error = "Session not found" });
                        return;
                    }
                    using var reader = new StreamReader(req.InputStream);
                    var body = reader.ReadToEnd();
                    io.EnqueueInput(body);
                    var session = _sessions.Get(id);
                    session?.Touch();
                    WriteJson(resp, 200, new { received = true });
                    return;
                }
            }

            // GET /sessions/{id} → 查询状态
            if (method == "GET" && path.StartsWith("/sessions/"))
            {
                var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2 && parts[0] == "sessions")
                {
                    var id = parts[1];
                    var session = _sessions.Get(id);
                    if (session == null)
                    {
                        WriteJson(resp, 404, new { error = "Session not found" });
                        return;
                    }
                    session.Touch();
                    WriteJson(resp, 200, new
                    {
                        sessionId = id,
                        isRunning = session.IsRunning,
                        lastActivity = session.LastActivityAt
                    });
                    return;
                }
            }

            // DELETE /sessions/{id} → 销毁会话
            if (method == "DELETE" && path.StartsWith("/sessions/"))
            {
                var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2 && parts[0] == "sessions")
                {
                    var id = parts[1];
                    _ioMap.TryRemove(id, out _);
                    var removed = _sessions.Remove(id);
                    WriteJson(resp, removed ? 200 : 404, new { removed });
                    return;
                }
            }

            WriteJson(resp, 404, new { error = "Not found" });
        }
        catch (Exception ex)
        {
            WriteJson(resp, 500, new { error = ex.Message });
        }
    }

    private static void WriteJson(HttpListenerResponse resp, int status, object obj)
    {
        resp.StatusCode = status;
        resp.ContentType = "application/json; charset=utf-8";
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(obj));
        resp.OutputStream.Write(bytes, 0, bytes.Length);
        resp.Close();
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Close();
        _sessions.Dispose();
    }
}
