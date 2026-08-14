using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Emuera.Maui.Json;

namespace Emuera.Maui;

/// <summary>
/// 探测本机 Headless Server 是否有可旁观的活跃会话（control-handoff issue 05 / M4）。
/// </summary>
/// <remarks>
/// 仅用 <see cref="HttpClient"/> 调 <c>GET /state</c>，不引入 ASP.NET Core。
/// 活跃判定：<c>state != Idle</c> 且 <c>gameDir</c> 非空——空闲 server 不弹远程入口。
/// 已结束会话（Quit/Error）仍算可旁观，便于看结局画面。
/// </remarks>
internal static class RemoteSessionProbe
{
    internal const string DefaultBaseUrl = "http://localhost:8080";
    internal const string HttpPageUrl = "http://localhost:8080/";
    internal const string WebSocketUrl = "ws://localhost:8080/ws";

    /// <summary>本机探测超时——server 未启动时应快速失败，避免拖慢每次启动。</summary>
    internal static readonly TimeSpan ProbeTimeout = TimeSpan.FromMilliseconds(800);

    internal static HttpClient CreateClient()
    {
        return new HttpClient { Timeout = ProbeTimeout };
    }

    /// <summary>
    /// 解析 GET /state JSON：有非 Idle 状态且带 gameDir → 活跃。
    /// </summary>
    internal static bool TryParseActiveSession(string? json, out string? gameDir, out string? state)
    {
        gameDir = null;
        state = null;
        if (string.IsNullOrWhiteSpace(json))
            return false;

        try
        {
            var parsed = JsonSerializer.Deserialize(json, MauiJsonContext.Default.HeadlessStateResponse);
            if (parsed is null)
                return false;

            state = parsed.State;
            gameDir = parsed.GameDir;
            return IsActiveSession(state, gameDir);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    internal static bool IsActiveSession(string? state, string? gameDir)
    {
        if (string.IsNullOrWhiteSpace(state) || string.Equals(state, "Idle", StringComparison.Ordinal))
            return false;
        return !string.IsNullOrWhiteSpace(gameDir);
    }

    /// <summary>
    /// <c>GET {DefaultBaseUrl}/state</c>。不通、非 2xx、空闲或解析失败均返回 null。
    /// </summary>
    internal static async Task<RemoteSessionSnapshot?> ProbeAsync(
        HttpClient http,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var resp = await http.GetAsync($"{DefaultBaseUrl}/state", cancellationToken).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return null;

            var json = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!TryParseActiveSession(json, out var gameDir, out var state))
                return null;

            return new RemoteSessionSnapshot(state!, gameDir!);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[maui] remote probe failed: {ex.Message}");
            return null;
        }
    }
}

/// <summary>探测到的远程活跃会话——仅展示用。</summary>
internal sealed record RemoteSessionSnapshot(string State, string GameDir);
