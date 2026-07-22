#if WINDOWS
using System;
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
/// 等待 <c>EnsureCoreWebView2Async</c> 完成，订阅 <see cref="CoreWebView2.WebMessageReceived"/>。
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
	private CoreWebView2? _core;
	private bool _attached;

	/// <inheritdoc />
	public event Action<string>? InputReceived;

	/// <inheritdoc />
	public async void Attach(WebView webView)
	{
		if (_attached)
			return;
		if (webView?.Handler is not WebViewHandler handler)
			return;
		if (handler.PlatformView is not WebView2 platformView)
			return;

		try
		{
			// EnsureCoreWebView2Async 在 CoreWebView2 已就绪时立即完成；首次调用会等待初始化。
			// 使用 await 避免 WebMessageReceived 订阅早于 CoreWebView2 就绪导致漏事件。
			await platformView.EnsureCoreWebView2Async();
			_core = platformView.CoreWebView2;
			if (_core is null)
				return;

			_core.WebMessageReceived += OnWebMessageReceived;
			_attached = true;
		}
		catch (Exception ex)
		{
			// 与 PostTurn 一致——Attach 失败不该 crash（async void 异常会逃逸到 SynchronizationContext）。
			System.Diagnostics.Debug.WriteLine($"WindowsJsBridge.Attach failed: {ex.Message}");
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
			if (!string.IsNullOrEmpty(message))
			{
				InputReceived?.Invoke(message);
			}
		}
		catch (Exception ex)
		{
			System.Diagnostics.Debug.WriteLine($"WindowsJsBridge.OnWebMessageReceived failed: {ex.Message}");
		}
	}
}
#endif
