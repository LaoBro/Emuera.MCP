using System;

namespace MinorShift.Emuera;

/// <summary>
/// Headless 模式下的私有字体集合存根。
/// 实际渲染不发生，仅记录字体文件加载请求（空实现）。
/// </summary>
internal sealed class HeadlessFontCollection : IDisposable
{
#pragma warning disable CA1822 // 存根方法故意保持实例方法签名以匹配调用方
	public void AddFontFile(string filename) { }
	public void Dispose() { }
#pragma warning restore CA1822
}
