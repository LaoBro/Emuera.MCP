using Microsoft.Extensions.Logging;

namespace Emuera.Maui;

/// <summary>
/// MAUI 应用入口工厂——issue 06 仅占位骨架。
/// T07 完整实现：GameResourceExtractor → GamePaths.Resolve → EmueraRuntimeInitializer.Initialize → DI 注册 ConfigData / ITerminalSetup 单例。
/// </summary>
public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		var builder = MauiApp.CreateBuilder();
		builder.UseMauiApp<App>();

#if DEBUG
		builder.Logging.AddDebug();
#endif

		return builder.Build();
	}
}
