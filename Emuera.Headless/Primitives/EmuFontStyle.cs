using System;

namespace MinorShift.Emuera.Primitives;

/// <summary>
/// 轻量级 FontStyle 值类型，替代 System.Drawing.FontStyle。
/// 支持位运算，与原类型数值完全兼容。
/// </summary>
public readonly record struct EmuFontStyle(int Value)
{
	public static readonly EmuFontStyle Regular = new(0);
	public static readonly EmuFontStyle Bold = new(1);
	public static readonly EmuFontStyle Italic = new(2);
	public static readonly EmuFontStyle Underline = new(4);
	public static readonly EmuFontStyle Strikeout = new(8);

	// 位运算操作符
	public static EmuFontStyle operator |(EmuFontStyle a, EmuFontStyle b)
		=> new(a.Value | b.Value);
	public static EmuFontStyle operator &(EmuFontStyle a, EmuFontStyle b)
		=> new(a.Value & b.Value);
	public static EmuFontStyle operator ^(EmuFontStyle a, EmuFontStyle b)
		=> new(a.Value ^ b.Value);

	// 与 int 的位运算
	public static EmuFontStyle operator |(EmuFontStyle a, int b)
		=> new(a.Value | b);
	public static EmuFontStyle operator &(EmuFontStyle a, int b)
		=> new(a.Value & b);

	// 等值比较（record struct 自动生成 == 和 !=，但需要与 int 比较）
	public static bool operator ==(EmuFontStyle a, int b) => a.Value == b;
	public static bool operator !=(EmuFontStyle a, int b) => a.Value != b;
	public static bool operator ==(int a, EmuFontStyle b) => a == b.Value;
	public static bool operator !=(int a, EmuFontStyle b) => a != b.Value;

#if !HEADLESS
	// 隐式转换：与 System.Drawing.FontStyle 数值完全一致
	public static implicit operator EmuFontStyle(System.Drawing.FontStyle fs)
		=> new((int)fs);

	public static implicit operator System.Drawing.FontStyle(EmuFontStyle fs)
		=> (System.Drawing.FontStyle)fs.Value;
#endif
}
