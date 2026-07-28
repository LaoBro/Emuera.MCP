using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MinorShift.Emuera;
using MinorShift.Emuera.GameView;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Terminal.Platform;
#if ANDROID
using Android.Webkit;
using Microsoft.Maui.Handlers;
#endif

namespace Emuera.Maui;

/// <summary>
/// MAUI 应用入口工厂——issue 07 / spec ID8。
/// <para>
/// 编排启动序列（与 <c>HeadlessEntry.Main</c> 共享 <see cref="EmueraRuntimeInitializer"/>）：
/// <list type="number">
///   <item><see cref="GameResourceExtractor.EnsureGameDirAsync"/>——解压 <c>test_game/</c> 到 <c>FileSystem.AppDataDirectory/emuera/</c>（marker 文件避免重复解压）</item>
///   <item><see cref="MinorShift.Emuera.GamePaths.Resolve"/>——绑定 <c>GamePaths.Current</c> 给共享源码读</item>
///   <item><see cref="EmueraRuntimeInitializer.Initialize"/>——共享 bootstrap（encoding/culture/ConfigData/Lang 等）</item>
///   <item>MAUI DI 注册 <see cref="ConfigData"/> / <see cref="ITerminalSetup"/> 单例——<see cref="MainPage"/> 重建时不重新初始化运行时</item>
/// </list>
/// </para>
/// <para>
/// <see cref="GamePaths.Validate"/> 不在此处调——MAUI 内置资源解压后路径必有效；
/// 若解压失败（IO 错误等），<see cref="GameResourceExtractor.EnsureGameDirAsync"/> 抛异常让 MAUI 弹崩溃对话框。
/// </para>
/// <remarks>
/// <see cref="CreateMauiApp"/> 是同步入口（MAUI <c>MauiWinUIApplication</c> 调用），
/// 但 <see cref="GameResourceExtractor.EnsureGameDirAsync"/> 是 async（MAUI <c>FileSystem.OpenAppPackageFileAsync</c> 异步）。
/// 用 <c>Task.Run(...).GetAwaiter().GetResult()</c> 同步等待——MAUI 启动期同步等 IO 可接受
/// （解压仅在首启动发生，二次启动 marker 文件存在直接返回）。
/// </remarks>
/// <para>
/// <b>调试 Console</b>：MAUI Windows app 是 WinUI 进程（OutputType=WinExe），默认无 console 窗口——
/// <c>Console.WriteLine</c> 输出丢失。DEBUG 构建下用 <see cref="AllocConsole"/> 创建 console 窗口，
/// 让 <c>BridgeHost</c> / <c>WindowsJsBridge</c> 的 <c>Console.WriteLine</c> 诊断日志可见。
/// Release 构建不创建 console（避免终端用户看到调试输出）。
/// </para>
public static class MauiProgram
{
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllocConsole();

    public static MauiApp CreateMauiApp()
    {
        // DEBUG 构建下创建 console 窗口——WinUI app 默认无 console，Console.WriteLine 输出会丢失。
        // dotnet run -c Debug 时此 console 是诊断日志唯一可见通道（VS 调试器 Output 窗口仅 VS 内可见）。
#if DEBUG
        AllocConsole();
        Console.WriteLine("[maui] MauiProgram.CreateMauiApp starting (DEBUG console allocated)");
#endif

        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();

#if DEBUG
        builder.Logging.AddDebug();
#endif

        try
        {
            // === spec ID8 启动编排 ===
            // 同步等待 EnsureGameDirAsync——见类 remarks。
            Console.WriteLine("[maui] EnsureGameDirAsync starting");
#if ANDROID
            Android.Util.Log.Info("EmueraMaui", "EnsureGameDirAsync starting");
#endif
            var gameDir = Task.Run(GameResourceExtractor.EnsureGameDirAsync).GetAwaiter().GetResult();
            Console.WriteLine($"[maui] EnsureGameDirAsync completed: gameDir={gameDir}");
#if ANDROID
            Android.Util.Log.Info("EmueraMaui", $"EnsureGameDirAsync completed: gameDir={gameDir}");
#endif
            var paths = GamePaths.Resolve(gameDir, new FileSystemGameDirAccessor());
            Console.WriteLine($"[maui] GamePaths.Resolve completed: ExeDir={paths.ExeDir}");
#if ANDROID
            Android.Util.Log.Info("EmueraMaui", $"GamePaths.Resolve completed: ExeDir={paths.ExeDir}");
#endif
            var (configData, terminalSetup) = EmueraRuntimeInitializer.Initialize(paths, new FileSystemGameDirAccessor());
            Console.WriteLine("[maui] EmueraRuntimeInitializer.Initialize completed");
#if ANDROID
            Android.Util.Log.Info("EmueraMaui", "EmueraRuntimeInitializer.Initialize completed");
#endif

            // DI 注册单例——MainPage 经 DI 注入，重建时不重新初始化运行时
            builder.Services.AddSingleton(configData);
            builder.Services.AddSingleton(terminalSetup);
            // MainPage ctor 是 internal（参数类型 ConfigData/ITerminalSetup 在 Core 内 internal）——
            // 用 factory delegate 显式构造，绕过 ActivatorUtilities 仅扫 public ctor 的限制。
            builder.Services.AddTransient<MainPage>(sp =>
                new MainPage(
                    sp.GetRequiredService<ConfigData>(),
                    sp.GetRequiredService<ITerminalSetup>()));

            Console.WriteLine("[maui] MauiProgram.CreateMauiApp completed, returning built app");
#if ANDROID
            Android.Util.Log.Info("EmueraMaui", "CreateMauiApp completed, returning built app");
#endif

#if ANDROID
            // Android WebView 配置：JavaScript + DOM Storage + 文件访问 + console 日志捕获
            Android.Util.Log.Info("EmueraMaui", "ConfigureAndroidWebView called before Build");
            ConfigureAndroidWebView();
            Android.Util.Log.Info("EmueraMaui", "ConfigureAndroidWebView completed");
#endif

            return builder.Build();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[maui] FATAL: CreateMauiApp failed: {ex}");
#if ANDROID
            Android.Util.Log.Info("EmueraMaui", $"FATAL: CreateMauiApp failed: {ex}");
#endif
            throw;
        }
    }

#if ANDROID
    /// <summary>
    /// Android WebView handler 自定义配置——启用 JavaScript / DOM Storage / 文件访问，
    /// 并挂载 LoggingWebChromeClient 将 console.log/warn/error 输出到 adb logcat。
    /// </summary>
    private static void ConfigureAndroidWebView()
    {
        // 启用 WebView 远程调试——chrome://inspect 可连接
        Android.Webkit.WebView.SetWebContentsDebuggingEnabled(true);

        WebViewHandler.Mapper.AppendToMapping("emueraWebView", (handler, view) =>
        {
            if (handler.PlatformView is Android.Webkit.WebView wv)
            {
                wv.Settings.JavaScriptEnabled = true;
                wv.Settings.DomStorageEnabled = true;
                wv.Settings.AllowFileAccess = true;
                // file:// 协议下允许加载外部 JS/CSS——Vite 构建产物使用 ES Module + 独立 CSS 文件，
                // 若不加此行会因 CORS 策略被拦截导致白屏
                wv.Settings.AllowFileAccessFromFileURLs = true;
                wv.Settings.AllowUniversalAccessFromFileURLs = true;
                wv.SetWebChromeClient(new LoggingWebChromeClient());
            }
        });
    }

    /// <summary>
    /// 自定义 WebChromeClient——将 WebView 控制台日志转发到 System.Diagnostics.Debug，
    /// 通过 adb logcat 可见（过滤 tag="EmueraWV"）。
    /// </summary>
    private sealed class LoggingWebChromeClient : WebChromeClient
    {
        public override bool OnConsoleMessage(ConsoleMessage? consoleMessage)
        {
            if (consoleMessage != null)
            {
                var msg = $"[WV] {consoleMessage.Message()} (line {consoleMessage.LineNumber()}, {consoleMessage.SourceId()})";
                System.Diagnostics.Debug.WriteLine(msg, "EmueraWV");
            }
            return base.OnConsoleMessage(consoleMessage);
        }
    }
#endif
}
