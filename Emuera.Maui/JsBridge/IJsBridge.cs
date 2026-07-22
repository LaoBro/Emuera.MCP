using Microsoft.Maui.Controls;

namespace Emuera.Maui.JsBridge;

/// <summary>
/// C# ↔ JS 桥接抽象层——issue 06 / spec ID5。
/// 屏蔽 Windows CoreWebView2 与 Android Webkit WebView 的平台差异，让 <c>BridgeHost</c> 不平台分叉。
/// </summary>
/// <remarks>
/// 三个成员：
/// <list type="bullet">
///   <item><see cref="PostTurn"/>：C# → JS，调 <c>window.__emueraOnTurn(turnJson)</c>。turnJson 是合法 JSON 字符串，
///       作为 JS 字面量传函数参数（无需再 JSON.stringify）。</item>
///   <item><see cref="InputReceived"/>：JS → C#，Vue 端 <c>postMessage(json)</c> 触发，C# 侧 <c>MauiBridgeIO.EnqueueInput</c>。</item>
///   <item><see cref="Attach"/>：挂载到 MAUI <see cref="WebView"/>，订阅平台原生消息事件。
///       在 <c>WebView.HandlerChanged</c> 事件内调（拿平台原生视图的最早可靠时机）。</item>
/// </list>
/// iOS / MacCatalyst 不在 Phase 1 范围（spec Out of Scope）。
/// </remarks>
internal interface IJsBridge
{
	/// <summary>
	/// C# → JS：向 WebView 投递一个 turn JSON。
	/// 内部调 <c>EvaluateJavaScriptAsync($"window.__emueraOnTurn({turnJson})")</c>。
	/// </summary>
	/// <param name="turnJson">合法 JSON 字符串（来自 <c>TurnRecord</c> 序列化）。作为 JS 字面量传入函数参数。</param>
	void PostTurn(string turnJson);

	/// <summary>
	/// JS → C#：Vue 端 <c>postMessage(json)</c> 触发。订阅者（<c>BridgeHost</c>）将消息入 <c>MauiBridgeIO</c>。
	/// </summary>
	event Action<string>? InputReceived;

	/// <summary>
	/// 挂载到 MAUI <see cref="WebView"/>——订阅平台原生消息事件（Windows <c>WebMessageReceived</c> /
	/// Android <c>AddJavascriptInterface</c>）。在 <c>WebView.HandlerChanged</c> 事件内调。
	/// </summary>
	/// <param name="webView">MAUI 跨平台 <see cref="WebView"/>，实现内部取平台原生视图。</param>
	void Attach(WebView webView);
}
