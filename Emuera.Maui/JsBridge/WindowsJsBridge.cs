#if WINDOWS
using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Handlers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;

namespace Emuera.Maui.JsBridge;

/// <summary>
/// Windows 平台 <see cref="IJsBridge"/> 实现——基于 CoreWebView2（issue 06 / spec ID5）。
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Attach"/>：从 MAUI <see cref="WebView"/> 取 WinUI <see cref="WebView2"/> 平台视图，
/// 等待 <c>EnsureCoreWebView2Async</c> 完成，配置虚拟主机映射（unpackaged 模式下 <c>ms-appx-web:</c>
/// 协议不可用，改用 <c>https://app.local/</c> 映射到 wwwroot 文件夹），订阅 <see cref="CoreWebView2.WebMessageReceived"/>。
/// </para>
/// <para>
/// <see cref="PostTurn"/>：调 <see cref="CoreWebView2.ExecuteScriptAsync"/> 执行
/// <c>window.__emueraOnTurn(turnJson)</c>——turnJson 作为 JS 字面量直接嵌入（JSON ⊂ JS 字面量）。
/// 异步 fire-and-forget，异常捕获写日志（不阻塞游戏循环线程）。
/// </para>
/// <para>
/// Vue 端（Windows）： <c>window.chrome.webview.postMessage(json)</c> → 触发 <see cref="CoreWebView2.WebMessageReceived"/>。
/// </para>
/// </remarks>
internal sealed class WindowsJsBridge : IJsBridge
{
	/// <summary>虚拟主机名——unpackaged 模式下用 <c>https://app.local/</c> 映射到 wwwroot 文件夹。</summary>
	/// <remarks>
	/// Packaged（MSIX）模式下可用 <c>ms-appx-web:///</c> 直接访问包内文件；
	/// unpackaged 模式下该协议不可用，改用 WebView2 的 SetVirtualHostNameToFolderMapping
	/// 把 <c>app.local</c> 映射到输出目录的 wwwroot/ 文件夹，Vue 用 <c>https://app.local/index.html</c> 加载。
	/// </remarks>
	internal const string VirtualHostName = "app.local";

	private CoreWebView2? _core;
	private bool _attached;

	/// <inheritdoc />
	public event Action<string>? InputReceived;

	/// <inheritdoc />
	public async Task Attach(WebView webView)
	{
		if (_attached)
			return;
		if (webView?.Handler is not WebViewHandler handler)
			return;
		if (handler.PlatformView is not WebView2 platformView)
			return;

		try
		{
			Console.WriteLine($"WindowsJsBridge.Attach starting, PlatformView ready");
			// EnsureCoreWebView2Async 在 CoreWebView2 已就绪时立即完成；首次调用会等待初始化。
			// 必须 await 完成后再调 SetVirtualHostNameToFolderMapping——否则映射未就绪时
			// 调用方（MainPage）设 Source 触发首次导航，https://app.local/ 请求会失败。
			await platformView.EnsureCoreWebView2Async();
			_core = platformView.CoreWebView2;
			if (_core is null)
			{
				Console.WriteLine("WindowsJsBridge.attach: CoreWebView2 null after EnsureCoreWebView2Async");
				return;
			}

			// unpackaged 模式下 ms-appx-web: 协议不可用——用 SetVirtualHostNameToFolderMapping
			// 把 https://app.local/ 映射到输出目录的 wwwroot/ 文件夹。
			// VueBuild.targets 的 CopyVueFrontendToWwwroot target 把 Vue 构建产物复制到此目录。
			// folderPath 用 AppContext.BaseDirectory（exe 所在目录）+ wwwroot 拼接。
			var wwwrootFolder = Path.Combine(AppContext.BaseDirectory, "wwwroot");
			Console.WriteLine($"WindowsJsBridge.attach: wwwrootFolder={wwwrootFolder}, exists={Directory.Exists(wwwrootFolder)}");
			if (Directory.Exists(wwwrootFolder))
			{
				_core.SetVirtualHostNameToFolderMapping(
					VirtualHostName,
					wwwrootFolder,
					CoreWebView2HostResourceAccessKind.Allow);
			}

			_core.WebMessageReceived += OnWebMessageReceived;
			_attached = true;
			Console.WriteLine("WindowsJsBridge.attach completed, WebMessageReceived subscribed");
		}
		catch (Exception ex)
		{
			Console.WriteLine($"WindowsJsBridge.attach failed: {ex}");
		}
	}

	/// <inheritdoc />
	public async void PostTurn(string turnJson)
	{
		if (_core is null)
			return;
		var script = JsBridgeHelper.FormatPostScript(turnJson);
		try
		{
			// ExecuteScriptAsync 在 UI 线程执行；调用方（BridgeHost）已 Dispatcher.Dispatch 切到 UI 线程。
			// 返回值是 JS 表达式的 JSON 序列化（__emueraOnTurn 返 undefined → "null"），本场景不消费。
			await _core.ExecuteScriptAsync(script);
		}
		catch (Exception ex)
		{
			// 不向游戏循环抛——PostTurn 失败不该 crash 整个游戏。Vue 端会因未收到 turn 而超时。
			System.Diagnostics.Debug.WriteLine($"WindowsJsBridge.PostTurn failed: {ex.Message}");
		}
	}

	private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
	{
		try
		{
			var message = e.TryGetWebMessageAsString();
			Console.WriteLine($"WindowsJsBridge.OnWebMessageReceived: message={message}");
			if (!string.IsNullOrEmpty(message))
			{
				InputReceived?.Invoke(message);
			}
		}
		catch (Exception ex)
		{
			Console.WriteLine($"WindowsJsBridge.OnWebMessageReceived failed: {ex.Message}");
		}
	}
}
#endif
