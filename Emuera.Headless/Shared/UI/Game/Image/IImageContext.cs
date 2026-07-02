using System;
using MinorShift.Emuera.Primitives;

namespace MinorShift.Emuera.UI.Game.Image;

/// <summary>
/// 图形渲染上下文抽象（替代 System.Drawing.Graphics）。
/// Headless 模式下为空实现；未来可替换为 SKSharp/ImageSharp。
/// </summary>
internal interface IImageContext : IDisposable
{
	void DrawString(string text, EmuFont font, EmuColor color, EmuPoint point);
	void FillRectangle(EmuColor color, EmuRectangle rect);
	void DrawImage(IBitmapImage source, EmuRectangle destRect, EmuRectangle srcRect);
}

/// <summary>
/// 位图图像抽象（替代 System.Drawing.Bitmap）。
/// Headless 模式下为空实现；未来可替换为跨平台图像类型。
/// </summary>
internal interface IBitmapImage : IDisposable
{
	int Width { get; }
	int Height { get; }
	EmuColor GetPixel(int x, int y);
}

/// <summary>
/// 画刷抽象（替代 System.Drawing.SolidBrush）。
/// </summary>
internal interface IBrush : IDisposable
{
	EmuColor Color { get; }
}
