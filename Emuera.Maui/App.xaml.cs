using Microsoft.Maui.Controls;

namespace Emuera.Maui;

/// <summary>
/// MAUI Application 入口——issue 07。
/// <para>
/// <see cref="CreateWindow"/> 经 MAUI DI 解析 <see cref="MainPage"/>——
/// <see cref="MauiProgram"/> 注册的 <see cref="MinorShift.Emuera.Runtime.Config.ConfigData"/> +
/// <see cref="MinorShift.Emuera.Terminal.Platform.ITerminalSetup"/> 单例由 DI 容器注入 <see cref="MainPage"/> 构造函数。
/// </para>
/// </summary>
public partial class App : Application
{
    public App()
    {
        InitializeComponent();
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        // MainPage 经 DI 注册（MauiProgram.AddTransient<MainPage>()），DI 容器自动注入 ConfigData / ITerminalSetup 单例。
        // activationState.Context.Services 在 MAUI 启动期由 MauiApplication 创建，MainPage 所需依赖已就位。
        var services = activationState?.Context?.Services
            ?? Handler?.MauiContext?.Services;
        if (services != null)
        {
            var mainPage = services.GetRequiredService<MainPage>();
            return new Window(mainPage);
        }
        // Fallback：理论上不应到达——MAUI 启动期 activationState 必非 null。
        // 若到达则说明 MAUI DI 未正确初始化，直接抛异常让开发者看见问题。
        throw new InvalidOperationException("MAUI DI services not available for MainPage construction");
    }
}

