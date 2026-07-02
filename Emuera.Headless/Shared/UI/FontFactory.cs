using MinorShift.Emuera.Primitives;
using MinorShift.Emuera.Runtime.Config;
using System.Collections.Generic;

namespace MinorShift.Emuera.UI;

/// <summary>
/// 字体工厂。Headless 模式下返回 EmuFont 值类型，不依赖 GDI/GDI+。
/// 未来跨平台 UI 可替换为实际字体解析。
/// </summary>
internal class FontFactory
{
	static readonly Dictionary<(string fontname, int fontSize, EmuFontStyle fontStyle), EmuFont> fontDic = [];

	public static EmuFont GetFont(string requestFontName, EmuFontStyle style)
	{
		string fn = string.IsNullOrEmpty(requestFontName) ? Config.FontName : requestFontName;
		var key = (fn, Config.FontSize, style);

		if (!fontDic.TryGetValue(key, out var font))
		{
			font = new EmuFont(fn, Config.FontSize, style);
			fontDic[key] = font;
		}
		return font;
	}

	public static void ClearFont()
	{
		fontDic.Clear();
	}
}
