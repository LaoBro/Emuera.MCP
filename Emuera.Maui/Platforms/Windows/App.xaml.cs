using Microsoft.UI.Xaml;

namespace Emuera.Maui.WinUI;

/// <summary>
/// WinUI 应用入口——MAUI 自动调用 <see cref="CreateMauiApp"/> 启动 <see cref="MauiProgram"/>。
/// </summary>
public partial class App : MauiWinUIApplication
{
	public App()
	{
		this.InitializeComponent();
	}

	protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
}
