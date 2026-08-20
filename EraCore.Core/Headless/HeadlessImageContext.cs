using MinorShift.Emuera.Primitives;

namespace MinorShift.Emuera.UI.Game.Image;

/// <summary>
/// Headless 模式的空图形上下文。所有渲染操作为空操作。
/// </summary>
internal sealed class HeadlessImageContext : IImageContext
{
	public void DrawString(string text, EmuFont font, EmuColor color, EmuPoint point) { }
	public void FillRectangle(EmuColor color, EmuRectangle rect) { }
	public void DrawImage(IBitmapImage source, EmuRectangle destRect, EmuRectangle srcRect) { }
	public void Dispose() { }
}

/// <summary>
/// Headless 模式的空位图。所有像素操作返回透明色。
/// </summary>
internal sealed class HeadlessBitmap : IBitmapImage
{
	public int Width => 0;
	public int Height => 0;
	public EmuColor GetPixel(int x, int y) => EmuColor.Transparent;
	public void Dispose() { }
}

/// <summary>
/// Headless 模式的空画刷。
/// </summary>
internal sealed class HeadlessBrush : IBrush
{
	public EmuColor Color => EmuColor.Transparent;
	public void Dispose() { }
}
