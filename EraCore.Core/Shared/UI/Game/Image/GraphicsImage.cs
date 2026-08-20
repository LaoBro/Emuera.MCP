#if HEADLESS
using MinorShift.Emuera.Primitives;
using MinorShift.Emuera.Runtime.Config;
using System;
using System.Collections.Generic;

namespace MinorShift.Emuera.UI.Game.Image;

/// <summary>
/// Headless 模式下的 GraphicsImage 存根。所有渲染操作为空实现，
/// 脚本系统（GCREATE/GDRAWTEXT 等）可正常调用但不产生实际输出。
/// </summary>
internal sealed class GraphicsImage : AbstractImage
{
	public GraphicsImage(int id) { ID = id; }
	public readonly int ID;

	// 状态存储
	private EmuSize _size;
	private EmuFont _font;
	private bool _created;
	private EmuFontStyle _fontStyle;
	private string _fontname = "";

	public static bool useImgList => false;
	public static List<Tuple<ASprite, EmuRectangle>> drawImgList => null!;

	public override int Width => _size.Width;
	public override int Height => _size.Height;
	public override bool IsCreated => _created;

	public string Fontname => _fontname;
	public int Fontsize => _font.Size > 0 ? (int)_font.Size : 0;
	public int Fontstyle
	{
		get
		{
			int ret = 0;
			if ((_fontStyle & EmuFontStyle.Bold) == EmuFontStyle.Bold) ret |= 1;
			if ((_fontStyle & EmuFontStyle.Italic) == EmuFontStyle.Italic) ret |= 2;
			if ((_fontStyle & EmuFontStyle.Strikeout) == EmuFontStyle.Strikeout) ret |= 4;
			if ((_fontStyle & EmuFontStyle.Underline) == EmuFontStyle.Underline) ret |= 8;
			return ret;
		}
	}

	public EmuFont Fnt => _font;

	public void GCreate(int x, int y, bool useGDI)
	{
		GDispose();
		_size = new EmuSize(x, y);
		_created = true;
	}

	internal void GCreateFromF(object bmp, bool useGDI)
	{
		GDispose();
		_created = true;
	}

	public static void GClear(EmuColor c) { }
	public static void GClear(EmuColor c, int x, int y, int w, int h) { }
	public static void GDrawString(string text, int x, int y) { }
	public static void GDrawString(string text, int x, int y, int width, int height) { }
	public static void GDrawRectangle(EmuRectangle rect) { }
	public static void GFillRectangle(EmuRectangle rect) { }
	public static void GDrawCImg(ASprite img, EmuRectangle destRect) { }
	public static void GDrawCImg(ASprite img, EmuRectangle destRect, float[][] cm) { }
	public static void GDrawG(GraphicsImage srcGra, EmuRectangle destRect, EmuRectangle srcRect) { }
	public static void GDrawG(GraphicsImage srcGra, EmuRectangle destRect, EmuRectangle srcRect, float[][] cm) { }
	public static void GDrawGWithMask(GraphicsImage srcGra, GraphicsImage maskGra, EmuPoint destPoint) { }
	public static void GRotate(long a, int x, int y) { }
	public static void GDrawGWithRotate(GraphicsImage srcGra, long a, int x, int y) { }
	public static void GDrawLine(int fromX, int fromY, int forX, int forY) { }
	public static void GDashStyle(long style, long cap) { }
	public void GSetFont(EmuFont r, EmuFontStyle fs) { _font = r; _fontStyle = fs; _fontname = r.Name; }
	public static void GSetBrush(IBrush r) { }
	public static void GSetPen(object r) { }
	public static object GetBitmap() => null!;
	// 调用方（CBGSETGRAPHG 等）会用 g.Bitmap == null 判断，headless 下恒为 null
	public static object Bitmap => null!;
	public static void GSetColor(EmuColor c, int x, int y) { }
	public static EmuColor GGetColor(int x, int y) => EmuColor.Black;
	public static bool GBitmapToInt64Array(long[,] array, int xstart, int ystart) => false;
	public static bool GByteArrayToBitmap(long[,] array, int xstart, int ystart) => false;
	public static void Load() { }
	public void UnLoad() { _created = false; }
	public void GDispose() { _size = default; _created = false; }
	public override void Dispose() { GDispose(); GC.SuppressFinalize(this); }
	~GraphicsImage() { Dispose(); }
}
#else
using MinorShift.Emuera.Runtime.Config;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace MinorShift.Emuera.UI.Game.Image;

internal sealed class GraphicsImage : AbstractImage
{
	public GraphicsImage(int id)
	{
		ID = id;
		g = null;
		Bitmap = null;
	}
	public readonly int ID;
	Size size;
	Brush brush;
	Pen pen;
	Font font;
	FontStyle style;

	public bool useImgList { get { return drawImgList != null; } }
	public List<Tuple<ASprite, Rectangle>> drawImgList;

	public Bitmap RealBitmap;
	public override Bitmap Bitmap
	{
		set { RealBitmap = value; }
		get
		{
			Load();
			return RealBitmap;
		}
	}

	#region Bitmap書き込み・作成

	public void GCreate(int x, int y, bool useGDI)
	{
		if (useGDI)
			throw new NotImplementedException();
		GDispose();
		RealBitmap = new Bitmap(x, y, PixelFormat.Format32bppArgb);
		size = new Size(x, y);
		g = Graphics.FromImage(RealBitmap);
		drawImgList = [];
		lock (AppContents.tempLoadedGraphicsImages)
			AppContents.tempLoadedGraphicsImages.Add(this);
	}
	internal void GCreateFromF(Bitmap bmp, bool useGDI)
	{
		if (useGDI)
			throw new NotImplementedException();
		GDispose();
		RealBitmap = new Bitmap(bmp.Width, bmp.Height, PixelFormat.Format32bppArgb);
		size = new Size(bmp.Width, bmp.Height);
		g = Graphics.FromImage(RealBitmap);
		g.DrawImage(bmp, 0, 0, bmp.Width, bmp.Height);
	}

	public void GClear(Color c)
	{
		if (g == null)
			throw new NullReferenceException();
		g.Clear(c);
	}

	public void GClear(Color c, int x, int y, int w, int h)
	{
		Load();
		if (g == null)
			throw new NullReferenceException();
		g.SetClip(new Rectangle(x, y, w, h), CombineMode.Replace);
		g.Clear(c);
		g.ResetClip();
		drawImgList = null;
	}

	#region EE_GDRAWTEXT
	public void GDrawString(string text, int x, int y)
	{
		Load();
		if (g == null)
			throw new NullReferenceException();

		drawImgList = null;

		Font usingFont = font;
		var format = new StringFormat(StringFormat.GenericTypographic);
		if (usingFont == null)
			usingFont = new(Config.FontName, 100, GlobalStatic.Console.StringStyle.FontStyle, GraphicsUnit.Pixel);
		GraphicsPath gp = new();
		float emSize = (float)usingFont.Height * usingFont.FontFamily.GetEmHeight(usingFont.Style) / usingFont.FontFamily.GetLineSpacing(usingFont.Style);
		gp.AddString(text, usingFont.FontFamily, (int)usingFont.Style, emSize, new Point(x, y), format);
		g.SmoothingMode = SmoothingMode.AntiAlias;
		if (brush != null)
			g.FillPath(brush, gp);
		else
			g.FillPath(new SolidBrush(Config.ForeColor), gp);

		if (pen != null)
			g.DrawPath(pen, gp);
		else
			g.DrawPath(new Pen(Config.ForeColor), gp);
	}
	#endregion

	public void GDrawString(string text, int x, int y, int width, int height)
	{
		Load();
		if (g == null)
			throw new NullReferenceException();

		drawImgList = null;

		Font usingFont = font;
		if (usingFont == null)
			usingFont = Config.DefaultFont;
		if (brush != null)
		{
			g.DrawString(text, usingFont, brush, new RectangleF(x, y, width, height));
		}
		else
		{
			using var b = new SolidBrush(Config.ForeColor);
			g.DrawString(text, usingFont, b, x, y);
		}
	}

	public void GDrawRectangle(Rectangle rect)
	{
		Load();
		if (g == null)
			throw new NullReferenceException();

		drawImgList = null;

		if (pen != null)
			g.DrawRectangle(pen, rect);
		else
		{
			using var p = new Pen(Config.ForeColor);
			g.DrawRectangle(p, rect);
		}
	}

	public void GFillRectangle(Rectangle rect)
	{
		Load();
		if (g == null)
			throw new NullReferenceException();

		drawImgList = null;

		if (brush != null)
			g.FillRectangle(brush, rect);
		else
		{
			using var b = new SolidBrush(Config.BackColor);
			g.FillRectangle(b, rect);
		}
	}

	public void GDrawCImg(ASprite img, Rectangle destRect)
	{
		Load();
		if (g == null)
			throw new NullReferenceException();
		if (useImgList)
		{
			if (img as SpriteG != null)
			{
				SpriteG imgG = img as SpriteG;
				if (imgG.useImgList)
				{
					foreach (Tuple<ASprite, Rectangle> img_element in imgG.drawImgList)
					{
						if (imgG.isBaseImage(this))
						{
							drawImgList = null;
							break;
						}
						drawImgList.Add(new Tuple<ASprite, Rectangle>(
							img_element.Item1,
							new Rectangle(
								(img_element.Item2.X + destRect.X) * destRect.Width / imgG.DestBaseSize.Width,
								(img_element.Item2.Y + destRect.Y) * destRect.Height / imgG.DestBaseSize.Height,
								img_element.Item2.Width * destRect.Width / imgG.DestBaseSize.Width,
								img_element.Item2.Height * destRect.Height / imgG.DestBaseSize.Height
							)
						));
					}
				}
				else
					drawImgList = null;
			}
			else if (img as SpriteF != null)
			{
				drawImgList.Add(new Tuple<ASprite, Rectangle>(img, destRect));
				if (drawImgList.Count > 50)
					drawImgList = null;
			}
			else
				drawImgList = null;
		}
		img.GraphicsDraw(g, destRect);
	}

	public void GDrawCImg(ASprite img, Rectangle destRect, float[][] cm)
	{
		Load();
		if (g == null)
			throw new NullReferenceException();

		drawImgList = null;

		ImageAttributes imageAttributes = new();
		ColorMatrix colorMatrix = new(cm);
		imageAttributes.SetColorMatrix(colorMatrix, ColorMatrixFlag.Default, ColorAdjustType.Bitmap);
		img.GraphicsDraw(g, destRect, imageAttributes);
	}

	public void GDrawG(GraphicsImage srcGra, Rectangle destRect, Rectangle srcRect)
	{
		Load();
		if (g == null)
			throw new NullReferenceException();

		drawImgList = null;

		Bitmap src = srcGra.GetBitmap();
		g.DrawImage(src, destRect, srcRect, GraphicsUnit.Pixel);
	}

	public void GDrawG(GraphicsImage srcGra, Rectangle destRect, Rectangle srcRect, float[][] cm)
	{
		Load();
		if (g == null)
			throw new NullReferenceException();

		drawImgList = null;

		Bitmap src = srcGra.GetBitmap();
		ImageAttributes imageAttributes = new();
		ColorMatrix colorMatrix = new(cm);
		imageAttributes.SetColorMatrix(colorMatrix, ColorMatrixFlag.Default, ColorAdjustType.Bitmap);
		g.DrawImage(src, destRect, srcRect.X, srcRect.Y, srcRect.Width, srcRect.Height, GraphicsUnit.Pixel, imageAttributes);
	}

	public void GDrawGWithMask(GraphicsImage srcGra, GraphicsImage maskGra, Point destPoint)
	{
		Load();
		if (g == null)
			throw new NullReferenceException();

		drawImgList = null;

		Bitmap destImg = GetBitmap();
		byte[] srcBytes = BytesFromBitmap(srcGra.GetBitmap());
		byte[] srcMaskBytes = BytesFromBitmap(maskGra.GetBitmap());
		Rectangle destRect = new(destPoint.X, destPoint.Y, srcGra.Width, srcGra.Height);

		BitmapData bmpData =
			destImg.LockBits(new Rectangle(0, 0, destImg.Width, destImg.Height),
			ImageLockMode.ReadWrite,
			PixelFormat.Format32bppArgb);
		try
		{
			nint ptr = bmpData.Scan0;
			byte[] pixels = new byte[bmpData.Stride * destImg.Height];
			Marshal.Copy(ptr, pixels, 0, pixels.Length);

			for (int y = 0; y < srcGra.Height; y++)
			{
				int destIndex = ((destPoint.Y + y) * destImg.Width + destPoint.X) * 4;
				int srcIndex = y * srcGra.Width * 4;
				for (int x = 0; x < srcGra.Width; x++)
				{
					if (srcMaskBytes[srcIndex] == 255)
					{
						pixels[destIndex++] = srcBytes[srcIndex++];
						pixels[destIndex++] = srcBytes[srcIndex++];
						pixels[destIndex++] = srcBytes[srcIndex++];
						pixels[destIndex++] = srcBytes[srcIndex++];
					}
					else if (srcMaskBytes[srcIndex] == 0)
					{
						destIndex += 4;
						srcIndex += 4;
					}
					else
					{
						int mask = srcMaskBytes[srcIndex]; mask++;
						pixels[destIndex] = (byte)(srcBytes[srcIndex] * mask + pixels[destIndex] * (256 - mask) >> 8); srcIndex++; destIndex++;
						pixels[destIndex] = (byte)(srcBytes[srcIndex] * mask + pixels[destIndex] * (256 - mask) >> 8); srcIndex++; destIndex++;
						pixels[destIndex] = (byte)(srcBytes[srcIndex] * mask + pixels[destIndex] * (256 - mask) >> 8); srcIndex++; destIndex++;
						pixels[destIndex] = (byte)(srcBytes[srcIndex] * mask + pixels[destIndex] * (256 - mask) >> 8); srcIndex++; destIndex++;
					}
				}
			}

			Marshal.Copy(pixels, 0, ptr, pixels.Length);
		}
		finally
		{
			destImg.UnlockBits(bmpData);
		}
	}

	#region EE_GDRAWGWITHROTATE
	public void GRotate(long a, int x, int y)
	{
		if (g == null)
			throw new NullReferenceException();
		float angle = a;
		g.TranslateTransform(-x, -y, MatrixOrder.Append);
		g.RotateTransform(angle, MatrixOrder.Append);
		g.TranslateTransform(x, y, MatrixOrder.Append);
		g.DrawImageUnscaled(Bitmap, 0, 0);
	}
	public void GDrawGWithRotate(GraphicsImage srcGra, long a, int x, int y)
	{
		if (g == null || srcGra == null)
			throw new NullReferenceException();
		float angle = a;
		g.TranslateTransform(-x, -y, MatrixOrder.Append);
		g.RotateTransform(angle, MatrixOrder.Append);
		g.TranslateTransform(x, y, MatrixOrder.Append);
		Bitmap src = srcGra.GetBitmap();
		g.DrawImage(src, 0, 0);
	}
	#endregion

	#region EE_GDRAWLINE
	public void GDrawLine(int fromX, int fromY, int forX, int forY)
	{
		if (g == null)
			throw new NullReferenceException();

		if (pen != null)
			g.DrawLine(pen, fromX, fromY, forX, forY);
		else
		{
			using (Pen p = new(Config.ForeColor))
				g.DrawLine(p, fromX, fromY, forX, forY);
		}
	}
	#endregion

	#region EE_GDASHSTYLE
	public void GDashStyle(long style, long cap)
	{
		if (g == null)
			throw new NullReferenceException();
		if (pen == null)
			pen = new Pen(Config.ForeColor);
		pen.DashStyle = (DashStyle)style;
		pen.DashCap = (DashCap)cap;
	}
	#endregion

	#region EE_GDRAWTEXT
	public void GSetFont(Font r, FontStyle fs)
	{
		if (font != null)
			font.Dispose();
		font = r;
		style = fs;
	}
	#endregion
	public void GSetBrush(Brush r)
	{
		if (brush != null)
			brush.Dispose();
		brush = r;
	}
	public void GSetPen(Pen r)
	{
		DashStyle style = DashStyle.Solid;
		DashCap cap = DashCap.Flat;

		if (pen != null)
		{
			style = pen.DashStyle;
			cap = pen.DashCap;
			pen.Dispose();
		}
		pen = r;
		pen.DashStyle = style;
		pen.DashCap = cap;
	}

	#region Bitmap読み込み・削除
	public Bitmap GetBitmap()
	{
		if (Bitmap == null)
			throw new NullReferenceException();
		return Bitmap;
	}
	public void GSetColor(Color c, int x, int y)
	{
		if (Bitmap == null)
			throw new NullReferenceException();
		Bitmap.SetPixel(x, y, c);
	}
	public Color GGetColor(int x, int y)
	{
		if (Bitmap == null)
			throw new NullReferenceException();
		return Bitmap.GetPixel(x, y);
	}

	public void UnLoad()
	{
		if (RealBitmap == null)
			return;
		if (g != null)
			g.Dispose();
		if (RealBitmap != null)
			RealBitmap.Dispose();
		g = null;
		RealBitmap = null;
	}

	public void GDispose()
	{
		size = new Size(0, 0);
		drawImgList = null;
		if (RealBitmap == null)
			return;
		if (g != null)
			g.Dispose();
		if (RealBitmap != null)
			RealBitmap.Dispose();
		if (brush != null)
			brush.Dispose();
		if (pen != null)
			pen.Dispose();
		if (font != null)
			font.Dispose();
		g = null;
		RealBitmap = null;
		brush = null;
		pen = null;
		font = null;
	}

	public override void Dispose()
	{
		GDispose();
		GC.SuppressFinalize(this);
	}

	~GraphicsImage()
	{
		Dispose();
	}
	#endregion

	#region 状態判定
	public override bool IsCreated { get { return g != null || useImgList; } }
	public int Width { get { return size.Width; } }
	public int Height { get { return size.Height; } }

	#region EE_GDRAWTEXTに付随する様々な要素
	public string Fontname { get { return font.Name; } }
	public int Fontsize { get { return (int)font.Size; } }
	public int Fontstyle
	{
		get
		{
			int ret = 0;
			if ((style & FontStyle.Bold) == FontStyle.Bold) ret |= 1;
			if ((style & FontStyle.Italic) == FontStyle.Italic) ret |= 2;
			if ((style & FontStyle.Strikeout) == FontStyle.Strikeout) ret |= 4;
			if ((style & FontStyle.Underline) == FontStyle.Underline) ret |= 8;
			return ret;
		}
	}

	public Font Fnt { get { return font; } }
	public Pen Pen { get { return pen; } }
	public Brush Brush { get { return brush; } }
	#endregion

	#endregion

	private static byte[] BytesFromBitmap(Bitmap bmp)
	{
		BitmapData bmpData = bmp.LockBits(
		  new Rectangle(0, 0, bmp.Width, bmp.Height),
		  ImageLockMode.ReadOnly,
		  PixelFormat.Format32bppArgb
		);
		if (bmpData.Stride < 0)
			throw new Exception();
		byte[] pixels = new byte[bmpData.Stride * bmp.Height];
		try
		{
			nint ptr = bmpData.Scan0;
			Marshal.Copy(ptr, pixels, 0, pixels.Length);
		}
		finally
		{
			bmp.UnlockBits(bmpData);
		}
		return pixels;
	}

	public bool GBitmapToInt64Array(long[,] array, int xstart, int ystart)
	{
		if (g == null || Bitmap == null)
			throw new NullReferenceException();
		int w = Bitmap.Width;
		int h = Bitmap.Height;
		if (xstart + w > array.GetLength(0) || ystart + h > array.GetLength(1))
			return false;
		Rectangle rect = new(0, 0, w, h);
		BitmapData bmpData = Bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
		nint ptr = bmpData.Scan0;
		byte[] rgbValues = new byte[w * h * 4];
		Marshal.Copy(ptr, rgbValues, 0, rgbValues.Length);
		Bitmap.UnlockBits(bmpData);
		int i = 0;
		for (int y = 0; y < h; y++)
		{
			for (int x = 0; x < w; x++)
			{
				array[x + xstart, y + ystart] =
				rgbValues[i++] + ((long)rgbValues[i++] << 8) + ((long)rgbValues[i++] << 16) + ((long)rgbValues[i++] << 24);
			}
		}
		return true;
	}

	public bool GByteArrayToBitmap(long[,] array, int xstart, int ystart)
	{
		if (g == null || Bitmap == null)
			throw new NullReferenceException();
		int w = Bitmap.Width;
		int h = Bitmap.Height;
		if (xstart + w > array.GetLength(0) || ystart + h > array.GetLength(1))
			return false;

		byte[] rgbValues = new byte[w * h * 4];
		int i = 0;
		for (int y = 0; y < h; y++)
		{
			for (int x = 0; x < w; x++)
			{
				long c = array[x + xstart, y + ystart];
				rgbValues[i++] = (byte)(c & 0xFF);
				rgbValues[i++] = (byte)(c >> 8 & 0xFF);
				rgbValues[i++] = (byte)(c >> 16 & 0xFF);
				rgbValues[i++] = (byte)(c >> 24 & 0xFF);
			}
		}
		Rectangle rect = new(0, 0, w, h);
		BitmapData bmpData = Bitmap.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
		nint ptr = bmpData.Scan0;
		Marshal.Copy(rgbValues, 0, ptr, rgbValues.Length);
		Bitmap.UnlockBits(bmpData);
		return true;
	}
	#endregion

	public void Load()
	{
		if (RealBitmap != null)
			return;
		if (drawImgList == null)
			return;

		RealBitmap = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppArgb);
		g = Graphics.FromImage(RealBitmap);

		foreach (Tuple<ASprite, Rectangle> tuple in drawImgList)
			tuple.Item1.GraphicsDraw(g, tuple.Item2);

		lock (AppContents.tempLoadedGraphicsImages)
			AppContents.tempLoadedGraphicsImages.Add(this);
	}
}
#endif
