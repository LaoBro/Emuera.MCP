using System;

namespace Emuera.Maui.JsBridge;

/// <summary>
/// C# → JS 的 JS 字面量构造——单一来源，平台实现共用。
/// </summary>
/// <remarks>
/// <para>
/// <see cref="FormatPostScript"/> 与 <see cref="FormatPostMessageScript"/> 把 JSON 字符串作为 JS 字面量直接嵌入函数调用——
/// 因为传入的是合法 JSON 字符串，JSON 本身就是 JS 字面量子集。
/// 调 <c>JSON.stringify</c> 会双重转义（字符串外面再加引号），错误。
/// </para>
/// <para>
/// 命名统一为 <c>window.__emueraOnTurn</c>（turn 消息）和 <c>window.__emueraOnMessage</c>（非 turn 事件）——
/// Vue 端按 <c>window.location.protocol</c> 判断 MAUI 环境后分别注册两个回调。
/// </para>
/// <para>
/// issue 09：原 <see cref="FormatPostScript"/> 仅服务 turn；新增 <see cref="FormatPostMessageScript"/> 服务
/// 文件选择器等非 turn 事件，避免污染 <c>applyTurn</c> 协议消费链路。
/// </para>
/// </remarks>
internal static class JsBridgeHelper
{
	/// <summary>
	/// 构造 C# → JS 的 turn 投递脚本： <c>window.__emueraOnTurn({turnJson})</c>。
	/// </summary>
	/// <param name="turnJson">合法 JSON 字符串，作为 JS 字面量嵌入。</param>
	/// <returns>可直接传给 <c>CoreWebView2.ExecuteScriptAsync</c> / <c>Android.Webkit.WebView.EvaluateJavaScript</c> 的 JS 脚本。</returns>
	/// <exception cref="ArgumentNullException"><paramref name="turnJson"/> 为 null。</exception>
	public static string FormatPostScript(string turnJson)
	{
		if (turnJson is null)
			throw new ArgumentNullException(nameof(turnJson));
		return $"window.__emueraOnTurn({turnJson})";
	}

	/// <summary>
	/// 构造 C# → JS 的非 turn 事件投递脚本： <c>window.__emueraOnMessage({messageJson})</c>。
	/// </summary>
	/// <remarks>
	/// issue 09 文件选择器引入——用于 <see cref="IJsBridge.PostMessage"/>，与 <see cref="FormatPostScript"/> 分流：
	/// turn 经 <c>__emueraOnTurn</c> → Vue <c>applyTurn</c>；非 turn 事件经 <c>__emueraOnMessage</c> → Vue <c>registerMessageHandler</c>。
	/// </remarks>
	/// <param name="messageJson">合法 JSON 字符串，作为 JS 字面量嵌入。</param>
	/// <returns>可直接传给 <c>CoreWebView2.ExecuteScriptAsync</c> / <c>Android.Webkit.WebView.EvaluateJavaScript</c> 的 JS 脚本。</returns>
	/// <exception cref="ArgumentNullException"><paramref name="messageJson"/> 为 null。</exception>
	public static string FormatPostMessageScript(string messageJson)
	{
		if (messageJson is null)
			throw new ArgumentNullException(nameof(messageJson));
		return $"window.__emueraOnMessage({messageJson})";
	}
}
