using System;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MinorShift.Emuera.GameView;
using MinorShift.Emuera.Terminal.Platform;

namespace MinorShift.Emuera.Server;

internal sealed class KestrelGameServer : IDisposable
{
    private const int TurnWaitTimeoutMs = 25000;

    /// <summary>无可用 session 时 /ws 升级被拒，下发的自定义 WS 关闭码（未在 IANA 注册，借用于"应用层错误"语义）。</summary>
    private const WebSocketCloseStatus WsCloseNoActiveSession = (WebSocketCloseStatus)4004;

    private readonly WebApplication _app;
    private readonly ITerminalSetup _terminalSetup;
    private Session? _session;
    private OutputHub? _hub;
    private readonly object _sessionLock = new();

    public KestrelGameServer(int port, ITerminalSetup terminalSetup)
    {
        _terminalSetup = terminalSetup;

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseKestrel();
        builder.WebHost.UseUrls($"http://localhost:{port}");
        builder.Logging.ClearProviders();
        _app = builder.Build();

        // P0-2 为 WebSocket 铺路：启用 WS 中间件（仅启用，未实现端点前无副作用）。
        _app.UseWebSockets();

        MapRoutes();
    }

    private void MapRoutes()
    {
        _app.MapPost("/session", (Delegate)HandleCreateSessionAsync);
        _app.MapGet("/turn", (Delegate)HandleGetTurnAsync);
        _app.MapPost("/input", (Delegate)HandlePostInputAsync);
        _app.MapGet("/state", (Delegate)HandleGetStateAsync);
        _app.MapDelete("/session", (Delegate)HandleDeleteSessionAsync);
        _app.MapGet("/ws", (Delegate)HandleWebSocketAsync);
    }

    public async Task StartAsync()
    {
        await _app.StartAsync();
        Console.Error.WriteLine("[server] Kestrel 监听已启动");
    }

    public async Task WaitForShutdownAsync()
    {
        await _app.WaitForShutdownAsync();
    }

    private IResult HandleCreateSessionAsync()
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

                var hub = new OutputHub();
                var io = new HttpSessionIO(hub);
                _hub = hub;
                _session = new Session(io, _terminalSetup);
                _session.Start();
                sessionId = _session.Id;
                createdAt = _session.CreatedAt;
                state = _session.StateString;
            }
        }

        if (conflict)
            return Results.Json(new { error = "A session is already active" }, statusCode: 409);

        return Results.Json(new { sessionId, createdAt, state }, statusCode: 201);
    }

    private async Task<IResult> HandleGetTurnAsync(HttpContext context)
    {
        Session? session;
        lock (_sessionLock)
        {
            session = _session;
        }

        if (session == null)
            return Results.Json(new { error = "No active session" }, statusCode: 404);

        if (session.IsFinalTurnDelivered)
            return Results.Json(new { error = "Session ended" }, statusCode: 404);

        var result = await session.WaitForTurnAsync(TurnWaitTimeoutMs, context.RequestAborted);
        switch (result.Status)
        {
            case TurnWaitStatus.Turn:
                return Results.Text(result.Turn!, "application/json", Encoding.UTF8, 200);
            case TurnWaitStatus.Timeout:
                return Results.NoContent();
            case TurnWaitStatus.Closed:
                return Results.Json(new { error = "Session ended" }, statusCode: 404);
            default:
                return Results.Json(new { error = "unexpected turn wait status" }, statusCode: 500);
        }
    }

    private async Task<IResult> HandlePostInputAsync(HttpContext context)
    {
        Session? session;
        lock (_sessionLock)
        {
            session = _session;
        }

        if (session == null)
            return Results.Json(new { error = "No active session" }, statusCode: 404);

        string? value;
        using (var reader = new StreamReader(context.Request.Body))
        {
            var body = await reader.ReadToEndAsync();
            try
            {
                var input = JsonSerializer.Deserialize<HttpInput>(body);
                value = input?.value;
            }
            catch
            {
                return Results.Json(new { error = "Invalid JSON, expected {\"value\":\"...\"}" }, statusCode: 400);
            }
        }

        if (value == null)
            return Results.Json(new { error = "Missing 'value' field" }, statusCode: 400);

        session.IO.EnqueueInput(BuildInputJsonl(value));
        return Results.Json(new { received = true });
    }

    /// <summary>把输入值构造成协议循环消费的 input jsonl（与 WS 输入帧同源，保证 HTTP/WS 输入格式对称）。</summary>
    private static string BuildInputJsonl(string value)
    {
        return JsonSerializer.Serialize(new { type = "input", value });
    }

    private IResult HandleGetStateAsync()
    {
        Session? session;
        lock (_sessionLock)
        {
            session = _session;
        }

        if (session == null)
            return Results.Json(new { state = "Idle", isRunning = false });

        return Results.Json(new
        {
            state = session.StateString,
            isRunning = session.IsRunning,
            sessionId = session.Id,
            createdAt = session.CreatedAt
        });
    }

    private IResult HandleDeleteSessionAsync()
    {
        bool removed;
        lock (_sessionLock)
        {
            if (_session != null)
            {
                _session.Dispose();
                _session = null;
                _hub = null;
                removed = true;
            }
            else
            {
                removed = false;
            }
        }

        return Results.Json(new { removed }, statusCode: removed ? 200 : 404);
    }

    private sealed class HttpInput
    {
        public string? value { get; set; }
    }

    /// <summary>
    /// WS 输入帧 DTO：仅接受 <c>{"type":"input","value":"..."}</c>。
    /// value 限制为字符串，与 HTTP <c>POST /input</c> 的 <see cref="HttpInput"/> 行为对称
    /// （非字符串 value 被拒绝），保证喂入 <c>EnqueueInput</c> 的 jsonl 格式一致。
    /// </summary>
    private sealed record WsInput(string? type, string? value);

    /// <summary>
    /// GET /ws —— WebSocket 旁路传输端点（Hub 旁路模式，非另一个 SessionIO）。
    ///
    /// 生命周期：
    /// - 升级门禁（5a）：无活跃 session 时接受升级后立即以下发关闭码 4004 退回，提示"请先创建会话"。
    /// - 有 session：接受升级，从 <see cref="OutputHub"/> 订阅输出（每产生一个 turn 推一帧裸 JSON 文本），
    ///   并接收客户端输入帧喂入同一输入 Channel。
    /// - 任一侧结束 / WS 关闭 → 取消另一循环并退订；session 结束（hub.Complete）以 WS 关闭帧通知。
    /// </summary>
    private async Task HandleWebSocketAsync(HttpContext context)
    {
        // 非 WS 请求直接拒绝（无论有无 session）。
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = 400;
            return;
        }

        Session? session;
        OutputHub? hub;
        lock (_sessionLock)
        {
            session = _session;
            hub = _hub;
        }

        // 升级门禁（生命周期 5a）：无活跃 session / 已结束 → 接受升级后立即以关闭码 4004 退回。
        if (session == null || hub == null || session.HasEnded)
        {
            using var rejected = await context.WebSockets.AcceptWebSocketAsync();
            await rejected.CloseAsync(WsCloseNoActiveSession, "No active session", CancellationToken.None);
            return;
        }

        using var ws = await context.WebSockets.AcceptWebSocketAsync();

        // 握手期间 session 可能被 DELETE：再次确认仍活跃，否则下发布局一致的 4004。
        // （re-check 与后续 Subscribe 之间无 await，竞态窗口可忽略；即使极小概率落到
        //  NormalClosure，语义上 session 已结束，客户端应重建会话。）
        if (session.HasEnded)
        {
            await ws.CloseAsync(WsCloseNoActiveSession, "No active session", CancellationToken.None);
            return;
        }

        var reader = hub.Subscribe();
        using var cts = new CancellationTokenSource();

        try
        {
            // 发送循环与接收循环任一结束即收尾（取消另一侧 + 退订 + 关闭 WS）。
            await Task.WhenAny(
                SendLoopAsync(ws, reader, cts.Token),
                ReceiveLoopAsync(ws, session, cts.Token)
            );
        }
        finally
        {
            cts.Cancel();
            hub.Unsubscribe(reader);
            try
            {
                if (ws.State is WebSocketState.Open or WebSocketState.CloseReceived or WebSocketState.CloseSent)
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
            }
            catch (WebSocketException)
            {
                // 客户端可能已先关闭，忽略。
            }
            catch (OperationCanceledException)
            {
                // 取消，忽略。
            }
        }
    }

    /// <summary>发送循环：从 hub 订阅 reader 读 turn，逐个以裸 JSON 文本帧下发。reader 完成（session 结束）即退出。</summary>
    private static async Task SendLoopAsync(WebSocket ws, ChannelReader<string> reader, CancellationToken ct)
    {
        try
        {
            await foreach (var turn in reader.ReadAllAsync(ct))
            {
                if (ws.State != WebSocketState.Open)
                    break;
                var bytes = Encoding.UTF8.GetBytes(turn);
                await ws.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // 被取消（对端结束 / 收尾），正常退出。
        }
        catch (ChannelClosedException)
        {
            // hub.Complete() 完成 reader，正常退出。
        }
        catch (WebSocketException)
        {
            // 客户端断开，正常退出。
        }
    }

    /// <summary>接收循环：读文本帧 → 校验 type=="input" → EnqueueInput。支持分片重组。客户端关闭帧即退出。</summary>
    private static async Task ReceiveLoopAsync(WebSocket ws, Session session, CancellationToken ct)
    {
        var buffer = new byte[8192];
        var accumulator = new List<byte>(8192);
        try
        {
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var result = await ws.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close)
                    break;

                accumulator.AddRange(buffer.AsSpan(0, result.Count));
                if (!result.EndOfMessage)
                    continue;

                var text = Encoding.UTF8.GetString(accumulator.ToArray());
                accumulator.Clear();
                HandleWsInput(text, session);
            }
        }
        catch (OperationCanceledException)
        {
            // 取消，正常退出。
        }
        catch (WebSocketException)
        {
            // 客户端断开，正常退出。
        }
    }

    /// <summary>校验 WS 输入帧并喂入同一条输入 Channel（与 POST /input 完全相同路径）。</summary>
    private static void HandleWsInput(string text, Session session)
    {
        try
        {
            var cmd = JsonSerializer.Deserialize<WsInput>(text);
            if (cmd is null || cmd.type != "input" || cmd.value == null)
                return;

            session.IO.EnqueueInput(BuildInputJsonl(cmd.value));
        }
        catch (JsonException)
        {
            // 非法 JSON：忽略该帧，不中断连接。
        }
    }

    public void Dispose()
    {
        lock (_sessionLock)
        {
            _session?.Dispose();
            _session = null;
            _hub = null;
        }
        ((IDisposable)_app).Dispose();
    }
}
