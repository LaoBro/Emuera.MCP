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
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Runtime.Utils;
using MinorShift.Emuera.Terminal.Platform;

namespace MinorShift.Emuera.Server;

internal sealed class KestrelGameServer : IDisposable
{
    private const int TurnWaitTimeoutMs = 25000;

    /// <summary>无可用 session 时 /ws 升级被拒，下发的自定义 WS 关闭码（未在 IANA 注册，借用于"应用层错误"语义）。</summary>
    private const WebSocketCloseStatus WsCloseNoActiveSession = (WebSocketCloseStatus)4004;

    private readonly WebApplication _app;
    private readonly ITerminalSetup _terminalSetup;
    /// <summary>
    /// 当前 ConfigData。issue 05 起可变——/load-game 重载游戏目录时整体重建并替换。
    /// 替换发生在 _sessionLock 内，新 Session 构造时拿到新 ConfigData 引用。
    /// </summary>
    private ConfigData _configData;
    private volatile Session? _session;
    /// <summary>
    /// 异步兼容锁——issue 05 起 /load-game 需在持锁期间 await ConfigData.LoadConfig 等同步步骤，
    /// 故从 <c>object</c> + <c>lock</c> 改为 <c>SemaphoreSlim(1,1)</c>。
    /// 串行化 /session、/load-game、DELETE /session 三个会话变更操作；
    /// /input、/snapshot、/ws 仅读 <c>_session</c> volatile 引用，不入锁。
    /// </summary>
    private readonly SemaphoreSlim _sessionLock = new(1, 1);

    public KestrelGameServer(int port, ITerminalSetup terminalSetup, ConfigData configData)
    {
        _terminalSetup = terminalSetup;
        _configData = configData;

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
        _app.MapGet("/turn", (Delegate)HandleGetTurnAsync);
        _app.MapPost("/input", (Delegate)HandlePostInputAsync);
        _app.MapGet("/state", (Delegate)HandleGetStateAsync);
        _app.MapGet("/snapshot", (Delegate)HandleGetSnapshotAsync);
        _app.MapDelete("/session", (Delegate)HandleDeleteSessionAsync);
        _app.MapGet("/ws", (Delegate)HandleWebSocketAsync);
        // issue 05：游戏选择器端点——/load-game 重载游戏目录，/native/pick-directory 安卓 SAF 桩
        _app.MapPost("/load-game", (Delegate)HandleLoadGameAsync);
        _app.MapPost("/native/pick-directory", (Delegate)HandlePickDirectoryAsync);
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

    internal async Task<IResult> HandleCreateSessionAsync()
    {
        bool conflict;
        string? sessionId = null;
        DateTimeOffset createdAt = default;
        string? state = null;

        await _sessionLock.WaitAsync();
        try
        {
            // T-025 D4/D16：空闲态守卫——_session == null 表示尚未加载游戏（POST /load-game
            // 成功才会建 session）。返 503 + {"error":"No game loaded"}，早于「已有活跃会话 409」判断。
            // 与 GET /snapshot 的 503（已加载但 session 未初始化，"Session not yet initialized"）语义区分。
            if (_session == null)
            {
                return Results.Json(new { error = "No game loaded" }, statusCode: 503);
            }

            if (!_session.HasEnded)
            {
                conflict = true;
            }
            else
            {
                conflict = false;
                _session.Dispose();
                _session = null;

                var io = new HttpSessionIO(new OutputHub());
                _session = new Session(io, _terminalSetup, _configData);
                _session.Start();
                sessionId = _session.Id;
                createdAt = _session.CreatedAt;
                state = _session.StateString;
            }
        }
        finally
        {
            _sessionLock.Release();
        }

        if (conflict)
            return Results.Json(new { error = "A session is already active" }, statusCode: 409);

        return Results.Json(new { sessionId, createdAt, state }, statusCode: 201);
    }

    private async Task<IResult> HandleGetTurnAsync(HttpContext context)
    {
        var session = _session;

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
        var session = _session;

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

    /// <summary>
    /// GET /state —— 当前会话状态 + gameDir（issue 05）+ 窗口布局元信息（issue 12）。
    ///
    /// 前端 App.vue 挂载时调用：判断 server 是否空闲（gameDir==null / state=="Idle"），
    /// 空闲则展示选择器并预填 localStorage 上次目录（T-025 D9 rev，不再自动 loadGame）。
    ///
    /// T-025 D5/D15：空闲态（_session==null）gameDir 显式置 null——前端据此可靠判 idle。
    /// 即使带了 --ExeDir valid_dir，idle 态仍返 gameDir: null（"已加载游戏的目录"与
    /// GamePaths.Current 内部路径是两个概念）。其余窗口元信息字段照常由默认 ConfigData 提供。
    ///
    /// Issue 12：新增 windowWidth / fontSize / lineHeight / gameColumns / fontName 五个字段。
    /// gameColumns = DrawableWidth / (FontSize/2)，与 CLI 模式
    /// TerminalLineFormatter.GetGameColumnWidth() 一致——前端用此值以 CSS ch 单位
    /// 设置容器宽度，让浏览器 monospace 字体宽度自适应（GDI ASCII=FontSize/2≈0.5em，
    /// 浏览器 monospace≈0.6em，若按 windowWidth 像素布局则字符画溢出容器）。
    /// fontName 来自 ConfigCode.FontName（默认 "ＭＳ ゴシック"）——前端将其作为 font-family
    /// 首选，浏览器找不到时再 fallback 到 ui-monospace 链。ASCII 字符画对字体宽度高度敏感，
    /// "ＭＳ ゴシック"（GDI 18px）与浏览器默认 monospace（如 Consolas）字形差异显著，
    /// 不读游戏字体名会让字符画视觉走形。
    /// Idle 分支也带这些字段，避免前端初始 fallback 偏差。/load-game 重建 ConfigData
    /// 后再次 GET /state 会拿到新游戏的窗口宽度。
    /// </summary>
    internal IResult HandleGetStateAsync()
    {
        var session = _session;
        var windowWidth = _configData.GetConfigValue<int>(ConfigCode.WindowX);
        var fontSize = _configData.GetConfigValue<int>(ConfigCode.FontSize);
        var lineHeight = _configData.GetConfigValue<int>(ConfigCode.LineHeight);
        var fontName = _configData.GetConfigValue<string>(ConfigCode.FontName);
        // gameColumns = DrawableWidth / charWidth，与 CLI TerminalLineFormatter.GetGameColumnWidth() 一致。
        // Headless 模式 TextDrawingMode != WINAPI，故 ShapePositionShift = Max(2, FontSize/6)。
        int charWidth = Math.Max(fontSize / 2, 1);
        int shapeShift = Math.Max(2, fontSize / 6);
        int drawableWidth = windowWidth - shapeShift;
        int gameColumns = drawableWidth / charWidth;

        if (session == null)
            // T-025 D5：空闲态 gameDir 显式置 null——前端据此可靠判 idle 并展示选择器。
            return Results.Json(new
            {
                state = "Idle",
                isRunning = false,
                gameDir = (string?)null,
                windowWidth,
                fontSize,
                lineHeight,
                gameColumns,
                fontName,
            });

        return Results.Json(new
        {
            state = session.StateString,
            isRunning = session.IsRunning,
            sessionId = session.Id,
            createdAt = session.CreatedAt,
            gameDir = GamePaths.Current.ExeDir,
            windowWidth,
            fontSize,
            lineHeight,
            gameColumns,
            fontName,
        });
    }

    /// <summary>
    /// GET /snapshot —— 全量显示状态快照（ADR-0013 决策一）。
    ///
    /// WS 晚加入者先调此端点拿初始全屏状态，再订阅 WS 收增量 ops。
    /// - 无活跃 session → 404 `{"error":"No active session"}`
    /// - session 未初始化（_console==null，POST /session 后极短窗口）→ 503
    /// - session 运行中或已结束（HasEnded=true）→ 200，body = DisplaySnapshot JSON
    ///
    /// 已结束 session 仍返回 200 + 最终状态（state="Quit"/"Error"），不返回 404——
    /// 晚加入者能看到游戏结束画面。
    /// </summary>
    private IResult HandleGetSnapshotAsync()
    {
        var session = _session;

        if (session == null)
            return Results.Json(new { error = "No active session" }, statusCode: 404);

        var json = session.GetDisplaySnapshot();
        if (json == null)
            return Results.Json(new { error = "Session not yet initialized" }, statusCode: 503);

        return Results.Text(json, "application/json", Encoding.UTF8, 200);
    }

    internal async Task<IResult> HandleDeleteSessionAsync()
    {
        bool removed;
        await _sessionLock.WaitAsync();
        try
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
        finally
        {
            _sessionLock.Release();
        }

        return Results.Json(new { removed }, statusCode: removed ? 200 : 404);
    }

    /// <summary>
    /// POST /load-game {gameDir} —— 原子重载游戏目录（issue 05）。
    ///
    /// 全程序在 _sessionLock 内完成（spec L224-229）：
    /// 1. 校验路径（拆旧前拦路径级错误）—— GamePaths.Resolve + Validate，失败抛 GamePathValidationException
    /// 2. dispose 旧 Session（若有）
    /// 3. Preload.Clear()
    /// 4. GamePaths.Resolve(newDir)（步骤 1 已完成——重赋静态 GamePaths.Current）
    /// 5. 重建 ConfigData：new ConfigData().LoadConfig(), ConfigData.SetCurrent()
    /// 6. Preload.Load 由新 Session 的 ConsoleStateManager.Initialize 异步执行（issue 11 D2：
    ///    去除 server 端双重 Preload——保留 ConsoleStateManager 入口供 CLI 共用）
    /// 7. 建新 Session（传入新 _configData）
    /// 8. 返 {sessionId, state, gameDir}
    ///
    /// 错误契约（spec L229）：
    /// - 路径级错误（DIR_NOT_FOUND / MISSING_CSV / MISSING_ERB）→ 400 `{error:{code,message}}`
    /// - 加载级失败（ConfigData.LoadConfig 等同步步骤异常、未预期异常）→ 500 `{error:{code:"LOAD_FAILED",message:"..."}}`
    ///   异步阶段（Preload.Load / Process.Initialize）失败由 ConsoleStateManager 置 State=Error，
    ///   前端经 GET /snapshot 看到"先警告后 state=Error"——issue 11 D4 自然可见，无需同步 500。
    ///
    /// 限制：ConfigData.configPath 是 `static readonly`，首次 ConfigData 构造时绑定 Program.ExeDir。
    /// 重载到含 emuera.config 的新目录时，该 config 文件不会被读取——v1 已知限制，
    /// 测试游戏（test_game）无 emuera.config，不受影响。后续 spec 可让 configPath 改为实例字段。
    /// </summary>
    internal async Task<IResult> HandleLoadGameAsync(HttpContext context)
    {
        string? gameDir;
        using (var reader = new StreamReader(context.Request.Body))
        {
            var body = await reader.ReadToEndAsync();
            try
            {
                var payload = JsonSerializer.Deserialize<LoadGameRequest>(body);
                gameDir = payload?.gameDir;
            }
            catch
            {
                return Results.Json(
                    new { error = new { code = "INVALID_JSON", message = "Invalid JSON, expected {\"gameDir\":\"...\"}" } },
                    statusCode: 400);
            }
        }

        if (string.IsNullOrWhiteSpace(gameDir))
            return Results.Json(
                new { error = new { code = "MISSING_GAME_DIR", message = "Missing or empty 'gameDir' field" } },
                statusCode: 400);

        await _sessionLock.WaitAsync();
        try
        {
            // 1. 校验路径——GamePathValidationException 转 400。
            //
            // 注意：GamePaths.Resolve(gameDir) 内部会先把静态 Current 指向新 paths 再返回。
            // 若 Validate 抛异常，Current 已被污染——spec L229 要求"拆旧前拦路径级错误"，
            // 即 server 状态不应被失败的 /load-game 改动。故在 catch 内回滚 Current 到旧值
            // （旧值 = 进入 /load-game 前的 GamePaths.Current，可能为 null 启动期场景）。
            GamePaths paths;
            GamePaths? previousPaths = GamePaths.Current;
            try
            {
                paths = GamePaths.Resolve(gameDir);
                paths.Validate();
            }
            catch (GamePathValidationException ex)
            {
                // 回滚 Current 到校验前的值——避免 GET /state 返回被拒绝的非法路径
                if (previousPaths != null)
                {
                    GamePaths.SetCurrent(previousPaths);
                }
                return Results.Json(
                    new { error = new { code = ex.Code, message = ex.Message } },
                    statusCode: 400);
            }

            // 2. dispose 旧 Session（若有）—— Dispose 等待旧 GameLoopAsync 退出，scope 自动清理
            if (_session != null)
            {
                _session.Dispose();
                _session = null;
            }

            // 3. Preload.Clear —— 清空旧 ERB/CSV 文件缓存
            Preload.Clear();

            // 4. GamePaths.Resolve 已在步骤 1 完成——GamePaths.Current 指向新目录

            // 5. 重建 ConfigData 并 SetCurrent（HTTP 线程 AsyncLocal——主要供新 Session 异步 Preload.Load 间接读）
            // Issue 12：使用 LoadConfig(paths.ExeDir) 显式读取新游戏目录的 emuera.config，
            // 避免 ConfigData.configPath 静态绑定到 Program.ExeDir 导致读不到新配置。
            var newConfig = new ConfigData();
            newConfig.LoadConfig(paths.ExeDir);
            ConfigData.SetCurrent(newConfig);
            _configData = newConfig;

            // 6. Preload.Load 已下沉到 ConsoleStateManager.Initialize（issue 11 D2：去重 server 端双重 Preload，
            //    保留 CLI/server 共用入口）——见 ConsoleStateManager.cs:58-60。新 Session.Start() 排
            //    GameLoopAsync 到独立 Task，_loading=true 让 StateString 返 "Loading"（issue 11 D1）。
            //    异步阶段的文件 I/O 失败由 ConsoleStateManager 置 State=Error，前端经 snapshot 自然可见。

            // 7. 建新 Session——GameLoopAsync 内 GameLoopComposer.OpenScope(_configData) 注入新配置
            var io = new HttpSessionIO(new OutputHub());
            var newSession = new Session(io, _terminalSetup, _configData);
            newSession.Start();
            _session = newSession;

            // 8. 返 {sessionId, state, gameDir}——state="Loading"（D1 落地后自动生效）
            return Results.Json(new
            {
                sessionId = newSession.Id,
                state = newSession.StateString,
                gameDir = paths.ExeDir,
            });
        }
        catch (Exception ex)
        {
            // 兜底：未预期异常归为 LOAD_FAILED
            Console.Error.WriteLine("[load-game] unexpected exception");
            Console.Error.WriteLine(ex);
            return Results.Json(
                new { error = new { code = "LOAD_FAILED", message = ex.Message } },
                statusCode: 500);
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    /// <summary>
    /// POST /native/pick-directory —— 安卓 SAF 目录选择器桩（issue 05）。
    ///
    /// 当前桩实现：返 200 `{platform:"web", supported:false, message:"Not implemented on this platform"}`。
    /// MAUI 阶段由 .NET MAUI 壳替换为真实现（调 Android Storage Access Framework 目录选择器）。
    /// 前端拿到 supported=false 时回退到路径输入框。
    /// </summary>
    private IResult HandlePickDirectoryAsync()
    {
        return Results.Json(new
        {
            platform = "web",
            supported = false,
            message = "Not implemented on this platform",
        });
    }

    private sealed class HttpInput
    {
        public string? value { get; set; }
    }

    private sealed class LoadGameRequest
    {
        public string? gameDir { get; set; }
    }

    /// <summary>
    /// GET /ws —— WebSocket 旁路传输端点（Hub 旁路模式，非另一个 SessionIO）。
    ///
    /// 生命周期：
    /// - 无活跃 session 时接受升级后立即以下发关闭码 4004 退回。
    /// - 有 session：锁内完成 accept→re-check→subscribe 原子序列，避免 session 被
    ///   另一线程 DELETE+POST 替换后仍从旧 hub 订阅。
    /// - 任一侧结束 / WS 关闭 → 取消另一循环并退订；session 结束（hub.Complete）以 WS
    ///   关闭帧通知。
    /// </summary>
    private async Task HandleWebSocketAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = 400;
            return;
        }

        using var ws = await context.WebSockets.AcceptWebSocketAsync();

        ChannelReader<string>? reader = null;
        Session? session = null;
        OutputHub? hub = null;

        await _sessionLock.WaitAsync();
        try
        {
            session = _session;
            if (session is { HasEnded: false })
            {
                hub = session.IO.Hub;
                reader = hub?.Subscribe();
            }
        }
        finally
        {
            _sessionLock.Release();
        }

        if (reader == null)
        {
            await ws.CloseAsync(WsCloseNoActiveSession, "No active session", CancellationToken.None);
            return;
        }

        using var cts = new CancellationTokenSource();

        try
        {
            await Task.WhenAny(
                SendLoopAsync(ws, reader, cts.Token),
                ReceiveLoopAsync(ws, session!, cts.Token)
            );
        }
        finally
        {
            cts.Cancel();
            hub!.Unsubscribe(reader);
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

    /// <summary>WS 输入帧已是 <c>{"type":"input","value":"..."}</c> 格式，直接入队。
    /// 协议层 <c>AgentJsonlProtocol</c> 会校验 <c>type=="input"</c>，无效帧自然被忽略。</summary>
    private static void HandleWsInput(string text, Session session)
    {
        session.IO.EnqueueInput(text);
    }

    public void Dispose()
    {
        _sessionLock.Wait();
        try
        {
            _session?.Dispose();
            _session = null;
        }
        finally
        {
            _sessionLock.Release();
        }
        ((IDisposable)_app).Dispose();
    }
}
