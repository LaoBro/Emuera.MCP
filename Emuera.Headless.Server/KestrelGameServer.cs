using System;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MinorShift.Emuera.Assets;
using MinorShift.Emuera.GameView;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Terminal.Platform;

namespace MinorShift.Emuera.Server;

/// <summary>
/// Kestrel 游戏服务器（职责拆分后）：仅负责 Kestrel 构建、静态资源中间件与路由注册，
/// 端点逻辑委托给 <see cref="SessionRegistry"/> / <see cref="GameConfigService"/> /
/// <see cref="WsConnectionHandler"/>。
/// 对外 HTTP/WS 契约与 ServerRunner 调用序列保持不变。
/// </summary>
internal sealed class KestrelGameServer : IDisposable
{
    private const int TurnWaitTimeoutMs = 25000;

    private readonly WebApplication _app;
    private readonly GameConfigService _config;
    private readonly SessionRegistry _sessions;
    private readonly WsConnectionHandler _ws;

    /// <summary>测试接缝：端点测试经此访问当前 session（如注入 turn）。</summary>
    internal SessionRegistry Sessions => _sessions;

    public KestrelGameServer(int port, ITerminalSetup terminalSetup, ConfigData configData)
    {
        _config = new GameConfigService(configData);
        _sessions = new SessionRegistry(terminalSetup, _config, ReadAgentLease());
        _ws = new WsConnectionHandler(_sessions);

        // issue 10：WebRootPath 显式指向 exe 所在目录的 wwwroot/——
        // WebApplication.CreateBuilder() 默认用 Directory.GetCurrentDirectory()/wwwroot，
        // 工作目录错位（如从仓库根启动 exe）会导致静态文件 404。
        // 必须用 WebApplicationOptions 在 builder 构造前指定 WebRootPath——
        // builder 创建后 builder.WebHost.UseWebRoot() 会抛 NotSupportedException
        // ("The web root changed ... Use WebApplication.CreateBuilder(WebApplicationOptions) instead")。
        // AppContext.BaseDirectory 在 PublishSingleFile=true 下返回 exe 所在目录
        // （.NET 8+ 单文件部署不再解压到临时目录），保证 wwwroot 跟随 exe 分发。
        var options = new WebApplicationOptions
        {
            ContentRootPath = AppContext.BaseDirectory,
            WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot"),
        };
        var builder = WebApplication.CreateBuilder(options);
        builder.WebHost.UseKestrel();
        builder.WebHost.UseUrls($"http://localhost:{port}");
        builder.Logging.ClearProviders();
        // 3.3（NativeAOT）：minimal API 响应序列化走 HttpJsonOptions 的 resolver chain，
        // AOT 下默认反射 resolver 被禁用——把 Server 的源生成上下文插到链首，
        // 否则 JsonResult(JsonObject) 抛 JsonTypeInfo metadata not provided（全端点 500）。
        builder.Services.ConfigureHttpJsonOptions(options =>
            options.SerializerOptions.TypeInfoResolverChain.Insert(0, ServerJsonContext.Default));
        _app = builder.Build();

        // P0-2 为 WebSocket 铺路：启用 WS 中间件（仅启用，未实现端点前无副作用）。
        _app.UseWebSockets();

        // issue 10：静态资源服务——server 模式下 Kestrel 直接提供 Vue 构建产物（wwwroot/）。
        // 顺序必须 UseDefaultFiles → UseStaticFiles：前者把 `/` 重写为 `/index.html`，
        // 后者从 wwwroot 提供文件。二者必须在 MapRoutes 之前注册，否则默认文件请求不会被拦截。
        // wwwroot/ 由 csproj BuildVueFrontend pre-build target 从 ../Emuera.Web/dist/ 复制填充。
        _app.UseDefaultFiles();
        _app.UseStaticFiles();

        MapRoutes();
    }

    private void MapRoutes()
    {
        _app.MapPost("/session", (Delegate)HandleCreateSessionAsync);
        _app.MapPost("/control/acquire", (Delegate)HandleAcquireControlAsync);
        _app.MapPost("/control/release", (Delegate)HandleReleaseControlAsync);
        _app.MapGet("/control", (Delegate)HandleGetControlAsync);
        _app.MapGet("/control/wait", (Delegate)HandleWaitForControlAsync);
        _app.MapGet("/turn", (Delegate)HandleGetTurnAsync);
        _app.MapPost("/input", (Delegate)HandlePostInputAsync);
        _app.MapGet("/state", (Delegate)HandleGetStateAsync);
        _app.MapGet("/config", (Delegate)HandleGetConfigAsync);
        _app.MapGet("/snapshot", (Delegate)HandleGetSnapshotAsync);
        _app.MapDelete("/session", (Delegate)HandleDeleteSessionForHttpAsync);
        _app.MapGet("/ws", (Delegate)_ws.HandleAsync);
        // issue 05：游戏选择器端点——/load-game 重载游戏目录，/native/pick-directory 安卓 SAF 桩
        _app.MapPost("/load-game", (Delegate)HandleLoadGameAsync);
        _app.MapPost("/native/pick-directory", (Delegate)HandlePickDirectoryAsync);
        // issue 03：游戏目录图片资源通道（spec Q5 安全决策）——路径消毒 + 扩展名白名单 + 缓存/CORS 头
        _app.MapGet("/assets/{**path}", (Delegate)HandleGetAssetAsync);
    }

    public async Task StartAsync()
    {
        await _app.StartAsync();
        EmueraLog.Info("server", "Kestrel 监听已启动");
    }

    public async Task WaitForShutdownAsync()
    {
        await _app.WaitForShutdownAsync();
    }

    /// <summary>
    /// POST /session —— 建会话。
    /// - 未加载游戏（_session==null，POST /load-game 成功前）→ 503 {"error":"No game loaded"}
    ///   （T-025 D4/D16：空闲态守卫，早于「已有活跃会话 409」判断）
    /// - 已有活跃会话 → 409 {"error":"A session is already active"}
    /// - 旧会话已结束 → dispose 后重建 → 201 {sessionId, createdAt, state}
    /// 生命周期逻辑见 <see cref="SessionRegistry.CreateNewSessionAsync"/>。
    /// </summary>
    internal async Task<IResult> HandleCreateSessionAsync()
    {
        var result = await _sessions.CreateNewSessionAsync();
        return result.Status switch
        {
            // 3.3（NativeAOT）：响应统一 JsonObject——JsonNode 系走内建 converter，
            // 匿名类型在 AOT 下反射序列化被禁用（JsonSerializerIsReflectionDisabled）。
            SessionCreateStatus.NoGameLoaded => JsonResult(new JsonObject { ["error"] = "No game loaded" }, statusCode: 503),
            SessionCreateStatus.Conflict => JsonResult(new JsonObject { ["error"] = "A session is already active" }, statusCode: 409),
            _ => JsonResult(new JsonObject { ["sessionId"] = result.SessionId, ["createdAt"] = result.CreatedAt, ["state"] = result.State }, statusCode: 201),
        };
    }

    /// <summary>POST /control/acquire —— 获取控制权并返回 drain 后的末位回合确认。</summary>
    internal async Task<IResult> HandleAcquireControlAsync(HttpContext context)
    {
        var session = _sessions.CurrentSession;
        if (session == null)
            return ErrorResult("NO_ACTIVE_SESSION", "No active session", 404);

        var request = await ReadControlRequestAsync(context);
        var identity = ToIdentity(request?.token ?? ReadToken(context));
        var result = session.AcquireControl(identity);
        if (result.Control.Status != ControlAcquireStatus.Acquired)
        {
            var code = result.Control.Status == ControlAcquireStatus.HeldByUser
                ? "CONTROL_HELD_BY_USER"
                : "CONTROL_HELD";
            return ControlErrorResult(code, result.Control.Status == ControlAcquireStatus.HeldByUser
                ? "Control is held by the user"
                : "Control is held by another agent", session);
        }

        return JsonResult(new JsonObject
        {
            ["controller"] = ControllerNode(session.Controller.CurrentInfo),
            ["state"] = session.Controller.State,
            ["turn"] = ParseTurn(result.Turn),
            ["turnsAdvanced"] = result.TurnsAdvanced,
        });
    }

    internal async Task<IResult> HandleReleaseControlAsync(HttpContext context)
    {
        var session = _sessions.CurrentSession;
        if (session == null)
            return ErrorResult("NO_ACTIVE_SESSION", "No active session", 404);

        var request = await ReadControlRequestAsync(context);
        var result = session.Controller.Release(ToIdentity(request?.token ?? ReadToken(context)));
        if (result.Status == ControlReleaseStatus.NotOwner)
            return ControlErrorResult("CONTROL_NOT_OWNER", "Only the current Controller may release", session);

        return JsonResult(new JsonObject
        {
            ["released"] = result.Status == ControlReleaseStatus.Released,
            ["controller"] = ControllerNode(session.Controller.CurrentInfo),
            ["state"] = session.Controller.State,
        });
    }

    internal IResult HandleGetControlAsync()
    {
        var session = _sessions.CurrentSession;
        return JsonResult(new JsonObject
        {
            ["controller"] = ControllerNode(session?.Controller.CurrentInfo),
            ["state"] = session?.Controller.State ?? Controller.StateIdle,
        });
    }

    internal async Task<IResult> HandleWaitForControlAsync(HttpContext context)
    {
        var session = _sessions.CurrentSession;
        if (session == null)
            return ErrorResult("NO_ACTIVE_SESSION", "No active session", 404);

        var controlEvent = await session.Controller.WaitForEventAsync(TurnWaitTimeoutMs, context.RequestAborted);
        return controlEvent == null
            ? Results.NoContent()
            : JsonResult(ControlEventNode(controlEvent));
    }

    /// <summary>
    /// GET /turn —— 长轮询等一个 turn。
    /// - 无 session → 404；finalTurn 已交付 → 404
    /// - 拿到 turn → 200 裸 JSON；25s 超时 → 204；session 结束（Channel 关闭）→ 404
    /// </summary>
    internal async Task<IResult> HandleGetTurnAsync(HttpContext context)
    {
        var session = _sessions.CurrentSession;

        if (session == null)
            return JsonResult(new JsonObject { ["error"] = "No active session" }, statusCode: 404);

        if (session.IsFinalTurnDelivered)
            return JsonResult(new JsonObject { ["error"] = "Session ended" }, statusCode: 404);

        var identity = ToIdentity(ReadToken(context));
        var result = await session.WaitForTurnAsync(TurnWaitTimeoutMs, identity, context.RequestAborted);
        switch (result.Status)
        {
            case TurnWaitStatus.Turn:
                return Results.Text(result.Turn!, "application/json", Encoding.UTF8, 200);
            case TurnWaitStatus.Timeout:
                return Results.NoContent();
            case TurnWaitStatus.Closed:
                return JsonResult(new JsonObject { ["error"] = "Session ended" }, statusCode: 404);
            case TurnWaitStatus.ControlLost:
                return ControlLostResult(result.Reason ?? "control_changed", result.At ?? DateTimeOffset.UtcNow, session);
            default:
                return ErrorResult("UNEXPECTED_TURN_STATUS", "unexpected turn wait status", 500);
        }
    }

    /// <summary>
    /// POST /input —— 提交输入。
    /// - 无 session → 404；非法 JSON → 400 {"error":"Invalid JSON, expected {\"value\":\"...\"}"}
    /// - 缺 value → 400 {"error":"Missing 'value' field"}；成功 → 200 {received:true}
    /// </summary>
    internal async Task<IResult> HandlePostInputAsync(HttpContext context)
    {
        var session = _sessions.CurrentSession;

        if (session == null)
            return JsonResult(new JsonObject { ["error"] = "No active session" }, statusCode: 404);

        string? value;
        string? bodyToken = null;
        using (var reader = new StreamReader(context.Request.Body))
        {
            var body = await reader.ReadToEndAsync();
            try
            {
                var input = JsonSerializer.Deserialize(body, ServerJsonContext.Default.HttpInput);
                value = input?.value;
                bodyToken = input?.token;
            }
            catch
            {
                return JsonResult(new JsonObject { ["error"] = "Invalid JSON, expected {\"value\":\"...\"}" }, statusCode: 400);
            }
        }

        if (value == null)
            return JsonResult(new JsonObject { ["error"] = "Missing 'value' field" }, statusCode: 400);

        var gate = session.Controller.CheckInput(ToIdentity(bodyToken ?? ReadToken(context)), session.HasEnded);
        if (gate.Status != ControlGateStatus.Allowed)
            return ControlErrorResult(gate.Reason, "Input is allowed only for the current Controller", session);

        session.IO.EnqueueInput(BuildInputJsonl(value));
        return JsonResult(new JsonObject { ["received"] = true });
    }

    /// <summary>把输入值构造成协议循环消费的 input jsonl（与 WS 输入帧同源，保证 HTTP/WS 输入格式对称）。
    /// 3.3（NativeAOT）：JsonObject.ToJsonString()——JsonNode 系内建序列化，AOT 安全。</summary>
    private static string BuildInputJsonl(string value)
    {
        return new JsonObject { ["type"] = "input", ["value"] = value }.ToJsonString();
    }

    /// <summary>
    /// GET /state —— 当前会话状态 + gameDir（issue 05）+ 窗口布局元信息（issue 12）。
    ///
    /// 前端 App.vue 挂载时调用：判断 server 是否空闲（gameDir==null / state=="Idle"），
    /// 空闲则展示选择器并预填 localStorage 上次目录（T-025 D9 rev，不再自动 loadGame）。
    ///
    /// T-025 D5/D15：空闲态（_session==null）gameDir 显式置 null——前端据此可靠判 idle。
    /// 即使带了 --ExeDir valid_dir，idle 态仍返 gameDir: null（"已加载游戏的目录"与
    /// GamePaths.Current 内部路径是两个概念）。其余窗口元信息字段照常由默认 ConfigData 提供。
    /// /load-game 重建 ConfigData 后再次 GET /state 会拿到新游戏的窗口宽度。
    /// </summary>
    internal IResult HandleGetStateAsync()
    {
        var session = _sessions.CurrentSession;
        var m = _config.GetWindowMetrics();

        // 窗口元信息五字段两分支共用（issue 12）——用 JsonObject 保持字段顺序与匿名对象一致。
        var payload = new JsonObject();
        if (session == null)
        {
            // T-025 D5：空闲态 gameDir 显式置 null——前端据此可靠判 idle 并展示选择器。
            payload["state"] = "Idle";
            payload["isRunning"] = false;
            payload["gameDir"] = null;
        }
        else
        {
            payload["state"] = session.StateString;
            payload["isRunning"] = session.IsRunning;
            payload["sessionId"] = session.Id;
            payload["createdAt"] = session.CreatedAt;
            payload["gameDir"] = GamePaths.Current.ExeDir;
        }
        payload["windowWidth"] = m.WindowWidth;
        payload["fontSize"] = m.FontSize;
        payload["lineHeight"] = m.LineHeight;
        payload["gameColumns"] = m.GameColumns;
        payload["fontName"] = m.FontName;
        return JsonResult(payload);
    }

    /// <summary>GET /config —— 当前 maxLog（前端履历容量）。</summary>
    internal IResult HandleGetConfigAsync()
    {
        var maxLog = _config.GetValue<int>(ConfigCode.MaxLog);
        return JsonResult(new JsonObject { ["maxLog"] = maxLog });
    }

    /// <summary>
    /// GET /snapshot —— 全量显示状态快照（ADR-0013 决策一）。
    ///
    /// WS 晚加入者先调此端点拿初始全屏状态，再订阅 WS 收增量 ops。
    /// - 无活跃 session → 404 {"error":"No active session"}
    /// - session 未初始化（_console==null，POST /session 后极短窗口）→ 503
    /// - session 运行中或已结束（HasEnded=true）→ 200，body = DisplaySnapshot JSON
    ///   已结束 session 仍返回 200 + 最终状态（state="Quit"/"Error"），不返回 404——
    ///   晚加入者能看到游戏结束画面。
    /// </summary>
    internal IResult HandleGetSnapshotAsync()
    {
        var session = _sessions.CurrentSession;

        if (session == null)
            return JsonResult(new JsonObject { ["error"] = "No active session" }, statusCode: 404);

        var json = session.GetDisplaySnapshot();
        if (json == null)
            return JsonResult(new JsonObject { ["error"] = "Session not yet initialized" }, statusCode: 503);

        return Results.Text(json, "application/json", Encoding.UTF8, 200);
    }

    /// <summary>DELETE /session —— 销毁会话。有 → 200 {removed:true}；无 → 404 {removed:false}。</summary>
    internal async Task<IResult> HandleDeleteSessionAsync()
    {
        return await HandleDeleteSessionCoreAsync(null);
    }

    private async Task<IResult> HandleDeleteSessionForHttpAsync(HttpContext context)
    {
        return await HandleDeleteSessionCoreAsync(ToIdentity(ReadToken(context)));
    }

    private async Task<IResult> HandleDeleteSessionCoreAsync(ControlIdentity? identity)
    {
        var result = await _sessions.DeleteSessionAsync(identity);
        return result.Status switch
        {
            SessionDeleteStatus.Removed => JsonResult(new JsonObject { ["removed"] = true }, statusCode: 200),
            SessionDeleteStatus.ControlDenied => ControlErrorResult(result.Reason ?? "CONTROL_NOT_CONTROLLER", "Only the current Controller may change the session", _sessions.CurrentSession),
            _ => JsonResult(new JsonObject { ["removed"] = false }, statusCode: 404),
        };
    }

    /// <summary>
    /// POST /load-game {gameDir} —— 原子重载游戏目录（issue 05）。
    ///
    /// body 解析与格式校验在此（锁外），路径校验/拆旧/重建 ConfigData/建新 Session 的
    /// 原子序列见 <see cref="SessionRegistry.ReplaceForLoadGameAsync"/>。
    ///
    /// 错误契约（spec L229）：
    /// - 格式错误 → 400 {error:{code:"INVALID_JSON"|"MISSING_GAME_DIR"}}
    /// - 路径级错误（DIR_NOT_FOUND / MISSING_CSV / MISSING_ERB）→ 400 {error:{code,message}}
    /// - 加载级失败 → 500 {error:{code:"LOAD_FAILED",message}}
    ///   异步阶段（Preload.Load / Process.Initialize）失败由 ConsoleStateManager 置 State=Error，
    ///   前端经 GET /snapshot 看到"先警告后 state=Error"（issue 11 D4）。
    ///
    /// 限制：ConfigData.configPath 是实例 readonly（issue 12），构造时绑定当时
    /// GamePaths.Current.ExeDir；重载到含 emuera.config 的新目录时经
    /// GameConfigService.Reload(exeDir) 显式读取（v1 已解决原 static 绑定问题）。
    /// </summary>
    internal async Task<IResult> HandleLoadGameAsync(HttpContext context)
    {
        string? gameDir;
        string? bodyToken = null;
        using (var reader = new StreamReader(context.Request.Body))
        {
            var body = await reader.ReadToEndAsync();
            try
            {
                var payload = JsonSerializer.Deserialize(body, ServerJsonContext.Default.LoadGameRequest);
                gameDir = payload?.gameDir;
                bodyToken = payload?.token;
            }
            catch
            {
                return JsonResult(
                    new JsonObject { ["error"] = new JsonObject { ["code"] = "INVALID_JSON", ["message"] = "Invalid JSON, expected {\"gameDir\":\"...\"}" } },
                    statusCode: 400);
            }
        }

        if (string.IsNullOrWhiteSpace(gameDir))
            return JsonResult(
                new JsonObject { ["error"] = new JsonObject { ["code"] = "MISSING_GAME_DIR", ["message"] = "Missing or empty 'gameDir' field" } },
                statusCode: 400);

        var result = await _sessions.ReplaceForLoadGameAsync(gameDir, ToIdentity(bodyToken ?? ReadToken(context)));
        return result.Status switch
        {
            LoadGameStatus.PathError => JsonResult(
                new JsonObject { ["error"] = new JsonObject { ["code"] = result.Code, ["message"] = result.Message } },
                statusCode: 400),
            LoadGameStatus.LoadFailed => JsonResult(
                new JsonObject { ["error"] = new JsonObject { ["code"] = "LOAD_FAILED", ["message"] = result.Message } },
                statusCode: 500),
            LoadGameStatus.ControlDenied => ControlErrorResult(result.Code ?? "CONTROL_NOT_CONTROLLER", result.Message ?? "Only the current Controller may change the session", _sessions.CurrentSession),
            _ => JsonResult(new JsonObject
            {
                ["sessionId"] = result.SessionId,
                ["state"] = result.State,
                ["gameDir"] = result.GameDir,
            }),
        };
    }

    private static async Task<ControlRequest?> ReadControlRequestAsync(HttpContext context)
    {
        if (context.Request.ContentLength is null or 0)
            return null;
        try
        {
            using var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync();
            return string.IsNullOrWhiteSpace(body)
                ? null
                : JsonSerializer.Deserialize(body, ServerJsonContext.Default.ControlRequest);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ReadToken(HttpContext context)
    {
        if (context.Request.Headers.TryGetValue("X-Control-Token", out var headerToken))
            return headerToken.ToString();
        if (context.Request.Headers.TryGetValue("Authorization", out var authorization))
        {
            var value = authorization.ToString();
            if (value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                return value[7..];
        }
        return context.Request.Query["token"].ToString();
    }

    private static ControlIdentity ToIdentity(string? token)
    {
        return string.IsNullOrWhiteSpace(token)
            ? ControlIdentity.User
            : ControlIdentity.Agent(token);
    }

    private static JsonObject? ControllerNode(ControllerInfo? info)
    {
        if (info == null)
            return null;
        var node = new JsonObject
        {
            ["kind"] = info.Kind,
            ["leaseExpiresAt"] = info.LeaseExpiresAt,
        };
        return node;
    }

    private static JsonObject ControlEventNode(ControlEvent controlEvent)
    {
        return new JsonObject
        {
            ["event"] = controlEvent.Type,
            ["type"] = controlEvent.Type,
            ["reason"] = controlEvent.Reason,
            ["at"] = controlEvent.At,
            ["controller"] = ControllerNode(controlEvent.Controller),
            ["state"] = controlEvent.State,
        };
    }

    private static JsonNode ParseTurn(string? turn)
    {
        if (turn == null)
            return null!;
        try
        {
            return JsonNode.Parse(turn) ?? JsonValue.Create(turn)!;
        }
        catch (JsonException)
        {
            return JsonValue.Create(turn)!;
        }
    }

    private static IResult JsonResult(JsonObject payload, int statusCode = 200)
    {
        var body = payload.ToJsonString(new JsonSerializerOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
        return Results.Text(body, "application/json", Encoding.UTF8, statusCode);
    }

    private static IResult ErrorResult(string code, string message, int statusCode)
    {
        return JsonResult(new JsonObject
        {
            ["error"] = code,
            ["code"] = code,
            ["message"] = message,
        }, statusCode: statusCode);
    }

    private static IResult ControlErrorResult(string code, string message, Session? session)
    {
        return JsonResult(new JsonObject
        {
            ["error"] = code,
            ["code"] = code,
            ["message"] = message,
            ["reason"] = code,
            ["hint"] = "重新 acquire 可获取快照",
            ["controller"] = ControllerNode(session?.Controller.CurrentInfo),
            ["state"] = session?.Controller.State ?? Controller.StateIdle,
        }, statusCode: 409);
    }

    private static IResult ControlLostResult(string reason, DateTimeOffset at, Session session)
    {
        return JsonResult(new JsonObject
        {
            ["error"] = "CONTROL_LOST",
            ["code"] = "CONTROL_LOST",
            ["reason"] = reason,
            ["at"] = at,
            ["controller"] = ControllerNode(session.Controller.CurrentInfo),
            ["state"] = session.Controller.State,
        }, statusCode: 409);
    }

    private static TimeSpan ReadAgentLease()
    {
        var raw = Environment.GetEnvironmentVariable("EMUERA_CONTROL_LEASE_SECONDS");
        return int.TryParse(raw, out var seconds) && seconds > 0
            ? TimeSpan.FromSeconds(seconds)
            : TimeSpan.FromMinutes(5);
    }

    /// <summary>
    /// POST /native/pick-directory —— 安卓 SAF 目录选择器桩（issue 05）。
    ///
    /// 当前桩实现：返 200 {platform:"web", supported:false, message:"Not implemented on this platform"}。
    /// MAUI 阶段由 .NET MAUI 壳替换为真实现（调 Android Storage Access Framework 目录选择器）。
    /// 前端拿到 supported=false 时回退到路径输入框。
    /// </summary>
    private IResult HandlePickDirectoryAsync()
    {
        return JsonResult(new JsonObject
        {
            ["platform"] = "web",
            ["supported"] = false,
            ["message"] = "Not implemented on this platform",
        });
    }

    /// <summary>
    /// GET /assets/{path} —— 游戏目录图片资源通道（issue 03，spec Q3 路线 B / Q5 安全）。
    ///
    /// 前端拿协议里的相对路径（如 <c>img/portrait.png</c>）拼 <c>/assets/</c> URL 即得；
    /// 浏览器原生缓存（Cache-Control）让状态屏每回合重印同一画像零流量。
    ///
    /// 安全规则（spec L116-118）：只服务图片扩展名白名单；路径消毒拒绝穿越（统一走
    /// <see cref="AssetPathValidator"/>，与 05 安卓 PathHandler 共用同一函数）；CORS 头
    /// 允许 canvas 读像素算 srcm 映射色。任何失败一律 404（不泄露资源是否存在）。
    /// </summary>
    internal async Task HandleGetAssetAsync(HttpContext context, string path)
    {
        // issue 05：四步（消毒/白名单/读字节/MIME）收敛到 AssetChannel——与 MAUI 双平台
        // （Windows 拦截 + 安卓 PathHandler）共用同一函数，spec Q5「单点实现」真正成立。
        var paths = GamePaths.Current;
        if (paths?.DirAccessor == null
            || !AssetChannel.TryGetImage(paths.DirAccessor, paths.ExeDir, path, out var bytes, out var mime))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = mime;
        // 头策略与 MAUI 双平台共用（AssetChannel 常量单点）——改缓存/CORS 只动 Core
        context.Response.Headers.CacheControl = AssetChannel.CacheControlHeader;
        context.Response.Headers.AccessControlAllowOrigin = AssetChannel.CorsAllowOriginHeader;
        await context.Response.Body.WriteAsync(bytes);
    }

    internal sealed class HttpInput
    {
        public string? value { get; set; }
        public string? token { get; set; }
    }

    internal sealed class ControlRequest
    {
        public string? token { get; set; }
    }

    internal sealed class LoadGameRequest
    {
        public string? gameDir { get; set; }
        public string? token { get; set; }
    }

    public void Dispose()
    {
        // 先销毁 session（join 游戏循环，scope 释放安全），再释放 Kestrel
        _sessions.DisposeAll();
        ((IDisposable)_app).Dispose();
    }
}
