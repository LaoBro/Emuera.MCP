#if ANDROID
using System;
using System.Threading.Tasks;
using Android.Webkit;
using Java.Interop;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Handlers;
using AWebView = Android.Webkit.WebView;

namespace Emuera.Maui.JsBridge;

/// <summary>
/// Android 平台 <see cref="IJsBridge"/> 实现——基于 Android.Webkit.WebView（issue 06 / spec ID5）。
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Attach"/>：从 MAUI <see cref="WebView"/> 取 <see cref="AWebView"/> 平台视图，
/// 调 <see cref="AWebView.AddJavascriptInterface(Java.Lang.Object, string?)"/> 注册 <see cref="Bridge"/> 实例，
/// JS 端通过 <c>window.emueraBridge.postMessage(json)</c> 触发 <see cref="Bridge.PostMessage"/> → <see cref="InputReceived"/>。
/// </para>
/// <para>
/// <see cref="PostTurn"/>：调 <see cref="AWebView.EvaluateJavaScript(string?, JValueCallback?)"/>
/// 执行 <c>window.__emueraOnTurn(turnJson)</c>——turnJson 作为 JS 字面量直接嵌入（JSON ⊂ JS 字面量）。
/// </para>
/// <para>
/// Vue 端（Android）： <c>window.emueraBridge.postMessage(json)</c> → 触发 <see cref="Bridge.PostMessage"/>。
/// </para>
/// <para>
/// 注意： <see cref="Bridge"/> 必须继承 <see cref="Java.Lang.Object"/> 才能暴露给 JS（<c>AddJavascriptInterface</c> 要求）。
/// 方法注解 <c>[JavascriptInterface]</c> 是 Android 4.2+ 的安全要求（仅注解的方法对 JS 可见）。
/// </para>
/// </remarks>
internal sealed class AndroidJsBridge : IJsBridge
{
	private AWebView? _androidWebView;
	private Bridge? _bridge;
	private bool _attached;

	/// <inheritdoc />
	public event Action<string>? InputReceived;

	/// <inheritdoc />
	public Task Attach(WebView webView)
	{
		if (_attached)
			return Task.CompletedTask;
		if (webView?.Handler is not WebViewHandler handler)
			return Task.CompletedTask;
		if (handler.PlatformView is not AWebView platformView)
			return Task.CompletedTask;

		_androidWebView = platformView;
		_bridge = new Bridge(this);
		// 注册名 "emueraBridge" 与 Vue 端 (window as any).emueraBridge.postMessage 对齐（spec ID5）。
		platformView.AddJavascriptInterface(_bridge, "emueraBridge");
		_attached = true;
		// Android WebView 无需异步初始化（AddJavascriptInterface 同步生效），直接返回 CompletedTask。
		return Task.CompletedTask;
	}

	/// <inheritdoc />
	public void PostTurn(string turnJson)
	{
		if (_androidWebView is null)
			return;
		var script = JsBridgeHelper.FormatPostScript(turnJson);
		try
		{
			// EvaluateJavaScript 在 UI 线程异步执行；调用方（BridgeHost）已 Dispatcher.Dispatch 切到 UI 线程。
			// 第二参数 IValueCallback 接收 JS 返回值，本场景不消费，传 null。
			_androidWebView.EvaluateJavaScript(script, null);
		}
		catch (Exception ex)
		{
			System.Diagnostics.Debug.WriteLine($"AndroidJsBridge.PostTurn failed: {ex.Message}");
		}
	}

	/// <summary>
	/// JS → C# 桥接对象——通过 <c>AddJavascriptInterface</c> 注册为 <c>window.emueraBridge</c>。
	/// Vue 端调 <c>window.emueraBridge.postMessage(json)</c> 触发 <see cref="PostMessage"/>，
	/// 进而触发 <see cref="AndroidJsBridge.InputReceived"/>。
	/// </summary>
	/// <remarks>
	/// 必须继承 <see cref="Java.Lang.Object"/>（<c>AddJavascriptInterface</c> 要求 Java 对象）。
	/// <c>[JavascriptInterface]</c> 注解是 Android 4.2+ 安全要求——未注解的方法对 JS 不可见。
	/// </remarks>
	private sealed class Bridge : Java.Lang.Object
	{
		private readonly AndroidJsBridge _owner;

		public Bridge(AndroidJsBridge owner)
		{
			_owner = owner;
		}

		[JavascriptInterface]
		public void PostMessage(string message)
		{
			if (!string.IsNullOrEmpty(message))
			{
				_owner.InputReceived?.Invoke(message);
			}
		}
	}
}
#endif
