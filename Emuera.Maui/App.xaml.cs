namespace Emuera.Maui;

/// <summary>
/// MAUI Application 入口——issue 06 占位骨架。T07 完成 MainPage（WebView + BridgeHost）注入。
/// </summary>
public partial class App : Application
{
	public App()
	{
		InitializeComponent();
	}

	protected override Window CreateWindow(IActivationState? activationState)
	{
		return new Window(new MainPage());
	}
}
