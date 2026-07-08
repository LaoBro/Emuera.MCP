using System;
using System.Collections.Generic;
using System.Text;

namespace MinorShift.Emuera.Runtime.Utils;

/// <summary>
/// 跨平台字符串转换，等价于 <c>Microsoft.VisualBasic.Strings.StrConv</c> 在东亚区域下的行为，
/// 但不依赖任何 Windows 专用 API（用于消除 CA1416）。
/// 映射关系由 Unicode 兼容性归一化（FormKC/FormKD）驱动，与 Windows 的
/// LCMapString(LCMAP_FULLWIDTH / LCMAP_HALFWIDTH / LCMAP_KATAKANA / LCMAP_HIRAGANA) 保持一致的语义。
/// </summary>
[Flags]
internal enum StrConvFlags
{
	None = 0,
	Uppercase = 1,
	Lowercase = 2,
	Wide = 4,
	Narrow = 8,
	Katakana = 16,
	Hiragana = 32,
}

internal static class StringConverter
{
	// 半角 -> 全角（half-width 字符串 -> 全角字符串）
	private static readonly Dictionary<string, string> s_halfToFull = new();
	// 全角 -> 半角（全角字符串 -> 半角字符串，值可能含组合浊点/半浊点）
	private static readonly Dictionary<string, string> s_fullToHalf = new();

	// VB 的 StrConv 把无法成字的组合浊点/半浊点输出为独立全角标记（U+309B / U+309C）
	private static string NormalizeMarks(string s)
	{
		if (!s.Contains('\u3099') && !s.Contains('\u309A'))
			return s;
		var sb = new StringBuilder(s.Length);
		foreach (char c in s)
		{
			if (c == '\u3099') sb.Append('\u309B');
			else if (c == '\u309A') sb.Append('\u309C');
			else sb.Append(c);
		}
		return sb.ToString();
	}

	static StringConverter()
	{
		// ASCII 可见字符与空格：用固定偏移（NFKC 不会把 ASCII 变全角）
		// 注意：0x5C（反斜杠）在日语区域下 VB 不做全半角转换，跳过以保持一致
		for (int c = 0x20; c <= 0x7E; c++)
		{
			if (c == 0x20)
			{
				AddMapping(" ", "　");
			}
			else if (c == 0x5C)
			{
				// VB 的 StrConv 不转换反斜杠（保留 0x5C）
			}
			else
			{
				char half = (char)c;
				char full = (char)(c + 0xFEE0);
				AddMapping(half.ToString(), full.ToString());
			}
		}

		// 孤立的半角浊点/半浊点 -> 独立全角标记（与 VB 一致）
		AddMapping("\uFF9E", "\u309B");
		AddMapping("\uFF9F", "\u309C");

		// 半角片假名 / 标点区块 U+FF61..U+FF9F（单字符全角化；基础字+标记的组合在 ToFullWidth 内合成）
		for (int c = 0xFF61; c <= 0xFF9F; c++)
		{
			if (c == 0xFF9E || c == 0xFF9F)
				continue; // 浊点/半浊点本身是组合标记，不单独建表
			string half = ((char)c).ToString();
			string full = NormalizeMarks(half.Normalize(NormalizationForm.FormKC));
			AddMapping(half, full);
		}
	}

	private static void AddMapping(string half, string full)
	{
		s_halfToFull[half] = full;
		s_fullToHalf[full] = half;
	}

	/// <summary>
	/// 等价 <c>Strings.StrConv(str, conv, locale)</c>。locale 仅影响东亚区域下的全半角/假名映射，
	/// 而这些映射与区域无关，故本实现忽略 locale（与日语区域行为一致）。
	/// </summary>
	public static string Convert(string str, StrConvFlags flags, int locale)
	{
		if (string.IsNullOrEmpty(str))
			return str;
		// 日元符号 U+00A5 在日语区域下 VB 当作 0x5C（反斜杠），与各转换标志无关，统一在此归一化
		if (str.Contains('\u00A5'))
			str = str.Replace('\u00A5', '\u005C');
		// 与 VB 一致：互斥标志同时出现时不转换
		if ((flags.HasFlag(StrConvFlags.Wide) && flags.HasFlag(StrConvFlags.Narrow)) ||
			(flags.HasFlag(StrConvFlags.Katakana) && flags.HasFlag(StrConvFlags.Hiragana)))
			return str;

		string s = str;
		if (flags.HasFlag(StrConvFlags.Narrow))
			s = ToHalfWidth(s);
		if (flags.HasFlag(StrConvFlags.Wide))
			s = ToFullWidth(s);
		if (flags.HasFlag(StrConvFlags.Katakana))
			s = ToKatakana(s);
		if (flags.HasFlag(StrConvFlags.Hiragana))
			s = ToHiragana(s);
		return s;
	}

	/// <summary>平假名 -> 片假名（等价于 VbStrConv.Katakana）</summary>
	public static string ToKatakana(string s)
	{
		if (string.IsNullOrEmpty(s))
			return s;
		var sb = new StringBuilder(s.Length);
		foreach (char c in s)
		{
			// 平假名区块（不含扩展假名 U+3094/U+3095/U+3096，VB 不转换）-> 片假名（偏移 0x60）
			if (c >= 0x3041 && c <= 0x3093)
				sb.Append((char)(c + 0x60));
			else if (c == 0x309D) // ゝ -> ヽ
				sb.Append((char)0x30FD);
			else if (c == 0x309E) // ゞ -> ヾ
				sb.Append((char)0x30FE);
			else
				sb.Append(c);
		}
		return sb.ToString();
	}

	/// <summary>片假名 -> 平假名（等价于 VbStrConv.Hiragana）</summary>
	public static string ToHiragana(string s)
	{
		if (string.IsNullOrEmpty(s))
			return s;
		var sb = new StringBuilder(s.Length);
		foreach (char c in s)
		{
			// 片假名区块（不含扩展假名 U+30F4/U+30F5/U+30F6 与共享长音记号 U+30FC，VB 不转换）-> 平假名
			if (c >= 0x30A1 && c <= 0x30F3)
				sb.Append((char)(c - 0x60));
			else if (c == 0x30FD) // ヽ -> ゝ
				sb.Append((char)0x309D);
			else if (c == 0x30FE) // ヾ -> ゞ
				sb.Append((char)0x309E);
			else
				sb.Append(c);
		}
		return sb.ToString();
	}

	/// <summary>半角 -> 全角（等价于 VbStrConv.Wide）</summary>
	public static string ToFullWidth(string s)
	{
		if (string.IsNullOrEmpty(s))
			return s;
		var sb = new StringBuilder(s.Length);
		int i = 0;
		while (i < s.Length)
		{
			char c = s[i];
			bool isMark = c == 0xFF9E || c == 0xFF9F;
			bool nextIsMark = i + 1 < s.Length && (s[i + 1] == 0xFF9E || s[i + 1] == 0xFF9F);
			if (!isMark && nextIsMark)
			{
				// 基础字（平/片假名，半角或全角）+ 半角浊点/半浊点 -> 全角预成字
				char combMark = s[i + 1] == 0xFF9E ? '\u3099' : '\u309A';
				sb.Append(NormalizeMarks((c.ToString() + combMark).Normalize(NormalizationForm.FormKC)));
				i += 2;
			}
			else if (isMark)
			{
				// 孤立的浊点/半浊点 -> 独立全角标记（与 VB 一致，VB 不会丢弃）
				sb.Append(c == 0xFF9E ? '\u309B' : '\u309C');
				i++;
			}
			else if (s_halfToFull.TryGetValue(c.ToString(), out var full))
			{
				sb.Append(full);
				i++;
			}
			else
			{
				sb.Append(c);
				i++;
			}
		}
		return sb.ToString();
	}

	/// <summary>全角 -> 半角（等价于 VbStrConv.Narrow）</summary>
	public static string ToHalfWidth(string s)
	{
		if (string.IsNullOrEmpty(s))
			return s;
		var sb = new StringBuilder(s.Length);
		foreach (char c in s)
		{
			if (c == 0xFF3C)
			{
				// VB 的 StrConv 不把全角反斜杠变半角，保留
				sb.Append(c);
			}
			else if (c == 0xFFE5)
			{
				// 全角日元符号在 Narrow 下 VB 当作 0x5C（反斜杠）
				sb.Append('\u005C');
			}
			else if (s_fullToHalf.TryGetValue(c.ToString(), out var half))
			{
				sb.Append(half);
			}
			else if (c >= 0x30A1 && c <= 0x30F6)
			{
				// 全角片假名浊点/半浊点预成字 -> 半角基础字 + 浊点/半浊点（与 VB 一致）
				string decomp = c.ToString().Normalize(NormalizationForm.FormKD);
				if (decomp.Length == 2 && (decomp[1] == 0x3099 || decomp[1] == 0x309A)
					&& s_fullToHalf.TryGetValue(decomp[0].ToString(), out var baseHalf))
				{
					sb.Append(baseHalf);
					sb.Append(decomp[1] == 0x3099 ? '\uFF9E' : '\uFF9F');
				}
				else
				{
					sb.Append(c);
				}
			}
			else
			{
				sb.Append(c);
			}
		}
		return sb.ToString();
	}
}
