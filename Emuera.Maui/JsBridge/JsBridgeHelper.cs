using System;

namespace Emuera.Maui.JsBridge;

/// <summary>
/// <see cref="IJsBridge.PostTurn"/> 的 JS 字面量构造——单一来源，平台实现共用。
/// </summary>
/// <remarks>
/// <para>
/// <see cref="FormatPostScript"/> 把 turnJson 作为 JS 字面量直接嵌入函数调用——
/// 因为 turnJson 是合法 JSON 字符串（来自 <c>TurnRecord</c> 序列化），JSON 本身就是 JS 字面量子集。
/// 调用 <c>JSON.stringify</c> 会双重转义（字符串外面再加引号），错误。
/// </para>
/// <para>
/// 命名统一为 <c>window.__emueraOnTurn</c>（spec ID5 / 修订记录推翻原 <c>receiveTurn</c>）。
/// Vue 端按 <c>window.location.protocol</c> 判断 MAUI 环境后注册 <c>window.__emueraOnTurn = (turn) => applyTurn(turn)</c>。
/// </para>
/// </remarks>
internal static class JsBridgeHelper
{
	/// <summary>
	/// 构造 C# → JS 的脚本字符串： <c>window.__emueraOnTurn({turnJson})</c>。
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
}
