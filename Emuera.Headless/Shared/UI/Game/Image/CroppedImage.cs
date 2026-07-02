#if HEADLESS
using MinorShift.Emuera.Runtime.Utils;
using System;
using System.Collections.Generic;
using MinorShift.Emuera.Primitives;
using trerror = MinorShift.Emuera.Runtime.Utils.EvilMask.Lang.Error;

namespace MinorShift.Emuera.UI.Game.Image;

internal abstract class ASprite : AContentItem, IDisposable
{
	public ASprite(string name, EmuSize size) : base(name)
	{
		if (size.Width < 0) size = new EmuSize(-size.Width, size.Height);
		if (size.Height < 0) size = new EmuSize(size.Width, -size.Height);
		DestBaseSize = size;
	}
	public readonly EmuSize DestBaseSize;
	public EmuPoint DestBasePosition;
	public override bool IsCreated => false;
	public abstract void Dispose();
	public void Move(EmuPoint point) { }
	// headless 下无实际位图，返回固定颜色供 SPRITEGETCOLOR 调用
	public virtual EmuColor SpriteGetColor(int x, int y) => EmuColor.Black;
}

internal abstract class ASpriteSingle : ASprite
{
	public ASpriteSingle(string name, AbstractImage img, EmuRectangle rect) : base(name, rect.Size) { }
	public ASpriteSingle(string name, AbstractImage img, EmuRectangle rect, EmuSize destSize) : base(name, destSize) { }
	public AbstractImage BaseImage;
	public override bool IsCreated => BaseImage != null && BaseImage.IsCreated;
	public override void Dispose() { BaseImage = null; }
}

internal sealed class SpriteG : ASpriteSingle
{
	public SpriteG(string name, GraphicsImage gra, EmuRectangle rect) : base(name, gra, rect) { }
	public bool useImgList => false;
	public List<Tuple<ASprite, EmuRectangle>> drawImgList => null;
	public bool isBaseImage(GraphicsImage gImg) => false;
}

internal sealed class SpriteF : ASpriteSingle
{
	public SpriteF(string name, ConstImage image, EmuRectangle rect, EmuPoint pos, EmuSize destSize) : base(name, image, rect, destSize) { }
}

internal sealed class SpriteAnime : ASprite
{
	public SpriteAnime(string name, EmuSize size) : base(name, size) { }
	internal bool AddFrame(AbstractImage parentImage, EmuRectangle rect, EmuPoint pos, int delay) => true;
	internal void ResetTime() { }
	public override bool IsCreated => true;
	public override void Dispose() { }
}
#else
using MinorShift.Emuera.Runtime.Utils;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using trerror = MinorShift.Emuera.Runtime.Utils.EvilMask.Lang.Error;

namespace MinorShift.Emuera.UI.Game.Image;

internal abstract class ASprite : AContentItem, IDisposable
{
	public ASprite(string name, Size size) : base(name)
	{
		if (size.Width < 0) size.Width = -size.Width;
		if (size.Height < 0) size.Height = -size.Height;
		DestBaseSize = size;
	}
	public abstract Color SpriteGetColor(int x, int y);
	public readonly Size DestBaseSize;
	public Point DestBasePosition;
	public abstract void GraphicsDraw(Graphics g, Point offset);
	public abstract void GraphicsDraw(Graphics g, Rectangle destRect);
	public abstract void GraphicsDraw(Graphics g, Rectangle destRect, ImageAttributes attr);
	public abstract void Dispose();
	public void Move(Point point) { DestBasePosition.Offset(point); }
}

internal abstract class ASpriteSingle : ASprite
{
	public ASpriteSingle(string name, AbstractImage img, Rectangle rect) : base(name, rect.Size) { SrcRectangle = rect; BaseImage = img; }
	public ASpriteSingle(string name, AbstractImage img, Rectangle rect, Size destSize) : base(name, destSize) { SrcRectangle = rect; BaseImage = img; }
	public AbstractImage BaseImage;
	public readonly Rectangle SrcRectangle;
	private Bitmap Bitmap { get { if (BaseImage != null && BaseImage.IsCreated) return BaseImage.Bitmap; return null; } }
	public override bool IsCreated => BaseImage != null && BaseImage.IsCreated;
	public override Color SpriteGetColor(int x, int y)
	{
		Bitmap bmp = Bitmap;
		if (bmp == null) return Color.Transparent;
		int bmpX = x + SrcRectangle.X; int bmpY = y + SrcRectangle.Y;
		if (bmpX < 0 || bmpX >= bmp.Width || bmpY < 0 || bmpY >= bmp.Height) return Color.Transparent;
		return bmp.GetPixel(bmpX, bmpY);
	}
	public override void Dispose() { BaseImage = null; }
	public override void GraphicsDraw(Graphics g, Point offset) { offset.Offset(DestBasePosition); g.DrawImage(Bitmap, new Rectangle(offset, DestBaseSize), SrcRectangle, GraphicsUnit.Pixel); }
	public override void GraphicsDraw(Graphics g, Rectangle destRect)
	{
		if (!DestBasePosition.IsEmpty) { destRect.X += DestBasePosition.X * destRect.Width / DestBaseSize.Width; destRect.Y += DestBasePosition.Y * destRect.Height / DestBaseSize.Height; destRect.Width = destRect.Width * SrcRectangle.Width / DestBaseSize.Width; destRect.Height = destRect.Height * SrcRectangle.Height / DestBaseSize.Height; }
		g.DrawImage(Bitmap, destRect, SrcRectangle, GraphicsUnit.Pixel);
	}
	public override void GraphicsDraw(Graphics g, Rectangle destRect, ImageAttributes attr)
	{
		if (!DestBasePosition.IsEmpty) { destRect.X += DestBasePosition.X * destRect.Width / DestBaseSize.Width; destRect.Y += DestBasePosition.Y * destRect.Height / DestBaseSize.Height; destRect.Width = destRect.Width * SrcRectangle.Width / DestBaseSize.Width; destRect.Height = destRect.Height * SrcRectangle.Height / DestBaseSize.Height; }
		g.DrawImage(Bitmap, destRect, SrcRectangle.X, SrcRectangle.Y, SrcRectangle.Width, SrcRectangle.Height, GraphicsUnit.Pixel, attr);
	}
}

internal sealed class SpriteG : ASpriteSingle
{
	public SpriteG(string name, GraphicsImage gra, Rectangle rect) : base(name, gra, rect) { }
	public bool useImgList { get { return (BaseImage as GraphicsImage).useImgList; } }
	public List<Tuple<ASprite, Rectangle>> drawImgList { get { return (BaseImage as GraphicsImage).drawImgList; } }
	public bool isBaseImage(GraphicsImage gImg) { return BaseImage as GraphicsImage == gImg; }
}

internal sealed class SpriteF : ASpriteSingle
{
	public SpriteF(string name, ConstImage image, Rectangle rect, Point pos, Size destSize) : base(name, image, rect, destSize) { DestBasePosition = pos; }
}

internal sealed class SpriteAnime : ASprite
{
	public SpriteAnime(string name, Size size) : base(name, size) { FrameList = []; totaltime = 0; }
	private sealed class AnimeFrame : IDisposable
	{
		public int index; public AbstractImage BaseImage; public Rectangle SrcRectangle; public Point Offset; public int DelayTimeMs;
		public void Normalize(Size parentSize) { Rectangle rect = Rectangle.Intersect(new Rectangle(Offset, SrcRectangle.Size), new Rectangle(new Point(), parentSize)); if (rect.IsEmpty) { BaseImage = null; return; } Offset.X = rect.X; Offset.Y = rect.Y; SrcRectangle.Width = rect.Width; SrcRectangle.Height = rect.Height; }
		public void Dispose() { BaseImage = null; }
	}
	List<AnimeFrame> FrameList;
	public long totaltime;
	internal bool AddFrame(AbstractImage parentImage, Rectangle rect, Point pos, int delay)
	{
		AnimeFrame frame = new() { index = FrameList.Count, BaseImage = parentImage, SrcRectangle = rect, Offset = pos };
		if (delay <= 0) delay = 1;
		frame.DelayTimeMs = delay; frame.Normalize(DestBaseSize); totaltime += delay; FrameList.Add(frame);
		return true;
	}
	internal void ResetTime() { StartTime = DateTime.MinValue; lastFrameTime = DateTime.MinValue; lastFrame = -1; }
	DateTime StartTime; DateTime lastFrameTime; int lastFrame = -1;
	private AnimeFrame GetCurrentFrame()
	{
		if (totaltime <= 0) return null;
		if (lastFrame == -1) { StartTime = DateTime.Now; lastFrameTime = StartTime; lastFrame = 0; return FrameList[0]; }
		if (DateTime.Now == lastFrameTime && lastFrame >= 0) return FrameList[lastFrame];
		lastFrameTime = DateTime.Now;
		long elapsedTime = (long)(lastFrameTime - StartTime).TotalMilliseconds % totaltime;
		foreach (AnimeFrame frame in FrameList) { elapsedTime -= frame.DelayTimeMs; if (elapsedTime <= 0) { lastFrame = frame.index; return frame; } }
		throw new ExeEE(trerror.SpriteTimeOut.Text);
	}
	public override bool IsCreated => true;
	public override void Dispose() { foreach (var frame in FrameList) frame.Dispose(); FrameList.Clear(); totaltime = 0; lastFrame = -1; }
	public override Color SpriteGetColor(int x, int y) { throw new NotSupportedException(); }
	public override void GraphicsDraw(Graphics g, Point offset)
	{
		AnimeFrame frame = GetCurrentFrame(); if (frame == null || frame.BaseImage == null || !frame.BaseImage.IsCreated || frame.BaseImage.Bitmap == null) return;
		offset.Offset(DestBasePosition); offset.Offset(frame.Offset);
		g.DrawImage(frame.BaseImage.Bitmap, new Rectangle(offset, frame.SrcRectangle.Size), frame.SrcRectangle, GraphicsUnit.Pixel);
	}
	public override void GraphicsDraw(Graphics g, Rectangle destRect)
	{
		AnimeFrame frame = GetCurrentFrame(); if (frame == null || frame.BaseImage == null || !frame.BaseImage.IsCreated || frame.BaseImage.Bitmap == null) return;
		destRect.X += (DestBasePosition.X + frame.Offset.X) * destRect.Width / DestBaseSize.Width; destRect.Y += (DestBasePosition.Y + frame.Offset.Y) * destRect.Height / DestBaseSize.Height;
		destRect.Width = frame.SrcRectangle.Width * destRect.Width / DestBaseSize.Width; destRect.Height = frame.SrcRectangle.Height * destRect.Height / DestBaseSize.Height;
		g.DrawImage(frame.BaseImage.Bitmap, destRect, frame.SrcRectangle, GraphicsUnit.Pixel);
	}
	public override void GraphicsDraw(Graphics g, Rectangle destRect, ImageAttributes attr)
	{
		AnimeFrame frame = GetCurrentFrame(); if (frame == null || frame.BaseImage == null || !frame.BaseImage.IsCreated || frame.BaseImage.Bitmap == null) return;
		destRect.X += (DestBasePosition.X + frame.Offset.X) * destRect.Width / DestBaseSize.Width; destRect.Y += (DestBasePosition.Y + frame.Offset.Y) * destRect.Height / DestBaseSize.Height;
		destRect.Width = frame.SrcRectangle.Width * destRect.Width / DestBaseSize.Width; destRect.Height = frame.SrcRectangle.Height * destRect.Height / DestBaseSize.Height;
		g.DrawImage(frame.BaseImage.Bitmap, destRect, frame.SrcRectangle.X, frame.SrcRectangle.Y, frame.SrcRectangle.Width, frame.SrcRectangle.Height, GraphicsUnit.Pixel, attr);
	}
}
#endif
