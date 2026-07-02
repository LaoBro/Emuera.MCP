namespace MinorShift.Emuera.Primitives;

/// <summary>
/// 轻量级字体描述类型，替代 System.Drawing.Font。
/// 仅封装字体名称、大小和样式，不依赖 GDI/GDI+。
/// </summary>
public readonly record struct EmuFont(string Name, float Size, EmuFontStyle Style)
{
	public static readonly EmuFont Default = new("ＭＳ ゴシック", 18, EmuFontStyle.Regular);

	/// <summary>
	/// 近似行高（emSize × 1.2），用于文本测量。
	/// </summary>
	public int Height => (int)(Size * 1.2f);

	/// <summary>
	/// 字体族名称（与 Name 相同，保留用于兼容 GraphicsImage.FontFamily 访问）。
	/// </summary>
	public string FontFamilyName => Name;

	/// <summary>
	/// 从 System.Drawing.Font 创建 EmuFont（隐式转换）。
	/// </summary>
	public static implicit operator EmuFont(System.Drawing.Font f)
		=> new(f.Name, f.Size, f.Style);

	/// <summary>
	/// 转换为 System.Drawing.Font（需要时使用）。
	/// </summary>
	public static implicit operator System.Drawing.Font(EmuFont f)
		=> new(f.Name, f.Size, f.Style, System.Drawing.GraphicsUnit.Pixel);
}
