using Microsoft.Maui.Storage;

namespace Emuera.AndroidNativeAot;

public partial class MainPage : ContentPage
{
		private const string ProbeHtml = """
				<!doctype html>
				<html lang="en">
				<head>
					<meta name="viewport" content="width=device-width, initial-scale=1">
					<style>
						body { background: #181a1f; color: #f2f4f8; font: 16px sans-serif; padding: 16px; }
						strong { color: #65b7ff; }
					</style>
				</head>
				<body><strong>WebView OK</strong><p>Native AOT host rendered this page.</p></body>
				</html>
				""";

		public MainPage()
		{
				InitializeComponent();
				ProbeWebView.Source = new HtmlWebViewSource { Html = ProbeHtml };
				StatusLabel.Text = $"引擎程序集：{typeof(MinorShift.Emuera.AppDataPaths).Assembly.GetName().Name}";
		}

		private async void OnPickFileClicked(object? sender, EventArgs e)
		{
				try
				{
						var file = await FilePicker.Default.PickAsync(new PickOptions
						{
								PickerTitle = "选择 Native AOT 冒烟文件"
						});

						StatusLabel.Text = file is null
								? "SAF：用户取消"
								: $"SAF：{file.FileName}";
				}
				catch (Exception ex)
				{
						StatusLabel.Text = $"SAF 失败：{ex.GetType().Name}";
				}
		}
}
