using MinorShift.Emuera.Primitives;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.UI.Game.Image;
using System;
using System.Text;
using static MinorShift.Emuera.Runtime.Utils.EvilMask.Utils;

namespace MinorShift.Emuera.UI.Game;

abstract class ConsoleShapePart : AConsoleColoredPart
{
	#region EM_私家版_HTMLパラメータ拡張
	static public ConsoleShapePart CreateShape(string shapeType, MixedNum[] param, EmuColor color, EmuColor bcolor, bool colorchanged)
	// static public ConsoleShapePart CreateShape(string shapeType, int[] param, EmuColor color, EmuColor bcolor, bool colorchanged)
	{
		string type = shapeType.ToLower();
		colorchanged = colorchanged || color != Config.ForeColor;
		StringBuilder sb = new();
		sb.Append("<shape type='");
		sb.Append(type);
		sb.Append("' param='");
		for (int i = 0; i < param.Length; i++)
		{
			// sb.Append(param[i].ToString());
			sb.Append(param[i].num);
			if (param[i].isPx) sb.Append("px");
			if (i < param.Length - 1)
				sb.Append(", ");
		}
		sb.Append('\'');
		if (colorchanged)
		{
			sb.Append(" color='");
			sb.Append(HtmlManager.GetColorToString(color));
			sb.Append('\'');
		}
		if (bcolor != Config.FocusColor)
		{
			sb.Append(" bcolor='");
			sb.Append(HtmlManager.GetColorToString(bcolor));
			sb.Append('\'');
		}
		sb.Append('>');
		ConsoleShapePart ret = null!;
		int lineHeight = Config.FontSize;
		//float[] paramPixel = new float[param.Length];
		//for (int i = 0; i < param.Length; i++)
		//{
		//	paramPixel[i] = ((float)param[i] * lineHeight) / 100f;
		//}
		//RectangleF rectF;

		switch (type)
		{
			case "space":
				#region EM_私家版_space制限解除
				// if (paramPixel.Length == 1 && paramPixel[0] >= 0)
				#endregion
				if (param.Length == 1)
				{
					//rectF = new RectangleF(0, 0, paramPixel[0], lineHeight);
					var rectF = new EmuRectangleF(0, 0, param[0].isPx ? param[0].num : (float)param[0].num * lineHeight / 100f, lineHeight);
				ret = new ConsoleSpacePart(rectF);
				}
				break;
			case "rect":
				// if (paramPixel.Length == 1 && paramPixel[0] >= 0)
				if (param.Length == 1 && param[0].num > 0)
				{
					//rectF = new RectangleF(0, 0, paramPixel[0], lineHeight);
					var rectF = new EmuRectangleF(0, 0, param[0].isPx ? param[0].num : (float)param[0].num * lineHeight / 100f, lineHeight);
				ret = new ConsoleRectangleShapePart(rectF);
				}
				// else if (paramPixel.Length == 4)
				else if (param.Length == 4)
				{
					//rectF = new RectangleF(paramPixel[0], paramPixel[1], paramPixel[2], paramPixel[3]);
					var rectF = new EmuRectangleF(MixedNum.ToPixelf(param[0]), MixedNum.ToPixelf(param[1]), MixedNum.ToPixelf(param[2]), MixedNum.ToPixelf(param[3]));
					//1820a12 サイズ上限撤廃
					if (rectF.X >= 0 && rectF.Width > 0 && rectF.Height > 0)
					//	rectF.Y >= 0 && (rectF.Y + rectF.Height) <= lineHeight)
					{
						ret = new ConsoleRectangleShapePart(rectF);
					}
				}
				break;
			case "polygon":
				break;
		}
		if (ret == null)
		{
			ret = new ConsoleErrorShapePart(sb.ToString());
		}
		ret.AltText = sb.ToString();
		ret.Color = color;
		ret.ButtonColor = bcolor;
		ret.colorChanged = colorchanged;
		return ret;
	}
	#endregion

	public override bool CanDivide
	{
		get { return false; }
	}

	/// <summary>issue 02：填充色（协议 shape segment 的 color 字段用）。</summary>
	internal EmuColor FillColor => Color;

	public override string ToString()
	{
		if (AltText == null)
			return "";
		return AltText;
	}
	#region EM_私家版_描画拡張
	public override StringBuilder BuildString(StringBuilder sb)
	{
		if (AltText != null) sb.Append(AltText);
		return sb;
	}
	#endregion
}

	internal sealed class ConsoleRectangleShapePart : ConsoleShapePart
{
	public ConsoleRectangleShapePart(EmuRectangleF theRect)
	{
		Text = "";
		originalRectF = theRect;
		WidthF = theRect.X + theRect.Width;
		int rectY = (int)theRect.Y;
		int rectHeight = (int)theRect.Height;
		if (rectHeight == 0 && theRect.Height >= 0.001f)
			rectHeight = 1;
		rect = new EmuRectangle(0, rectY, 0, rectHeight);
		top = Math.Min(0, rect.Y);
		bottom = Math.Max(Config.FontSize, rect.Y + rect.Height);
	}
	private readonly int top;
	private readonly int bottom;
	public override int Top { get { return top; } }
	public override int Bottom { get { return bottom; } }
	/// <summary>issue 01：SetWidth 后的最终绝对几何（含 shape position shift），协议序列化用。</summary>
	internal EmuRectangle Rect => rect;
	readonly EmuRectangleF originalRectF;
	bool visible;
	EmuRectangle rect;
	public override void DrawTo(IImageContext graph, int pointY, bool isSelecting, bool isFocus, bool isBackLog, TextDrawingMode mode, bool isButton = false)
	{
		if (!visible)
			return;
		EmuRectangle targetRect = rect;
		targetRect = new EmuRectangle(targetRect.X + PointX, targetRect.Y + pointY, targetRect.Width, targetRect.Height);
		EmuColor dcolor = isSelecting ? ButtonColor : Color;
		graph.FillRectangle(dcolor, targetRect);
	}

	public override void SetWidth(StringMeasure sm, float subPixel)
	{
		float widF = subPixel + WidthF;
		Width = (int)widF;
		XsubPixel = widF - Width;
		int rectX = (int)(subPixel + originalRectF.X);
		int rectWidth = Width - rectX;
		rectX += Config.DrawingParam_ShapePositionShift;
		rect = new EmuRectangle(rectX, rect.Y, rectWidth, rect.Height);
		visible = rect.X >= 0 && rect.Width > 0;// && rect.Y >= 0 && (rect.Y + rect.Height) <= Config.Config.FontSize);
	}
}

internal sealed class ConsoleSpacePart : ConsoleShapePart
{
	public ConsoleSpacePart(EmuRectangleF theRect)
	{
		Text = "";
		WidthF = theRect.Width;
		//Width = width;
	}

	public override void DrawTo(IImageContext graph, int pointY, bool isSelecting, bool isFocus, bool isBackLog, TextDrawingMode mode, bool isButton = false) { }

	public override void SetWidth(StringMeasure sm, float subPixel)
	{
		float widF = subPixel + WidthF;
		Width = (int)widF;
		XsubPixel = widF - Width;
	}
}

internal sealed class ConsoleErrorShapePart : ConsoleShapePart
{
	public ConsoleErrorShapePart(string errMes)
	{
		Text = errMes;
		AltText = errMes;
	}

	public override void DrawTo(IImageContext graph, int pointY, bool isSelecting, bool isFocus, bool isBackLog, TextDrawingMode mode, bool isButton = false)
	{
		if (mode == TextDrawingMode.GRAPHICS)
			graph.DrawString(Text, Config.DefaultFont, Config.ForeColor, new EmuPoint(PointX, pointY));
#if !HEADLESS
		// WinForms rendering path - no-op in headless mode
#endif
	}
	public override void SetWidth(StringMeasure sm, float subPixel)
	{
		if (Error)
		{
			Width = 0;
			return;
		}
		Width = sm.GetDisplayLength(Text, Config.DefaultFont);
		XsubPixel = subPixel;
	}
}
