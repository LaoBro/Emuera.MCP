using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Emuera.Maui.JsBridge;
using Microsoft.Maui.Storage;
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
    /// <summary>Preferences key——主目录路径持久化（spec ID5）。</summary>
    private const string MainGameDirKey = "emuera.mainGameDir";

    private readonly IDispatcher _dispatcher;
    private readonly ConfigData _configData;
    private readonly ITerminalSetup _terminalSetup;
    private readonly IJsBridge _jsBridge;
    private readonly MauiBridgeIO _bridgeIO;
    private readonly Action<string>? _onReloadGame;
    private readonly Action? _onGameExited;
    private readonly CancellationTokenSource _cts = new();
    private Task? _gameTask;
    private bool _disposed;
    private bool _started;
    private bool _readyReceived;

    /// <summary>
    /// 当前主目录路径——game-library spec ID4 / ID5。
    /// <para>
    /// 构造时从 <see cref="Preferences"/> 读取（key=<see cref="MainGameDirKey"/>）；无值时按平台回退：
    /// Windows <c>Documents/emuera</c>，Android <c>null</c>（待权限引导后设置）。
    /// </para>
    /// <para>
    /// <see cref="HandleScanGames"/> 收到非空 rootDir 时更新此字段 + 写 Preferences，
    /// 保证 Vue 端与 C# 端主目录一致。
    /// </para>
    /// </summary>
    private string? _mainGameDir;

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
    /// <param name="onGameExited">game-library spec ID10 退出游戏回调——BridgeHost 处理 <c>exitGame</c>
    /// 后调此回调让 MainPage 重建 BridgeHost（新 host 不启动游戏循环，等 Vue 触发 scanGames）。</param>
    internal BridgeHost(
        IDispatcher dispatcher,
        ConfigData configData,
        ITerminalSetup terminalSetup,
        IJsBridge jsBridge,
        Action<string>? onReloadGame = null,
        Action? onGameExited = null)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _configData = configData ?? throw new ArgumentNullException(nameof(configData));
        _terminalSetup = terminalSetup ?? throw new ArgumentNullException(nameof(terminalSetup));
        _jsBridge = jsBridge ?? throw new ArgumentNullException(nameof(jsBridge));
        _onReloadGame = onReloadGame;
        _onGameExited = onGameExited;

        // game-library spec ID5：从 Preferences 读 mainGameDir——无值时按平台回退
        // Windows: Documents/emuera；Android: null（待权限引导后设置）
        _mainGameDir = LoadMainGameDir();

        // MauiBridgeIO 的 _onTurn 回调在游戏循环线程执行——Dispatcher.Dispatch 切 UI 线程投递给 WebView。
        _bridgeIO = new MauiBridgeIO(OnTurnFromGame);
        _jsBridge.InputReceived += OnInputFromJs;
#if ANDROID
        // ADR-0019：兜底——InputReceived 无订阅者时仍能收到 JS 消息
        AndroidJsBridge.FallbackInputHandler = OnInputFromJs;
        // ADR-0019：BridgeClient URL 拦截——可靠 JS→C# 备份通道
        AndroidJsBridge.BridgeUrlReceived += OnBridgeUrl;
#endif
    }

    /// <summary>
    /// 当前主目录路径——game-library spec ID4。
    /// Vue 端首次启动时通过 <c>mainDir</c> 消息接收此值；用户更改主目录后通过 <c>scanGames</c> 更新。
    /// </summary>
    public string? MainGameDir => _mainGameDir;

    /// <summary>
    /// game-library spec ID10：游戏循环是否正在运行——MainPage 据此决定 Android 物理返回键行为。
    /// <c>true</c> 表示游戏循环已启动且未 Dispose。
    /// </summary>
    public bool IsGameRunning => _started && !_disposed;

    /// <summary>
    /// 从 Preferences 加载 mainGameDir——无值时按平台回退（spec ID5）。
    /// <para>
    /// 平台默认：
    /// <list type="bullet">
    ///   <item>Windows：<c>Documents/emuera</c></item>
    ///   <item>Android：<c>null</c>（首启动待权限引导后由 <c>PermissionGuidePage</c> 写入默认值）</item>
    /// </list>
    /// </para>
    /// </summary>
    private static string? LoadMainGameDir()
    {
        try
        {
            var stored = Preferences.Get(MainGameDirKey, null);
            if (!string.IsNullOrEmpty(stored))
                return stored;
        }
        catch
        {
            // Preferences 不可用（极少见）——回退到平台默认
        }
#if WINDOWS
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "emuera");
#else
        return null;
#endif
    }

    /// <summary>
    /// 持久化 mainGameDir 到 Preferences——spec ID5。
    /// 失败时静默（Preferences 不可用），_mainGameDir 字段仍更新。
    /// </summary>
    private void SaveMainGameDir(string dir)
    {
        try
        {
            Preferences.Set(MainGameDirKey, dir);
        }
        catch
        {
            // 静默——字段已更新，仅持久化失败
        }
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
    ///   <item>game-library spec ID3：<c>{"type":"scanGames","rootDir":...}</c> /
    ///       <c>{"type":"listDirectories","dirPath":...}</c> / <c>{"type":"exitGame"}</c>——
    ///       分别调 <see cref="HandleScanGames"/> / <see cref="HandleListDirectories"/> / <see cref="HandleExitGame"/></item>
    ///   <item>其他（如 <c>{"type":"input","value":"..."}</c>）——原样入 <see cref="MauiBridgeIO.EnqueueInput"/>，
    ///       由 <c>AgentJsonlProtocol.RunLoopAsync</c> 反序列化消费</item>
    /// </list>
    /// </para>
    /// <para>
    /// 非 JSON 消息静默吞掉（仅写日志）——Vue 端 <c>postInput</c> 总发合法 JSON，
    /// 到此分支说明桥接层异常，但不阻塞游戏循环。
    /// </para>
    /// </summary>
    /// <summary>ADR-0019：BridgeClient 备用通道。</summary>
    private void OnBridgeUrl(string data)
    {
        if (_disposed) return;
        // bridge://post?msg=... → data 是已解码的 JSON 字符串
        // bridge://pickSafDirectory → data 是简单 action 名
        if (data.StartsWith('{'))
            OnInputFromJs(data);
        else
            OnInputFromJs($"{{\"type\":\"{data}\"}}");
    }

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
                if (type == "pickSafDirectory")
                {
                    HandlePickSafDirectory();
                    return;
                }
                if (type == "loadGame")
                {
                    HandleLoadGame(doc.RootElement);
                    return;
                }
                // game-library spec ID3：游戏列表扫描 / 目录列举 / 退出游戏——
                // 这些消息可在游戏循环未启动时处理（Vue 启动后立即 scanGames 填充列表）
                if (type == "scanGames")
                {
                    HandleScanGames(doc.RootElement);
                    return;
                }
                if (type == "listDirectories")
                {
                    HandleListDirectories(doc.RootElement);
                    return;
                }
                if (type == "exitGame")
                {
                    HandleExitGame();
                    return;
                }
                // game-library spec ID6：Android 存储权限检查与请求
                if (type == "checkPermission")
                {
                    HandleCheckPermission();
                    return;
                }
                if (type == "requestPermission")
                {
                    HandleRequestPermission();
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
    /// ADR-0019：处理 Vue 端 pickSafDirectory 消息——Android SAF 原生目录选择器。
    /// 通过 <see cref="IGameDirAccessor.PickDirectoryAsync"/> 弹出系统原生目录选择器。
    /// </summary>
    private async void HandlePickSafDirectory()
    {
        Console.WriteLine("[bridge] HandlePickSafDirectory");
        AgentLog.Instance.Write("[bridge] HandlePickSafDirectory");
        try
        {
#if ANDROID
            var dirAccessor = (IGameDirAccessor?)SafGameDirAccessor.Instance;
#else
            IGameDirAccessor? dirAccessor = null;
#endif
            if (dirAccessor == null)
            {
                Console.WriteLine("[bridge] HandlePickSafDirectory: no DirAccessor");
#if ANDROID
                var errJson = JsonSerializer.Serialize(new { type = "safDirectoryPicked", error = "DirAccessor is null — SafGameDirAccessor not initialized" });
#else
                var errJson = JsonSerializer.Serialize(new { type = "safDirectoryPicked", error = "SAF not supported on this platform" });
#endif
                _dispatcher.Dispatch(() => _jsBridge.PostMessage(errJson));
                return;
            }

            var result = await dirAccessor.PickDirectoryAsync();
            if (result == null)
            {
                var cancelJson = JsonSerializer.Serialize(new { type = "safDirectoryPicked", cancelled = true });
                _dispatcher.Dispatch(() => _jsBridge.PostMessage(cancelJson));
                return;
            }

            Console.WriteLine($"[bridge] HandlePickSafDirectory: picked={result}");
            // 更新主目录 + 持久化
            _mainGameDir = result;
            SaveMainGameDir(result);

            // 方案 B / B1：写权限 + 探针（logcat 验收；缺写时提示重选）
            var hasWrite = dirAccessor.HasWriteAccess();
            var writeProbeOk = false;
            var writeProbeDetail = "";
#if ANDROID
            if (dirAccessor is SafGameDirAccessor saf)
            {
                writeProbeOk = saf.TryWriteProbe(out writeProbeDetail);
            }
#endif
            Console.WriteLine(
                $"[bridge] HandlePickSafDirectory: hasWrite={hasWrite}, writeProbeOk={writeProbeOk}, detail={writeProbeDetail}");
#if ANDROID
            Android.Util.Log.Info("EmueraMaui",
                $"safDirectoryPicked hasWrite={hasWrite} writeProbeOk={writeProbeOk} detail={writeProbeDetail}");
#endif
            if (!hasWrite || !writeProbeOk)
            {
                var needRepick = JsonSerializer.Serialize(new
                {
                    type = "safDirectoryPicked",
                    path = result,
                    hasWrite,
                    writeProbeOk,
                    writeProbeDetail,
                    error = hasWrite
                        ? $"Write probe failed: {writeProbeDetail}"
                        : "No write permission on selected folder. Please re-select the game directory and allow access.",
                    needRepickForWrite = true
                });
                _dispatcher.Dispatch(() => _jsBridge.PostMessage(needRepick));
                // 仍继续扫描——读权限可能足够浏览；存档会再失败并提示
            }

            // 直接扫描并推送 gamesScanned——避免 JS→C# 走不可靠的 emueraBridge
            ScanAndPushGames(result);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[bridge] HandlePickSafDirectory failed: {ex}");
            var errJson = JsonSerializer.Serialize(new { type = "safDirectoryPicked", error = ex.Message });
            _dispatcher.Dispatch(() => _jsBridge.PostMessage(errJson));
        }
    }

    /// <summary>
    /// ADR-0019：直接扫描 rootDir 并推送 gamesScanned 到 Vue。
    /// 绕过 JS→C# scanGames 消息链（emueraBridge 不可靠）。
    /// </summary>
    private void ScanAndPushGames(string? rootDir)
    {
        var dirAccessor = ResolveDirAccessor(rootDir);
        bool rootDirExists = GameScanner.RootDirectoryExists(rootDir, dirAccessor);
        var games = GameScanner.Scan(rootDir, dirAccessor);
        var gamesPayload = games.Select(g => new { name = g.Name, fullPath = g.FullPath }).ToList();
        var msgJson = JsonSerializer.Serialize(new
        {
            type = "gamesScanned",
            games = gamesPayload,
            rootDir,
            rootDirExists,
        });
        _dispatcher.Dispatch(() => _jsBridge.PostMessage(msgJson));
        Console.WriteLine($"[bridge] ScanAndPushGames: {gamesPayload.Count} games, rootDir={rootDir}");
    }

    /// <summary>
    /// 按主目录路径选择正确的文件访问器。
    /// Android SAF 的 <c>content://</c> URI 必须使用持久化的 SAF accessor；
    /// 传统路径则使用当前游戏 accessor（或本地文件系统 fallback）。
    /// </summary>
    private static IGameDirAccessor ResolveDirAccessor(string? rootDir)
    {
        if (rootDir?.StartsWith("content://", StringComparison.Ordinal) == true)
        {
#if ANDROID
            if (SafGameDirAccessor.Instance is { } safAccessor)
                return safAccessor;
#endif
        }

        return GamePaths.Current?.DirAccessor ?? new FileSystemGameDirAccessor();
    }

    /// <summary>
    /// 处理 Vue 端 scanGames 消息（game-library spec ID3 / ID4）——
    /// 扫描 rootDir 下的游戏列表并回复 <c>gamesScanned</c>。
    /// <para>
    /// 流程：
    /// <list type="number">
    ///   <item>读 <c>msg.rootDir</c>——若未提供则用 <see cref="_mainGameDir"/></item>
    ///   <item>若 rootDir 非空且与 <see cref="_mainGameDir"/> 不同——更新 <see cref="_mainGameDir"/> + 写 Preferences</item>
    ///   <item>调 <see cref="GameScanner.Scan"/> 扫描</item>
    ///   <item>回复 <c>{"type":"gamesScanned","games":[{name,fullPath}],"rootDir":...}</c></item>
    /// </list>
    /// </para>
    /// <para>
    /// <b>rootDir 为空时的处理</b>：若 <see cref="_mainGameDir"/> 也为 null（Android 首启动未授权），
    /// 回复空 games + null rootDir——Vue 端据此显示「请选择主目录」UI。
    /// </para>
    /// </summary>
    private void HandleScanGames(JsonElement root)
    {
        string? rootDir = null;
        if (root.TryGetProperty("rootDir", out var rootEl) && rootEl.ValueKind == JsonValueKind.String)
        {
            rootDir = rootEl.GetString();
        }
        if (string.IsNullOrEmpty(rootDir))
            rootDir = _mainGameDir;
        else if (rootDir != _mainGameDir)
        {
            // 用户更改主目录——更新字段 + 持久化
            _mainGameDir = rootDir;
            SaveMainGameDir(rootDir!);
            Console.WriteLine($"[bridge] mainGameDir updated to: {rootDir}");
        }

        Console.WriteLine($"[bridge] HandleScanGames: rootDir={rootDir}");
        // game-library spec ID13：检查主目录是否存在——Vue 端区分「目录不存在」vs「无游戏」
        var dirAccessor = ResolveDirAccessor(rootDir);
        bool rootDirExists = GameScanner.RootDirectoryExists(rootDir, dirAccessor);
        var games = GameScanner.Scan(rootDir, dirAccessor);
        var gamesPayload = games.Select(g => new { name = g.Name, fullPath = g.FullPath }).ToList();
        var msgJson = JsonSerializer.Serialize(new
        {
            type = "gamesScanned",
            games = gamesPayload,
            rootDir,
            rootDirExists,
        });
        _dispatcher.Dispatch(() => _jsBridge.PostMessage(msgJson));
    }

    /// <summary>
    /// 处理 Vue 端 listDirectories 消息（game-library spec ID3 / ID8）——
    /// 列举 dirPath 下的子目录并回复 <c>directoriesListed</c>，用于 Android 目录浏览器弹窗。
    /// <para>
    /// 回复格式：<c>{"type":"directoriesListed","currentPath":...,"parentPath":...|"null","subDirectories":[...]}</c>。
    /// dirPath 缺失时使用 <see cref="_mainGameDir"/>（若也为 null 则回复空结果）。
    /// </para>
    /// </summary>
    private void HandleListDirectories(JsonElement root)
    {
        string? dirPath = null;
        if (root.TryGetProperty("dirPath", out var dirEl) && dirEl.ValueKind == JsonValueKind.String)
        {
            dirPath = dirEl.GetString();
        }
        if (string.IsNullOrEmpty(dirPath))
            dirPath = _mainGameDir;

        Console.WriteLine($"[bridge] HandleListDirectories: dirPath={dirPath}");
        var result = DirectoryLister.ListDirectories(dirPath, ResolveDirAccessor(dirPath));
        var msgJson = JsonSerializer.Serialize(new
        {
            type = "directoriesListed",
            currentPath = result.CurrentPath,
            parentPath = result.ParentPath,
            subDirectories = result.SubDirectories,
        });
        _dispatcher.Dispatch(() => _jsBridge.PostMessage(msgJson));
    }

    /// <summary>
    /// 处理 Vue 端 exitGame 消息（game-library spec ID3 / ID10）——
    /// 退出当前游戏并回复 <c>gameExited</c>，让 Vue 回到列表态。
    /// <para>
    /// 流程：
    /// <list type="number">
    ///   <item>回复 <c>{"type":"gameExited"}</c> 给 Vue——必须先回复再 Dispose，
    ///       Dispose 后 OnInputFromJs 守卫会丢消息</item>
    ///   <item>调 <see cref="Dispose"/>——取消游戏循环 + 关 MauiBridgeIO</item>
    ///   <item>调 <c>_onGameExited?.Invoke()</c>——让 MainPage 重建 BridgeHost
    ///       （新 host 不启动游戏循环，等 Vue 触发 scanGames）</item>
    /// </list>
    /// </para>
    /// <para>
    /// <b>时序保证</b>：PostMessage 用 <see cref="IDispatcher.Dispatch"/> 异步派发到 UI 线程，
    /// 此方法返回后 UI 线程会依次执行：投递 gameExited → 重建 host。
    /// Vue 端收到 gameExited 后投递 scanGames，此时新 host 已就绪可接收。
    /// </para>
    /// </summary>
    private void HandleExitGame()
    {
        Console.WriteLine("[bridge] HandleExitGame: disposing game loop and notifying Vue");
        AgentLog.Instance.Write("[bridge] HandleExitGame: disposing game loop and notifying Vue");

        // 1. 先回复 gameExited——Dispatch 异步派发到 UI 线程队列
        var msgJson = JsonSerializer.Serialize(new { type = "gameExited" });
        _dispatcher.Dispatch(() => _jsBridge.PostMessage(msgJson));

        // 2. 调回调让 MainPage 重建 BridgeHost——RecreateHost 内会 Dispose 当前 host
        //    Dispose 后 OnInputFromJs 守卫 _disposed=true，新 host 接管后续消息
        try
        {
            _onGameExited?.Invoke();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[bridge] HandleExitGame: _onGameExited threw: {ex}");
            AgentLog.Instance.Write($"[bridge] HandleExitGame: _onGameExited threw: {ex}");
        }

        // 3. 若未提供 _onGameExited 回调（旧调用方）——直接 Dispose 让游戏循环停止
        if (_onGameExited is null)
        {
            Dispose();
        }
    }

    /// <summary>
    /// game-library spec ID6：检查 Android MANAGE_EXTERNAL_STORAGE 权限状态——回复 Vue。
    /// <para>
    /// Android 平台检查 <see cref="Android.OS.Environment.IsExternalStorageManager"/>；
    /// 非 Android 平台（Windows）自动视为已授权（<c>granted: true</c>）。
    /// 回复消息：<c>{"type":"permissionStatus","granted":true/false}</c>
    /// </para>
    /// </summary>
    private void HandleCheckPermission()
    {
        bool granted;
#if ANDROID
        // ADR-0019：SAF 替代了 MANAGE_EXTERNAL_STORAGE，不需要存储权限。
        // 始终返 granted=true，让 Vue 跳过 PermissionGuide 直接进入游戏列表。
        granted = true;
#else
        // Windows MAUI 不检查 Android 权限——默认视为已授权
        granted = true;
#endif
        var msg = JsonSerializer.Serialize(new { type = "permissionStatus", granted });
        _dispatcher.Dispatch(() => _jsBridge.PostMessage(msg));
        Console.WriteLine($"[bridge] HandleCheckPermission: granted={granted}");
        AgentLog.Instance.Write($"[bridge] HandleCheckPermission: granted={granted}");
    }

    /// <summary>
    /// game-library spec ID6：引导用户授权 MANAGE_EXTERNAL_STORAGE——
    /// 启动 Android 系统设置页。
    /// <para>
    /// Intent: <c>Settings.ActionManageAppAllFilesPermission</c> +
    /// <c>Intent.SetData(Android.Net.Uri.FromParts("package", PackageName, null))</c>
    /// </para>
    /// <para>
    /// API < 30 降级：打开应用详情设置页（<c>Settings.ActionApplicationDetailsSettings</c>），
    /// 用户可在此授予 READ_EXTERNAL_STORAGE。
    /// </para>
    /// <para>
    /// 不回复消息——用户跳转设置页后，返回 app 时通过 <c>MainActivity.OnResume</c> 或
    /// Vue 端「已授权，重新扫描」按钮重新投递 <c>checkPermission</c>。
    /// 非 Android 平台 no-op（Windows 无此权限概念）。
    /// </para>
    /// </summary>
    private void HandleRequestPermission()
    {
        // ADR-0019：SAF 替代了 MANAGE_EXTERNAL_STORAGE，不需要跳转系统设置。
        // 此方法保留为空操作——Vue 因 permissionStatus 永远 granted 已不会调用。
        Console.WriteLine("[bridge] HandleRequestPermission: no-op (SAF replaces MANAGE_EXTERNAL_STORAGE)");
        AgentLog.Instance.Write("[bridge] HandleRequestPermission: no-op (SAF replaces MANAGE_EXTERNAL_STORAGE)");
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
    /// <summary>
    /// 推送布局元数据给 Vue——让前端在首帧 turn 到达前就能用正确的游戏设置渲染终端区域。
    /// <para>
    /// 与 <c>KestrelGameServer.HandleGetStateAsync</c> 一致的计算逻辑：
    /// 从 <see cref="ConfigData"/> 读取 WindowX/FontSize/LineHeight/FontName，
    /// 按无头模式 <c>DrawingParam_ShapePositionShift = Max(2, FontSize/6)</c> 算 <c>gameColumns</c>。
    /// </para>
    /// <para>
    /// 消息格式：<c>{"type":"layout","state":"Loading","gameDir":"D:/game","windowWidth":760,"fontSize":18,"lineHeight":19,"gameColumns":84,"fontName":"ＭＳ ゴシック"}</c>
    /// </para>
    /// </summary>
    private void PushLayoutMessage()
    {
        var ww = _configData.GetConfigValue<int>(ConfigCode.WindowX);
        var fs = _configData.GetConfigValue<int>(ConfigCode.FontSize);
        var lh = _configData.GetConfigValue<int>(ConfigCode.LineHeight);
        var fn = _configData.GetConfigValue<string>(ConfigCode.FontName) ?? "";

        int charWidth = Math.Max(fs / 2, 1);
        int marginOffset = Math.Max(2, fs / 6);
        int gameColumns = (ww - marginOffset) / charWidth;

        var msg = JsonSerializer.Serialize(new
        {
            type = "layout",
            state = "Loading",
            gameDir = GamePaths.Current?.ExeDir,
            windowWidth = ww,
            fontSize = fs,
            lineHeight = lh,
            gameColumns,
            fontName = fn,
        });
        _dispatcher.Dispatch(() => _jsBridge.PostMessage(msg));
    }

    /// <summary>
    /// 推送 emuera.config 值给 Vue——让设置页显示当前配置。
    /// <para>
    /// 消息格式：<c>{"type":"config","maxLog":5000}</c>
    /// </para>
    /// </summary>
    private void PushConfigMessage()
    {
        var maxLog = _configData.GetConfigValue<int>(ConfigCode.MaxLog);
        var msg = JsonSerializer.Serialize(new
        {
            type = "config",
            maxLog,
        });
        _dispatcher.Dispatch(() => _jsBridge.PostMessage(msg));
    }

    internal void Start()
    {
        if (_started)
            return;
        _started = true;
        _readyReceived = true; // 标记 ready 已收到——避免后续 ready 信号重复触发 Start
        // 在游戏循环启动前推送布局元数据——Vue 在首帧 turn 到达前拿到字体/列宽/字号/行距
        PushLayoutMessage();
        PushConfigMessage();
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
                    Console.WriteLine("[bridge] GameLoopAsync starting RunLoopAsync");
					await ((AgentJsonlProtocol)p).RunLoopAsync(enableTimeout: true, _cts.Token);
					Console.WriteLine("[bridge] GameLoopAsync RunLoopAsync exited");
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
        catch (OperationCanceledException) when (_disposed)
        {
            // issue 09 hot-swap reload / 页面销毁：Dispose → CTS.Cancel → ReadLineAsync 抛 OperationCanceledException。
            // 这是预期行为，完全静默——不打 fatal 日志（避免干扰 debug），不推 error turn（Vue 已由新 host 接管）。
            // 用 when(_disposed) 子句确保只有 dispose 触发的取消走此路径，其他意外取消仍走下方 fatal 分支。
            Console.WriteLine("[bridge] game loop cancelled via Dispose (reload/page close)");
            AgentLog.Instance.Write("[bridge] game loop cancelled via Dispose (reload/page close)");
        }
        catch (Exception ex)
        {
            // 游戏循环整体崩溃（Initialize 失败 / OpenScope 失败 / protocol 未捕获异常等）
            Console.WriteLine($"[bridge] game loop fatal: {ex}");
            AgentLog.Instance.Write($"[bridge] game loop fatal: {ex}");
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
#if ANDROID
        Android.Util.Log.Error("EmueraMaui", $"FATAL: {ex}");
#endif
        System.Diagnostics.Debug.WriteLine($"[FATAL] {ex}");
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
                    var mainPage = Application.Current?.Windows[0].Page;
                    if (mainPage != null)
                    {
                        await mainPage.DisplayAlertAsync("Error", message, "OK");
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
