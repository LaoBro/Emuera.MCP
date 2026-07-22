using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Emuera.Maui.JsBridge;
using MinorShift.Emuera;
using MinorShift.Emuera.GameView;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Server;
using MinorShift.Emuera.Terminal.Platform;

namespace Emuera.Maui;

/// <summary>
/// MAUI 桥接编排器——issue 07 / 08 / 09 / spec ID8 / ID9 / ID10。
/// <para>
/// 持有 <see cref="MauiBridgeIO"/> + <see cref="IJsBridge"/>，在游戏循环线程与 UI 线程之间转发 turn / input。
/// 构造时创建 <see cref="MauiBridgeIO"/>（<c>_onTurn</c> 回调内 <c>Dispatcher.Dispatch</c> 切 UI 线程投递给 WebView），
/// 订阅 <see cref="IJsBridge.InputReceived"/> 接收 Vue 端 postMessage。
/// </para>
/// <para>
/// <b>启动时序（spec ID7）</b>：构造不启动游戏循环。Vue 启动后 <c>postMessage({"type":"ready"})</c>，
/// <see cref="OnInputFromJs"/> 识别 ready 后调 <see cref="Start"/>，<see cref="Task.Run"/> 启动
/// <see cref="GameLoopComposer.RunAsync"/>。Vue ready 是游戏循环启动的前置条件，第一帧 turn 自然推给已 ready 的 Vue。
/// </para>
/// <para>
/// <b>致命错误处理（spec ID10）</b>：<see cref="GameLoopComposer.RunAsync"/> 抛非 <see cref="GameExitException"/> 异常时
/// <see cref="ShowFatalError"/> 推 error turn 给 Vue（Vue 渲染 error 字段），fallback 用 <c>DisplayAlert</c>。
/// 单 step 脚本异常（ERB THROW / 除零等）由 <c>AgentJsonlProtocol.StepAsync</c> 内 catch 处理，不传播到 <see cref="ShowFatalError"/>。
/// </para>
/// <para>
/// <b>文件选择器（issue 09）</b>：Vue 端 <c>pickGameFolder()</c> 投递 <c>{"type":"pickFolder"}</c> →
/// <see cref="HandlePickFolder"/> 调 <see cref="IJsBridge.PickFolderAsync"/> 弹原生选择器 →
/// 经 <see cref="IJsBridge.PostMessage"/> 推 <c>{"type":"folderPicked","path":...}</c> 回 Vue →
/// Vue 端 <c>loadGameFromPath(path)</c> 投递 <c>{"type":"loadGame","path":...}</c> →
/// <see cref="HandleLoadGame"/> 调 <c>_onReloadGame(path)</c> 让 <c>MainPage</c> 重建 BridgeHost。
/// </para>
/// <remarks>
/// 生命周期：在 <c>MainPage</c> 构造时 new（不启动游戏循环），在 <c>MainPage.OnDisappearing</c> 或
/// <c>MainPage.RecreateHost</c>（issue 09 hot-swap reload）时 <see cref="Dispose"/>。
/// <see cref="ConfigData"/> / <see cref="ITerminalSetup"/> 单例由 <c>MauiProgram</c> DI 注入，
/// <c>MainPage</c> 重建时不重新初始化运行时；issue 09 hot-swap 时 <c>MainPage</c> 重新调
/// <c>EmueraRuntimeInitializer.Initialize</c> 替换字段后重建 BridgeHost。
/// </remarks>
internal sealed class BridgeHost : IDisposable
{
    private readonly IDispatcher _dispatcher;
    private readonly ConfigData _configData;
    private readonly ITerminalSetup _terminalSetup;
    private readonly IJsBridge _jsBridge;
    private readonly MauiBridgeIO _bridgeIO;
    private readonly Action<string>? _onReloadGame;
    private readonly CancellationTokenSource _cts = new();
    private Task? _gameTask;
    private bool _disposed;
    private bool _started;
    private bool _readyReceived;

    /// <summary>
    /// 构造桥接宿主——不启动游戏循环（spec ID7 延迟启动）。
    /// <para>
    /// <b>Spec 偏离说明</b>：spec ID8 伪代码签名含 <c>webView</c> 参数，实现省略——
    /// <c>IJsBridge.Attach(webView)</c> 由 <see cref="MainPage"/> 在 <c>HandlerChanged</c> UI 事件内直接调
    /// （WebView 平台原生视图就绪的最早可靠时机，UI 线程约束），<see cref="BridgeHost"/> 仅编排 turn/input 流转，
    /// 不持 <c>webView</c> 引用避免跨线程访问风险。
    /// </para>
    /// <para>
    /// <b>可访问性</b>：ctor 标 <c>internal</c>——参数类型 <see cref="ConfigData"/> / <see cref="ITerminalSetup"/>
    /// 在 <c>Emuera.Headless.Core</c> 内为 <c>internal</c>，C# 规则要求方法可访问性不得高于参数类型
    /// （与 <see cref="MainPage"/> ctor 一致）。
    /// </para>
    /// <para>
    /// <b>issue 09 reload 回调</b>：<paramref name="onReloadGame"/> 由 <see cref="MainPage"/> 提供——
    /// <see cref="HandleLoadGame"/> 收到 <c>{"type":"loadGame","path":...}</c> 时调此回调，
    /// 让 MainPage 重新初始化运行时 + 重建 BridgeHost（hot-swap reload）。
    /// </para>
    /// </summary>
    /// <param name="dispatcher">MAUI <see cref="IDispatcher"/>——用于把游戏循环线程的 turn 回调切到 UI 线程调 <see cref="IJsBridge.PostTurn"/>。</param>
    /// <param name="configData">已加载的 <see cref="ConfigData"/> 单例（<c>MauiProgram</c> 初始化时加载）。</param>
    /// <param name="terminalSetup">已启用 ANSI 的 <see cref="ITerminalSetup"/> 单例。</param>
    /// <param name="jsBridge">平台 <see cref="IJsBridge"/> 实现（Windows / Android）。</param>
    /// <param name="onReloadGame">issue 09 hot-swap reload 回调——传游戏目录绝对路径让 MainPage 重建 BridgeHost。</param>
    internal BridgeHost(
        IDispatcher dispatcher,
        ConfigData configData,
        ITerminalSetup terminalSetup,
        IJsBridge jsBridge,
        Action<string>? onReloadGame = null)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _configData = configData ?? throw new ArgumentNullException(nameof(configData));
        _terminalSetup = terminalSetup ?? throw new ArgumentNullException(nameof(terminalSetup));
        _jsBridge = jsBridge ?? throw new ArgumentNullException(nameof(jsBridge));
        _onReloadGame = onReloadGame;

        // MauiBridgeIO 的 _onTurn 回调在游戏循环线程执行——Dispatcher.Dispatch 切 UI 线程投递给 WebView。
        _bridgeIO = new MauiBridgeIO(OnTurnFromGame);
        _jsBridge.InputReceived += OnInputFromJs;
    }

    /// <summary>
    /// 游戏循环线程的 turn 回调——<see cref="MauiBridgeIO.WriteLine"/> 调用。
    /// <para>
    /// <see cref="IJsBridge.PostTurn"/> 内部调 <c>EvaluateJavaScriptAsync</c> / <c>EvaluateJavaScript</c>，
    /// 必须在 UI 线程执行（CoreWebView2 / Android.Webkit.WebView 要求）。
    /// <see cref="IDispatcher.Dispatch"/> 是 fire-and-forget——不阻塞游戏循环线程。
    /// </para>
    /// </summary>
    private void OnTurnFromGame(string turnJson)
    {
        _dispatcher.Dispatch(() => _jsBridge.PostTurn(turnJson));
    }

    /// <summary>
    /// JS → C# 消息处理——<see cref="IJsBridge.InputReceived"/> 触发。
    /// <para>
    /// 识别四类消息：
    /// <list type="bullet">
    ///   <item><c>{"type":"ready"}</c>——首帧 ready 信号，调 <see cref="Start"/> 启动游戏循环（仅一次，幂等）</item>
    ///   <item><c>{"type":"pickFolder"}</c>——issue 09 文件选择器，调 <see cref="HandlePickFolder"/> 弹原生选择器</item>
    ///   <item><c>{"type":"loadGame","path":...}</c>——issue 09 hot-swap reload，调 <see cref="HandleLoadGame"/> 通知 MainPage 重建</item>
    ///   <item>其他（如 <c>{"type":"input","value":"..."}</c>）——原样入 <see cref="MauiBridgeIO.EnqueueInput"/>，
    ///       由 <c>AgentJsonlProtocol.RunLoopAsync</c> 反序列化消费</item>
    /// </list>
    /// </para>
    /// <para>
    /// 非 JSON 消息静默吞掉（仅写日志）——Vue 端 <c>postInput</c> 总发合法 JSON，
    /// 到此分支说明桥接层异常，但不阻塞游戏循环。
    /// </para>
    /// </summary>
    private void OnInputFromJs(string message)
    {
        // 三通道日志：Console（dotnet run 终端）+ AgentLog（持久化文件）+ Debug.WriteLine（VS 调试器）
        Console.WriteLine($"[bridge] OnInputFromJs: {message}");
        if (_disposed)
        {
            // Dispose 后到达的消息丢弃——游戏循环已取消 / IO 已关闭
            AgentLog.Instance.Write($"[bridge] input after dispose dropped: {message}");
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(message);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("type", out var typeEl)
                && typeEl.ValueKind == JsonValueKind.String)
            {
                var type = typeEl.GetString();
                if (type == "ready")
                {
                    HandleReady();
                    return;
                }
                if (type == "pickFolder")
                {
                    HandlePickFolder();
                    return;
                }
                if (type == "loadGame")
                {
                    HandleLoadGame(doc.RootElement);
                    return;
                }
                // 其他 typed 消息（input 等）原样入队——AgentJsonlProtocol.RunLoopAsync 内
                // JsonSerializer.Deserialize<JsonlCommand> 校验 type=="input" 后取 value。
                // 不在此处解 value 字段，避免与 protocol 层重复解析 / 不一致。
            }
        }
        catch (JsonException ex)
        {
            Console.WriteLine($"[bridge] non-JSON input received (ignored): {ex.Message}");
            AgentLog.Instance.Write($"[bridge] non-JSON input received (ignored): {ex.Message}");
            return;
        }

        // input / 其他 typed 消息：原样入队给 protocol 层消费
        _bridgeIO.EnqueueInput(message);
    }

    /// <summary>
    /// 处理 Vue ready 信号——首帧时调 <see cref="Start"/>，重复 ready 幂等忽略。
    /// </summary>
    private void HandleReady()
    {
        if (_readyReceived)
        {
            Console.WriteLine("[bridge] duplicate ready signal ignored");
            return;
        }
        _readyReceived = true;
        Console.WriteLine("[bridge] Vue ready signal received");
        AgentLog.Instance.Write("[bridge] Vue ready signal received");
        System.Diagnostics.Debug.WriteLine("[bridge] Vue ready signal received");
        Start();
    }

    /// <summary>
    /// 弹出原生文件夹选择器（issue 09）——UI 线程调用，结果异步推回 Vue。
    /// <para>
    /// 调 <see cref="IJsBridge.PickFolderAsync"/>（MAUI <c>FolderPicker.PickAsync</c>）弹原生选择器，
    /// 用户选中后经 <see cref="IJsBridge.PostMessage"/> 投递 <c>{"type":"folderPicked","path":...}</c> 回 Vue。
    /// 用户取消（path 为 null）静默 no-op，不发 folderPicked 消息——Vue 端 picking 状态由点击立即重置。
    /// </para>
    /// <para>
    /// <b>async void</b>：UI 事件回调常见模式——异常在 try/catch 内吞掉，不让 async void 异常逃逸到 SynchronizationContext。
    /// </para>
    /// </summary>
    private async void HandlePickFolder()
    {
        Console.WriteLine("[bridge] HandlePickFolder: opening native folder picker");
        AgentLog.Instance.Write("[bridge] HandlePickFolder: opening native folder picker");
        try
        {
            var path = await _jsBridge.PickFolderAsync();
            if (string.IsNullOrEmpty(path))
            {
                // 用户取消——不推 folderPicked，Vue 端 picking 状态由点击立即重置
                Console.WriteLine("[bridge] HandlePickFolder: user cancelled or path empty");
                return;
            }
            Console.WriteLine($"[bridge] HandlePickFolder: picked path={path}");
            var msgJson = JsonSerializer.Serialize(new { type = "folderPicked", path });
            _dispatcher.Dispatch(() => _jsBridge.PostMessage(msgJson));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[bridge] HandlePickFolder failed: {ex}");
            AgentLog.Instance.Write($"[bridge] HandlePickFolder failed: {ex}");
            // 推 error 事件给 Vue 让 UI 解除 picking 状态 + 显示错误
            var errJson = JsonSerializer.Serialize(new { type = "folderPicked", error = ex.Message });
            _dispatcher.Dispatch(() => _jsBridge.PostMessage(errJson));
        }
    }

    /// <summary>
    /// 处理 Vue 端 loadGame 消息（issue 09 hot-swap reload）——调 <c>_onReloadGame</c> 回调让 MainPage 重建。
    /// <para>
    /// Vue 端 <c>loadGameFromPath(path)</c> 投递 <c>{"type":"loadGame","path":...}</c>，
    /// 此方法解析 path 字段后调 <see cref="Action{T}"/> 回调。
    /// MainPage 收到回调后：后台线程跑 <c>EmueraRuntimeInitializer.Initialize</c> → UI 线程 Dispose 旧 BridgeHost +
    /// new 新 BridgeHost + <see cref="Start"/>（Vue 已 ready，无需再等 ready 信号）。
    /// </para>
    /// <para>
    /// <b>无回调时静默 no-op</b>：旧 BridgeHost（issue 08 版本）构造未传 <c>_onReloadGame</c>，
    /// 收到 loadGame 消息仅写日志，不抛错——保证向后兼容。
    /// </para>
    /// </summary>
    private void HandleLoadGame(JsonElement root)
    {
        if (!root.TryGetProperty("path", out var pathEl) || pathEl.ValueKind != JsonValueKind.String)
        {
            Console.WriteLine("[bridge] HandleLoadGame: missing or non-string 'path' field");
            AgentLog.Instance.Write("[bridge] HandleLoadGame: missing or non-string 'path' field");
            return;
        }
        var path = pathEl.GetString();
        if (string.IsNullOrEmpty(path))
        {
            Console.WriteLine("[bridge] HandleLoadGame: empty path");
            return;
        }
        Console.WriteLine($"[bridge] HandleLoadGame: path={path}");
        AgentLog.Instance.Write($"[bridge] HandleLoadGame: path={path}");
        if (_onReloadGame is null)
        {
            Console.WriteLine("[bridge] HandleLoadGame: no _onReloadGame callback (old BridgeHost without issue 09 support)");
            return;
        }
        try
        {
            _onReloadGame.Invoke(path);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[bridge] HandleLoadGame: _onReloadGame threw: {ex}");
            AgentLog.Instance.Write($"[bridge] HandleLoadGame: _onReloadGame threw: {ex}");
            ShowError("重新加载游戏失败: " + ex.Message);
        }
    }

    /// <summary>
    /// 启动游戏循环——<see cref="Task.Run"/> 后台执行 <see cref="GameLoopComposer.RunAsync"/>。
    /// <para>
    /// 仅首次调用启动游戏循环；重复调用（多次 ready 信号等）幂等忽略。
    /// <see cref="CancellationTokenSource"/> 控制取消——<see cref="Dispose"/> 调 <see cref="CancellationTokenSource.Cancel"/>。
    /// </para>
    /// <para>
    /// 异常处理在 <see cref="GameLoopAsync"/> 内：<see cref="GameExitException"/> 静默（ERB QUIT 正常退出），
    /// 其他异常调 <see cref="ShowFatalError"/> 推 error turn 给 Vue。
    /// </para>
    /// <para>
    /// <b>issue 09 hot-swap reload</b>：MainPage 重建 BridgeHost 后直接调 <see cref="Start"/>——
    /// Vue 已 ready（首次 ready 信号早已收到），无需再等 ready 信号。
    /// </para>
    /// </summary>
    internal void Start()
    {
        if (_started)
            return;
        _started = true;
        _readyReceived = true; // 标记 ready 已收到——避免后续 ready 信号重复触发 Start
        Console.WriteLine("[bridge] Starting game loop");
        AgentLog.Instance.Write("[bridge] starting game loop");
        _gameTask = Task.Run(GameLoopAsync);
    }

    /// <summary>
    /// 游戏循环主体——封装 <see cref="GameLoopComposer.RunAsync"/> 的异常边界。
    /// <para>
    /// <b>buildProtocol lambda（spec ID12）</b>：构造 <c>AgentJsonlProtocol</c> 时传 <c>DisplayState(console, defaultFontName)</c>，
    /// <c>defaultFontName</c> 从 <c>ConfigData.GetConfigValue&lt;string&gt;(ConfigCode.FontName) ?? ""</c> 读
    /// （与 <c>Session.cs:100</c> HTTP 模式一致，修正原 spec 空字符串 bug）。
    /// </para>
    /// <para>
    /// <b>runLoop lambda</b>：调 <c>((AgentJsonlProtocol)p).RunLoopAsync(enableTimeout: true, _cts.Token)</c>。
    /// <c>enableTimeout: true</c> 启用 TINPUT 超时分支（spec ID14：Windows 桌面窗口最小化不影响进程调度，
    /// 超时正常触发；Android 后台 Doze 冻结进程，超时延迟到回前台）。
    /// </para>
    /// <para>
    /// <b>异常分支</b>：
    /// <list type="bullet">
    ///   <item><see cref="GameExitException"/>——ERB QUIT/EXIT 正常退出，静默</item>
    ///   <item>其他 <see cref="Exception"/>——游戏循环整体崩溃，调 <see cref="ShowFatalError"/></item>
    /// </list>
    /// </para>
    /// <para>
    /// 单 step 脚本异常（ERB THROW / 除零等）由 <c>AgentJsonlProtocol.StepAsync</c> 内 catch 处理，
    /// <c>RunLoopAsync</c> 写 error turn 后 <c>Stop()</c> 让循环下一轮退出——不抛到此 catch。
    /// 此 catch 是 <c>console.Initialize()</c> / <c>GlobalStatic.OpenScope</c> 等同步路径的兜底。
    /// </para>
    /// </summary>
    private async Task GameLoopAsync()
    {
        try
        {
            await GameLoopComposer.RunAsync(
                _configData,
                _terminalSetup,
                (console, ui, ts) =>
                {
                    // 与 Session.cs:100 HTTP 模式一致——从 ConfigData 读默认字体名传给 DisplayState，
                    // 避免 BuildPrintOpsForLine 访问 Config.FontName 时 NRE
                    // （HTTP/MAUI 线程上 Config.Current AsyncLocal 不可用，仅游戏循环 task 内设置）。
                    var defaultFontName = _configData.GetConfigValue<string>(ConfigCode.FontName) ?? "";
                    // MAUI 模式无 GET /snapshot 端点——首帧 diff 是唯一画面来源，
                    // fullDiffOnFirstTurn=true 让 ComputeDiff 首帧返回全量 AppendLinesOp 而非 null。
                    // HTTP 模式靠 GET /snapshot 拿画面，首帧 diff=null 正确（默认 false）。
                    var displayState = new DisplayState(console, defaultFontName, fullDiffOnFirstTurn: true);
                    return new AgentJsonlProtocol(console, ui, _bridgeIO, displayState);
                },
                async p =>
                {
                    await ((AgentJsonlProtocol)p).RunLoopAsync(enableTimeout: true, _cts.Token);
                });
            Console.WriteLine("[bridge] game loop completed normally");
            AgentLog.Instance.Write("[bridge] game loop completed normally");
        }
        catch (GameExitException)
        {
            // ERB QUIT/EXIT：脚本请求正常退出，静默
            Console.WriteLine("[bridge] game loop exited via QUIT/EXIT");
            AgentLog.Instance.Write("[bridge] game loop exited via QUIT/EXIT");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[bridge] game loop fatal: {ex}");
            AgentLog.Instance.Write($"[bridge] game loop fatal: {ex}");
            // issue 09 hot-swap reload：Dispose 触发的 OperationCanceledException 是预期行为——
            // RecreateHost 调 Dispose → CTS.Cancel → ReadLineAsync 抛 OperationCanceledException。
            // 此时 Vue 已由新 BridgeHost 接管，旧 host 的 error turn 会污染 Vue 状态（覆盖新 host 的首帧），
            // 故 _disposed=true 时静默吞掉不推 error turn。
            // 同理覆盖 OnDisappearing（页面销毁）路径——Vue 都不在了，推 error turn 无意义。
            if (_disposed)
            {
                Console.WriteLine("[bridge] game loop exited via Dispose (reload/page close), suppressing error turn");
                AgentLog.Instance.Write("[bridge] game loop exited via Dispose (reload/page close), suppressing error turn");
                return;
            }
            // 游戏循环整体崩溃（Initialize 失败 / OpenScope 失败 / protocol 未捕获异常等）
            ShowFatalError(ex);
        }
    }

    /// <summary>
    /// 推致命错误 turn 给 Vue——spec ID10。
    /// <para>
    /// 构造 <c>TurnRecord(state:"Error", error: ex.Message)</c> JSON 调 <see cref="IJsBridge.PostTurn"/> 推给 Vue，
    /// Vue 端 <c>applyTurn</c> 渲染 error 字段。error turn 格式与 <c>AgentJsonlProtocol.StepAsync</c> 的脚本异常
    /// error turn 一致——前端代码零改动复用渲染逻辑。
    /// </para>
    /// <para>
    /// <b>fallback</b>：<see cref="IJsBridge.PostTurn"/> 失败（WebView 未 Attach / EvaluateJavaScriptAsync 抛异常）
    /// 时调 <c>MainThread.BeginInvokeOnMainThread</c> + <c>Application.Current.MainPage.DisplayAlert</c>
    /// 兜底展示错误。ID7 延迟启动保证 Vue ready 后才 <see cref="Start"/>，崩溃时 Vue 已在跑能接收 error turn。
    /// </para>
    /// </summary>
    private void ShowFatalError(Exception ex)
    {
        ShowError(ex.Message);
    }

    /// <summary>
    /// 推 error turn 给 Vue（issue 09 公开版本——可被外部 reload 路径调用）。
    /// <para>
    /// 与 <see cref="ShowFatalError"/> 共用渲染链路——构造 <c>TurnRecord(state:"Error", error: message)</c> JSON
    /// 调 <see cref="IJsBridge.PostTurn"/> 推给 Vue。失败 fallback <c>DisplayAlert</c>。
    /// </para>
    /// </summary>
    /// <param name="message">错误消息字符串（Vue error turn 的 error 字段）。</param>
    internal void ShowError(string message)
    {
        try
        {
            var errorTurn = JsonSerializer.Serialize(new TurnRecord(
                state: "Error",
                inputType: null,
                needValue: false,
                diff: null,
                error: message
            ), AgentJsonlProtocol.TurnJsonOptions);
            // _jsBridge.PostTurn 内部 fire-and-forget（async void），异常被自身 catch 不抛——
            // 但 try/catch 兜底防御：PostTurn 之外的序列化失败也走 DisplayAlert fallback。
            _dispatcher.Dispatch(() => _jsBridge.PostTurn(errorTurn));
            Console.WriteLine($"[bridge] error turn pushed to Vue: {errorTurn}");
            AgentLog.Instance.Write($"[bridge] error turn pushed to Vue");
        }
        catch (Exception postEx)
        {
            // PostTurn 失败——fallback DisplayAlert
            Console.WriteLine($"[bridge] PostTurn failed, fallback to DisplayAlert: {postEx}");
            AgentLog.Instance.Write($"[bridge] PostTurn failed, fallback to DisplayAlert: {postEx}");
            try
            {
                MainThread.BeginInvokeOnMainThread(async () =>
                {
                    var mainPage = Application.Current?.MainPage;
                    if (mainPage != null)
                    {
                        await mainPage.DisplayAlert("Error", message, "OK");
                    }
                });
            }
            catch (Exception alertEx)
            {
                // DisplayAlert 也失败——只剩日志
                Console.WriteLine($"[bridge] DisplayAlert fallback failed: {alertEx}");
                AgentLog.Instance.Write($"[bridge] DisplayAlert fallback failed: {alertEx}");
            }
        }
    }

    /// <summary>
    /// 释放桥接资源——fire-and-forget，不阻塞 UI 线程（spec ID9）。
    /// <para>
    /// <b>关键约束</b>：UI 线程不阻塞——不调 <see cref="Task.Wait"/> / <c>GetAwaiter().GetResult()</c>。
    /// 正常关闭路径（用户点关闭，游戏循环在 <c>ReadLineAsync</c> await）游戏循环毫秒级退出，
    /// <c>using (var scope = GlobalStatic.OpenScope(...))</c> 的 finally 跑 scope Dispose，I-11 退出存活覆盖。
    /// 异常路径（卡在同步 ERB）进程强杀，scope Dispose 不跑——I-11 不覆盖异常退出，可接受。
    /// </para>
    /// <para>
    /// <b>步骤</b>：
    /// <list type="number">
    ///   <item><see cref="CancellationTokenSource.Cancel"/>——通知 <c>RunLoopAsync</c> 退出
    ///     （<c>externalCt.IsCancellationRequested</c> 跳出循环）</item>
    ///   <item><see cref="MauiBridgeIO.Close"/>——关 Channel，<c>ReadLineAsync</c> 返回 null 让循环退出</item>
    ///   <item><see cref="Task.ContinueWith"/>——诊断日志，<see cref="TaskScheduler.Default"/> 在线程池跑</item>
    /// </list>
    /// </para>
    /// <para>
    /// 幂等：多次调用安全（<c>_disposed</c> 守卫）。
    /// issue 09 hot-swap reload 也调此方法 Dispose 旧 BridgeHost——主线程调用，不阻塞。
    /// </para>
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _jsBridge.InputReceived -= OnInputFromJs;

        // 1. 取消 CTS——RunLoopAsync 内 externalCt.IsCancellationRequested 跳出循环
        try
        {
            _cts.Cancel();
        }
        catch (Exception ex)
        {
            // CTS 已 Dispose / 其他异常——日志吞掉，不阻断 Close
            AgentLog.Instance.Write($"[bridge] Dispose CTS.Cancel failed: {ex.Message}");
        }

        // 2. 关闭 IO——Channel.Reader.ReadAsync 返回 null 让 ReadLineAsync 退出
        _bridgeIO.Close();

        // 3. fire-and-forget 诊断日志——不阻塞 UI 线程
        // spec ID9：不调 GetAwaiter().GetResult() 阻塞 UI。
        var task = _gameTask;
        if (task != null)
        {
            _ = task.ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    AgentLog.Instance.Write($"[bridge] game task faulted on dispose: {t.Exception}");
                    Console.WriteLine($"[bridge] game task faulted on dispose: {t.Exception}");
                }
                else if (t.IsCanceled)
                {
                    AgentLog.Instance.Write("[bridge] game task canceled on dispose");
                }
                else
                {
                    AgentLog.Instance.Write("[bridge] game task completed on dispose");
                }
            }, TaskScheduler.Default);
        }

        // CTS 不在 Dispose 中释放——_gameTask 后台观察者可能在 ContinueWith 内访问 _cts，
        // 让 GC 自然回收（BridgeHost 是 MainPage 级单例，不频繁分配，无泄漏风险）。
    }
}
