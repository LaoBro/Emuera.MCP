using System;
using System.Threading.Tasks;
using Emuera.Maui.JsBridge;
using MinorShift.Emuera;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Terminal.Platform;

namespace Emuera.Maui;

/// <summary>
/// MAUI 主页面——issue 07 / 08 / 09 / spec ID6 + ID8。
/// <para>
/// 持有 <see cref="WebView"/> + <see cref="BridgeHost"/> + <see cref="IJsBridge"/>，编排：
/// <list type="number">
///   <item>构造时 DI 注入 <see cref="ConfigData"/> + <see cref="ITerminalSetup"/> 单例</item>
///   <item>new <see cref="BridgeHost"/>（构造不启动游戏循环——spec ID7 延迟启动）</item>
///   <item>new <see cref="IJsBridge"/>（<see cref="JsBridgeFactory.Create"/> 平台分流）</item>
///   <item>订阅 <c>MainWebView.HandlerChanged</c>——Handler 就绪后 <c>_jsBridge.Attach(webView)</c> + 设 URL</item>
///   <item>Vue 加载后 postMessage({type:'ready'}) → <see cref="BridgeHost.OnInputFromJs"/> 识别</item>
/// </list>
/// </para>
/// <para>
/// URL 平台分叉（spec ID6）：
/// <list type="bullet">
///   <item>Windows: <c>https://app.local/index.html</c>（unpackaged 模式下用 WebView2 虚拟主机映射，
///     <see cref="WindowsJsBridge.Attach"/> 内调 <c>SetVirtualHostNameToFolderMapping</c> 把
///     <c>app.local</c> 映射到输出目录 wwwroot/）</item>
///   <item>Android: <c>file:///android_asset/wwwroot/index.html</c>（Android WebView + APK assets/）</item>
/// </list>
/// </para>
/// <para>
/// <b>issue 09 hot-swap reload</b>：Vue 端 <c>pickGameFolder()</c> → C# <c>BridgeHost.HandlePickFolder</c>
/// → 原生 FolderPicker → <c>folderPicked</c> 推回 Vue → Vue <c>loadGameFromPath(path)</c> →
/// <c>BridgeHost.HandleLoadGame</c> 调 <see cref="OnReloadGame"/> 回调 →
/// 后台线程跑 <c>EmueraRuntimeInitializer.Initialize</c> → UI 线程 <see cref="RecreateHost"/>。
/// <see cref="_host"/> / <see cref="_configData"/> / <see cref="_terminalSetup"/> 为可变字段，
/// reload 时替换为新实例。
/// </para>
/// <remarks>
/// <see cref="BridgeHost"/> 在 <see cref="OnDisappearing"/> 或 <see cref="RecreateHost"/> 时
/// <see cref="BridgeHost.Dispose"/>——前者是页面销毁（Windows <c>Window.Closed</c> / Android <c>Activity.OnDestroy</c>），
/// 后者是 issue 09 hot-swap 旧 host 让位给新 host。
/// </remarks>
public partial class MainPage : ContentPage
{
    private readonly IJsBridge _jsBridge;
    private BridgeHost? _host;
    private ConfigData _configData;
    private ITerminalSetup _terminalSetup;
    private bool _urlSet;

    /// <summary>
    /// DI 注入构造——<see cref="MauiProgram"/> 注册的 <see cref="ConfigData"/> + <see cref="ITerminalSetup"/> 单例经 MAUI DI 容器注入。
    /// <para>
    /// 构造标记 <c>internal</c> 因参数类型 <see cref="ConfigData"/> / <see cref="ITerminalSetup"/>
    /// 在 <c>Emuera.Headless.Core</c> 内为 <c>internal</c>——C# 规则要求方法可访问性不得高于参数类型。
    /// <see cref="MauiProgram"/> 用 factory delegate 注册（同程序集，可访问 internal ctor）。
    /// </para>
    /// </summary>
    internal MainPage(ConfigData configData, ITerminalSetup terminalSetup)
    {
        InitializeComponent();

        _jsBridge = JsBridgeFactory.Create();
        _configData = configData;
        _terminalSetup = terminalSetup;
        RecreateHost();

        // HandlerChanged 是 MAUI WebView 平台原生视图就绪的最早可靠时机（spec ID8）。
        // 在此 Attach IJsBridge（订阅 WebMessageReceived / AddJavascriptInterface）+ 设 URL 加载 Vue。
        MainWebView.HandlerChanged += OnWebViewHandlerChanged;

        // 兜底日志：WebView 导航结果监听——每种结果都输出日志方便诊断白屏
        MainWebView.Navigated += OnWebViewNavigated;
    }

    private async void OnWebViewHandlerChanged(object? sender, EventArgs e)
    {
#if ANDROID
        Android.Util.Log.Info("EmueraMaui", $"OnWebViewHandlerChanged fired, _urlSet={_urlSet}, Handler={MainWebView.Handler?.GetType().Name}");
#endif
        // Attach 幂等（WindowsJsBridge / AndroidJsBridge 内部 _attached flag 防重复）。
        // Attach 内部会配置虚拟主机映射（Windows）——必须 await 完成后再设 URL，
        // 否则首帧导航到 https://app.local/ 时映射未就绪导致空白。
        await _jsBridge.Attach(MainWebView);

        // 设 URL——仅首次就绪时设一次，避免 HandlerChanged 多次触发重复加载。
        if (!_urlSet)
        {
            _urlSet = true;
            var url = ResolveWebViewUrl();
#if ANDROID
            Android.Util.Log.Info("EmueraMaui", $"Setting WebView URL: {url}");
#endif
            MainWebView.Source = new UrlWebViewSource { Url = url };
        }
    }

    /// <summary>
    /// WebView 导航完成回调——输出导航结果用于诊断白屏。
    /// </summary>
    private void OnWebViewNavigated(object? sender, WebNavigatedEventArgs e)
    {
        var source = e.Url ?? "(null)";
        var result = e.Result.ToString();
#if ANDROID
        Android.Util.Log.Info("EmueraMaui", $"WebView Navigated: result={result}, url={source}");
#else
        Console.WriteLine($"[maui] WebView Navigated: result={result}, url={source}");
#endif
        if (e.Result != WebNavigationResult.Success)
        {
#if ANDROID
            Android.Util.Log.Warn("EmueraMaui", $"WebView navigation FAILED: {result}, url={source}");
#endif
        }
    }

    /// <summary>
    /// 按平台返回 WebView 加载 URL（spec ID6）。
    /// <para>
    /// Windows unpackaged 模式下用 <c>https://app.local/index.html</c>——
    /// <see cref="WindowsJsBridge.Attach"/> 内 <c>SetVirtualHostNameToFolderMapping</c>
    /// 把 <c>app.local</c> 映射到输出目录 <c>wwwroot/</c>。
    /// </para>
    /// </summary>
    private static string ResolveWebViewUrl()
    {
#if WINDOWS
        return $"https://{WindowsJsBridge.VirtualHostName}/index.html";
#elif ANDROID
        return "file:///android_asset/wwwroot/index.html";
#else
        throw new System.PlatformNotSupportedException(
            "MAUI WebView URL 当前平台不支持（Phase 1 仅 Windows + Android）");
#endif
    }

    /// <summary>
    /// 用当前 <see cref="_configData"/> + <see cref="_terminalSetup"/> 重建 <see cref="BridgeHost"/>。
    /// <para>
    /// Dispose 旧 host（若存在）→ new 新 host（订阅 InputReceived + 传 <see cref="OnReloadGame"/> 回调）→ 不启动。
    /// 启动时机：首次构造时由 Vue ready 信号触发；reload 时由 <see cref="OnReloadGame"/> 直接调 <see cref="BridgeHost.Start"/>。
    /// </para>
    /// <para>
    /// 调用线程：UI 线程（构造时 / reload 后切回 UI 线程）。
    /// </para>
    /// </summary>
    private void RecreateHost()
    {
        _host?.Dispose();
        _host = new BridgeHost(Dispatcher, _configData, _terminalSetup, _jsBridge, OnReloadGame);
    }

    /// <summary>
    /// issue 09 hot-swap reload 回调——<see cref="BridgeHost.HandleLoadGame"/> 收到
    /// <c>{"type":"loadGame","path":...}</c> 后调此方法。
    /// <para>
    /// <b>步骤</b>：
    /// <list type="number">
    ///   <item>后台线程跑 <see cref="EmueraRuntimeInitializer.Initialize"/> + <see cref="GamePaths.Validate"/>——
    ///     避免阻塞 UI（加载 config / 语言文件耗时）</item>
    ///   <item>成功：UI 线程替换 <see cref="_configData"/> / <see cref="_terminalSetup"/> +
    ///     <see cref="RecreateHost"/> + <see cref="BridgeHost.Start"/>（Vue 已 ready，直接启动）</item>
    ///   <item>失败：用旧 host 推 error turn 给 Vue 让用户看到错误信息，旧 host 继续运行</item>
    /// </list>
    /// </para>
    /// <para>
    /// <b>线程</b>：BridgeHost.HandleLoadGame 在 OnInputFromJs 内调此方法（UI 线程）；
    /// 后台初始化用 <see cref="Task.Run"/>；UI 重建用 <see cref="IDispatcher.Dispatch"/>。
    /// </para>
    /// </summary>
    /// <param name="gamePath">用户选中的游戏目录绝对路径。</param>
    private void OnReloadGame(string gamePath)
    {
        Console.WriteLine($"[maui] OnReloadGame: {gamePath}");
        // 后台线程跑 IO 重型初始化——避免 UI 卡顿（加载 config / 语言文件）
        _ = Task.Run(() =>
        {
            ConfigData? newConfig = null;
            ITerminalSetup? newTerminal = null;
            Exception? initError = null;
            try
            {
                var paths = GamePaths.Resolve(gamePath);
                paths.Validate(); // 校验失败抛 GamePathValidationException
                (newConfig, newTerminal) = EmueraRuntimeInitializer.Initialize(paths);
                Console.WriteLine($"[maui] OnReloadGame init completed: ExeDir={paths.ExeDir}");
            }
            catch (Exception ex)
            {
                initError = ex;
                Console.WriteLine($"[maui] OnReloadGame init failed: {ex}");
            }

            // 回 UI 线程——BridgeHost ctor 订阅 InputReceived 事件 + Start 必须 UI 线程
            Dispatcher.Dispatch(() =>
            {
                if (initError != null || newConfig == null || newTerminal == null)
                {
                    // 初始化失败——用旧 host 推 error turn 给 Vue，旧 host 继续运行
                    Console.WriteLine($"[maui] OnReloadGame keeping old host, error: {initError?.Message}");
                    _host?.ShowError($"重新加载游戏失败: {initError?.Message}");
                    return;
                }

                _configData = newConfig;
                _terminalSetup = newTerminal;
                RecreateHost();
                // Vue 已 ready（首次 ready 信号早已收到），直接启动新游戏循环
                _host?.Start();
                Console.WriteLine("[maui] OnReloadGame: new BridgeHost started");
            });
        });
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _host?.Dispose();
    }
}
