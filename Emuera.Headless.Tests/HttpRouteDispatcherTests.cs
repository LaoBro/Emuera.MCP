using System.Threading;
using System.Threading.Tasks;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Server;
using Xunit;

namespace MinorShift.Emuera.Tests;

/// <summary>
/// HttpRouteDispatcher 单测（C1 三件套第 1 件）——直接注入传输无关 HttpRequestData，
/// 断言 HttpResult，验证「路由 + body 解析 + 校验错误映射」（无需拉起 Kestrel / HttpListener）。
/// 端到端 token 回退语义由既有 KestrelGameServerEndpointTests / HttpListenerHostTests 锁定
/// （二者现均经本 dispatcher 路由）；此处补纯校验面。
/// </summary>
public class HttpRouteDispatcherTests
{
    private static HttpRouteDispatcher CreateDispatcher()
        => new(new GameServerProtocol(CreateRegistry(), CreateConfigService()));

    private static GameConfigService CreateConfigService()
        => new(new ConfigData(), false);

    private static SessionRegistry CreateRegistry()
        => new(new SessionRegistryTests.NullTerminalSetup(), CreateConfigService(), GameServerProtocol.ReadAgentLease());

    private static Task<HttpResult> DispatchAsync(HttpRouteDispatcher dispatcher, string method, string path, string? body = null, string? token = null)
        => dispatcher.DispatchAsync(new HttpRequestData(method, path, body, token), CancellationToken.None);

    // ---- 未知路径路由 ----

    [Fact]
    public async Task Unknown_path_returns_404()
    {
        var dispatcher = CreateDispatcher();
        var resp = await DispatchAsync(dispatcher, "GET", "/does-not-exist");
        Assert.Equal(404, resp.StatusCode);
        Assert.Contains("NOT_FOUND", resp.Body);
    }

    // ---- 路由映射 ----

    [Fact]
    public async Task Get_turn_without_session_returns_404()
    {
        var dispatcher = CreateDispatcher();
        var resp = await DispatchAsync(dispatcher, "GET", "/turn");
        Assert.Equal(404, resp.StatusCode);
        Assert.Contains("No active session", resp.Body);
    }

    [Fact]
    public async Task Acquire_without_session_returns_404()
    {
        var dispatcher = CreateDispatcher();
        var resp = await DispatchAsync(dispatcher, "POST", "/control/acquire");
        Assert.Equal(404, resp.StatusCode);
        Assert.Contains("NO_ACTIVE_SESSION", resp.Body);
    }

    // ---- /input 校验错误映射 ----

    [Theory]
    [InlineData("not-json")]
    [InlineData("")]
    [InlineData(null)]
    public async Task Input_bad_or_empty_body_returns_400_invalid_json(string? body)
    {
        var dispatcher = CreateDispatcher();
        var resp = await DispatchAsync(dispatcher, "POST", "/input", body);
        Assert.Equal(400, resp.StatusCode);
        Assert.Contains("Invalid JSON", resp.Body);
    }

    [Fact]
    public async Task Input_missing_value_returns_400()
    {
        var dispatcher = CreateDispatcher();
        var resp = await DispatchAsync(dispatcher, "POST", "/input", "{\"foo\":\"bar\"}");
        Assert.Equal(400, resp.StatusCode);
        Assert.Contains("Missing 'value' field", resp.Body);
    }

    [Fact]
    public async Task Input_valid_json_reaches_protocol_without_session_returns_404()
    {
        // 解析成功 → 过校验 → 无 session → 协议层 404（校验映射前置、入队在后）。
        var dispatcher = CreateDispatcher();
        var resp = await DispatchAsync(dispatcher, "POST", "/input", "{\"value\":\"hi\"}");
        Assert.Equal(404, resp.StatusCode);
        Assert.Contains("No active session", resp.Body);
    }

    // ---- /load-game 校验错误映射 ----

    [Theory]
    [InlineData("not-json")]
    [InlineData("")]
    [InlineData(null)]
    public async Task LoadGame_bad_or_empty_body_returns_400_invalid_json(string? body)
    {
        var dispatcher = CreateDispatcher();
        var resp = await DispatchAsync(dispatcher, "POST", "/load-game", body);
        Assert.Equal(400, resp.StatusCode);
        Assert.Contains("INVALID_JSON", resp.Body);
    }

    [Fact]
    public async Task LoadGame_missing_dir_returns_400()
    {
        var dispatcher = CreateDispatcher();
        var resp = await DispatchAsync(dispatcher, "POST", "/load-game", "{\"foo\":\"bar\"}");
        Assert.Equal(400, resp.StatusCode);
        Assert.Contains("MISSING_GAME_DIR", resp.Body);
    }
}