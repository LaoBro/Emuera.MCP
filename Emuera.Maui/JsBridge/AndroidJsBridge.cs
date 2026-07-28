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
/// <para>
/// game-library spec ID11：原 <c>PickFolderAsync</c> 空桩已删除——Android 不再走平台 picker，
/// 改由 <see cref="BridgeHost.HandleListDirectories"/> + Vue 端 <c>DirectoryBrowser.vue</c> 弹窗选目录。
/// 接口默认实现（<c>Task.FromResult&lt;string?&gt;(null)</c>）兜底，Android 路径不会到达此方法。
/// </para>
/// </remarks>
internal sealed class AndroidJsBridge : IJsBridge
{
	private AWebView? _androidWebView;
	private Bridge? _bridge;
	private bool _attached;

	/// <inheritdoc />
	public event Action<string>? InputReceived;

    /// <summary>ADR-0019 诊断：静态日志缓冲区，Vue 通过 scanGames/gamesScanned 回复可见。</summary>
    internal static string? LastDiagnostic;

    /// <summary>InputReceived 无订阅者时的兜底回调。</summary>
    internal static Action<string>? FallbackInputHandler;

    /// <inheritdoc />
    public Task Attach(Microsoft.Maui.Controls.WebView webView)
	{
		Android.Util.Log.Info("EmueraMaui", $"AndroidJsBridge.Attach called, _attached={_attached}, Handler={(webView?.Handler != null ? webView.Handler.GetType().Name : "null")}");
		if (_attached)
			return Task.CompletedTask;
		if (webView?.Handler is not WebViewHandler handler)
		{
			Android.Util.Log.Warn("EmueraMaui", $"AndroidJsBridge.Attach: WebViewHandler null, cannot attach");
			return Task.CompletedTask;
		}
		if (handler.PlatformView is not AWebView platformView)
		{
			Android.Util.Log.Warn("EmueraMaui", $"AndroidJsBridge.Attach: PlatformView is {handler.PlatformView?.GetType().Name ?? "null"}, not Android.Webkit.WebView");
			return Task.CompletedTask;
		}

		_androidWebView = platformView;
		_bridge = new Bridge(this);
		platformView.AddJavascriptInterface(_bridge, "emueraBridge");

		// ADR-0019：WebViewClient 拦截 bridge:// URL——可靠 JS→C# 通道，不依赖 emueraBridge
		platformView.SetWebViewClient(new BridgeClient(this));

		_attached = true;
		Android.Util.Log.Info("EmueraMaui", "AndroidJsBridge.Attach completed: emueraBridge registered");
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
			_androidWebView.EvaluateJavascript(script, null);
		}
		catch (Exception ex)
		{
			System.Diagnostics.Debug.WriteLine($"AndroidJsBridge.PostTurn failed: {ex.Message}");
		}
	}

	/// <inheritdoc />
	public void PostMessage(string messageJson)
	{
		if (_androidWebView is null)
			return;
		var script = JsBridgeHelper.FormatPostMessageScript(messageJson);
		try
		{
			// 与 PostTurn 同样——EvaluateJavaScript 在 UI 线程异步执行。
			// __emueraOnMessage 在 Vue 端由 registerMessageHandler 注册，未注册时返 undefined，无副作用。
			_androidWebView.EvaluateJavascript(script, null);
		}
		catch (Exception ex)
		{
			System.Diagnostics.Debug.WriteLine($"AndroidJsBridge.PostMessage failed: {ex.Message}");
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
				var hasSub = _owner.InputReceived != null;
				if (hasSub)
					_owner.InputReceived!.Invoke(message);
				else
					FallbackInputHandler?.Invoke(message);
			}
		}
	}

	/// <summary>ADR-0019：bridge:// URL → Action 回调——可靠 JS→C# 通道。</summary>
	internal static event Action<string>? BridgeUrlReceived;

	private sealed class BridgeClient : WebViewClient
	{
		private readonly AndroidJsBridge _bridge;
		public BridgeClient(AndroidJsBridge bridge) => _bridge = bridge;

		public override bool ShouldOverrideUrlLoading(AWebView? view, IWebResourceRequest? request)
		{
			if (request?.Url?.Scheme == "bridge")
			{
				var host = request.Url.Host;
				if (host == "post")
				{
					var msg = request.Url.GetQueryParameter("msg");
					if (msg != null)
					{
						var json = Uri.UnescapeDataString(msg);
						Android.Util.Log.Info("EmueraMaui", $"BridgeClient post: {json[..Math.Min(json.Length, 80)]}");
						BridgeUrlReceived?.Invoke(json);
						return true;
					}
				}
				else
				{
					Android.Util.Log.Info("EmueraMaui", $"BridgeClient action: {host}");
					BridgeUrlReceived?.Invoke(host);
					return true;
				}
			}
			return base.ShouldOverrideUrlLoading(view, request);
		}
	}
}
#endif
