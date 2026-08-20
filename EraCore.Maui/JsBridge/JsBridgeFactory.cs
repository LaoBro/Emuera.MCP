namespace EraCore.Maui.JsBridge;

/// <summary>
/// <see cref="IJsBridge"/> 平台分流工厂——issue 06 / spec ID5。
/// </summary>
/// <remarks>
/// 编译期 <c>#if</c> 分流：Windows 编译返回 <c>WindowsJsBridge</c>，Android 编译返回 <c>AndroidJsBridge</c>。
/// 调用方（<c>MainPage</c> / <c>BridgeHost</c>）拿 <see cref="IJsBridge"/> 抽象，不平台分叉。
/// iOS / MacCatalyst 未实现，编译期抛 <see cref="System.PlatformNotSupportedException"/>（编译时不可达——
/// 项目 csproj 仅 net10.0-android + net10.0-windows10.0.19041.0，spec Out of Scope 明确不做 iOS）。
/// </remarks>
internal static class JsBridgeFactory
{
	/// <summary>
	/// 创建当前平台对应的 <see cref="IJsBridge"/> 实例。
	/// </summary>
	/// <returns>Windows 下返回 <c>WindowsJsBridge</c>，Android 下返回 <c>AndroidJsBridge</c>。</returns>
	/// <exception cref="System.PlatformNotSupportedException">
	/// 当前平台不在 Phase 1 范围（iOS / MacCatalyst）。
	/// </exception>
	public static IJsBridge Create()
	{
#if WINDOWS
		return new WindowsJsBridge();
#elif ANDROID
		return new AndroidJsBridge();
#else
		throw new System.PlatformNotSupportedException(
			"IJsBridge 当前平台不支持（Phase 1 仅 Windows + Android；iOS/MacCatalyst 见 spec Out of Scope）");
#endif
	}
}
