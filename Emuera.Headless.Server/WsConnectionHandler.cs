using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

namespace MinorShift.Emuera.Server;

/// <summary>
/// GET /ws —— WebSocket 旁路传输端点（Hub 旁路模式，非另一个 SessionIO）。
/// 从 <see cref="KestrelGameServer"/> 拆出，逻辑逐字迁移，行为不变。
///
/// 生命周期：
/// - 无活跃 session 时接受升级后立即以下发关闭码 4004 退回。
/// - 有 session：SessionRegistry.TryGetWsSubscriptionAsync 锁内完成 accept→re-check→subscribe
///   原子序列，避免 session 被另一线程 DELETE+POST 替换后仍从旧 hub 订阅。
/// - 任一侧结束 / WS 关闭 → 取消另一循环并退订；session 结束（hub.Complete）以 WS 关闭帧通知。
/// </summary>
internal sealed class WsConnectionHandler
{
    /// <summary>无可用 session 时 /ws 升级被拒，下发的自定义 WS 关闭码（未在 IANA 注册，借用于"应用层错误"语义）。</summary>
    private const WebSocketCloseStatus WsCloseNoActiveSession = (WebSocketCloseStatus)4004;

    private readonly SessionRegistry _sessions;

    public WsConnectionHandler(SessionRegistry sessions)
    {
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
    }

    /// <summary>路由委托入口（迁移自原 HandleWebSocketAsync）。</summary>
    public async Task HandleAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = 400;
            return;
        }

        using var ws = await context.WebSockets.AcceptWebSocketAsync();

        // 锁内 accept→re-check→subscribe 原子序列由 SessionRegistry 保证
        var subscription = await _sessions.TryGetWsSubscriptionAsync(ReadIdentity(context));
        if (subscription == null)
        {
            await ws.CloseAsync(WsCloseNoActiveSession, "No active session", CancellationToken.None);
            return;
        }

        await RunConnectionAsync(ws, subscription);
    }

    /// <summary>
    /// 连接主循环（internal 可测核心）：双循环并行，任一侧结束即收尾。
    /// - 发送循环从 hub 订阅 reader 读 turn，逐个以裸 JSON 文本帧下发；reader 完成（session 结束）即退出。
    /// - 接收循环读文本帧 → 校验 type=="input" → EnqueueInput；客户端关闭帧即退出。
    /// </summary>
    internal static async Task RunConnectionAsync(WebSocket ws, WsSubscription subscription)
    {
        using var cts = new CancellationTokenSource();

        try
        {
            await Task.WhenAny(
                SendLoopAsync(ws, subscription.Reader, cts.Token),
                ReceiveLoopAsync(ws, subscription, cts.Token)
            );
        }
        finally
        {
            cts.Cancel();
            subscription.Hub.Unsubscribe(subscription.Reader);
            try
            {
                if (ws.State is WebSocketState.Open or WebSocketState.CloseReceived or WebSocketState.CloseSent)
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
            }
            catch (WebSocketException)
            {
            }
            catch (OperationCanceledException)
            {
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
    private static async Task ReceiveLoopAsync(WebSocket ws, WsSubscription subscription, CancellationToken ct)
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
                HandleWsInput(text, subscription.Session, subscription.Identity);
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

    /// <summary>WS 输入帧已是 <c>{"type":"input","value":"..."}</c> 格式，直接入队。
    /// 协议层 <c>AgentJsonlProtocol</c> 会校验 <c>type=="input"</c>，无效帧自然被忽略。</summary>
    private static void HandleWsInput(string text, Session session, ControlIdentity identity)
    {
        var gate = session.Controller.CheckInput(identity, session.HasEnded);
        if (gate.Status == ControlGateStatus.Allowed)
            session.IO.EnqueueInput(text);
    }

    private static ControlIdentity ReadIdentity(HttpContext context)
    {
        string? token = context.Request.Query["token"].ToString();
        if (context.Request.Headers.TryGetValue("X-Control-Token", out var headerToken))
            token = headerToken.ToString();
        if (string.IsNullOrWhiteSpace(token) && context.Request.Headers.TryGetValue("Authorization", out var authorization))
        {
            var value = authorization.ToString();
            if (value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                token = value[7..];
        }
        return string.IsNullOrWhiteSpace(token)
            ? ControlIdentity.User
            : ControlIdentity.Agent(token);
    }
}
