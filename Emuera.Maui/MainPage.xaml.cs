using System;
using Emuera.Maui.JsBridge;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Terminal.Platform;

namespace Emuera.Maui;

/// <summary>
/// MAUI 主页面——issue 07 / spec ID6 + ID8。
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
///   <item>Windows: <c>ms-appx-web:///wwwroot/index.html</c>（WinUI WebView2 + MSIX 包内文件）</item>
///   <item>Android: <c>file:///android_asset/wwwroot/index.html</c>（Android WebView + APK assets/）</item>
/// </list>
/// </para>
/// <remarks>
/// <see cref="BridgeHost"/> 在 <see cref="OnDisappearing"/> 时 <see cref="BridgeHost.Dispose"/>——
/// Windows <c>Window.Closed</c> / Android <c>Activity.OnDestroy</c> 都触发 OnDisappearing。
/// </remarks>
public partial class MainPage : ContentPage
{
    private readonly BridgeHost _host;
    private readonly IJsBridge _jsBridge;

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
        _host = new BridgeHost(Dispatcher, configData, terminalSetup, _jsBridge);

        // HandlerChanged 是 MAUI WebView 平台原生视图就绪的最早可靠时机（spec ID8）。
        // 在此 Attach IJsBridge（订阅 WebMessageReceived / AddJavascriptInterface）+ 设 URL 加载 Vue。
        MainWebView.HandlerChanged += OnWebViewHandlerChanged;
    }

    private void OnWebViewHandlerChanged(object? sender, EventArgs e)
    {
        // Attach 幂等（WindowsJsBridge / AndroidJsBridge 内部 _attached flag 防重复）
        _jsBridge.Attach(MainWebView);

        // 设 URL——平台分叉（spec ID6）
        // 仅在 Handler 首次就绪时设一次，避免 HandlerChanged 多次触发重复加载
        if (MainWebView.Source is not UrlWebViewSource)
        {
            var url = ResolveWebViewUrl();
            MainWebView.Source = new UrlWebViewSource { Url = url };
        }
    }

    /// <summary>
    /// 按平台返回 WebView 加载 URL（spec ID6）。
    /// </summary>
    private static string ResolveWebViewUrl()
    {
#if WINDOWS
        return "ms-appx-web:///wwwroot/index.html";
#elif ANDROID
        return "file:///android_asset/wwwroot/index.html";
#else
        throw new System.PlatformNotSupportedException(
            "MAUI WebView URL 当前平台不支持（Phase 1 仅 Windows + Android）");
#endif
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _host.Dispose();
    }
}
