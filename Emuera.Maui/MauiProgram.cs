using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MinorShift.Emuera;
using MinorShift.Emuera.GameView;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Terminal.Platform;

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
public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();

#if DEBUG
        builder.Logging.AddDebug();
#endif

        // === spec ID8 启动编排 ===
        // 同步等待 EnsureGameDirAsync——见类 remarks。
        var gameDir = Task.Run(GameResourceExtractor.EnsureGameDirAsync).GetAwaiter().GetResult();
        var paths = GamePaths.Resolve(gameDir);
        var (configData, terminalSetup) = EmueraRuntimeInitializer.Initialize(paths);

        // DI 注册单例——MainPage 经 DI 注入，重建时不重新初始化运行时
        builder.Services.AddSingleton(configData);
        builder.Services.AddSingleton(terminalSetup);
        // MainPage ctor 是 internal（参数类型 ConfigData/ITerminalSetup 在 Core 内 internal）——
        // 用 factory delegate 显式构造，绕过 ActivatorUtilities 仅扫 public ctor 的限制。
        builder.Services.AddTransient<MainPage>(sp =>
            new MainPage(
                sp.GetRequiredService<ConfigData>(),
                sp.GetRequiredService<ITerminalSetup>()));

        return builder.Build();
    }
}
