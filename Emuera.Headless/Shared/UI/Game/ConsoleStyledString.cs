using MinorShift.Emuera.Primitives;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Runtime.Config.JSON;
using MinorShift.Emuera.UI.Game.Image;
using System;
#if !HEADLESS
using System.Drawing;
using System.Windows.Forms;
#endif

namespace MinorShift.Emuera.UI.Game;

/// <summary>
/// 装飾付文字列。stringとStringStyleからなる。
/// </summary>
internal sealed class ConsoleStyledString : AConsoleColoredPart
{
	private ConsoleStyledString() { }
	public ConsoleStyledString(string str, StringStyle style)
	{
		//if ((StaticConfig.TextDrawingMode != TextDrawingMode.GRAPHICS) && (str.IndexOf('\t') >= 0))
		//    str = str.Replace("\t", "");
		Text = str;
		StringStyle = style;
		Font = FontFactory.GetFont(style.Fontname, style.FontStyle);
		if (Font.Size <= 0)
		{
			Error = true;
			return;
		}
		Color = style.Color;
		ButtonColor = style.ButtonColor;
		colorChanged = style.ColorChanged;
		if (!colorChanged && Color != Config.ForeColor)
			colorChanged = true;
		PointX = -1;
		Width = -1;
	}
	public EmuFont Font { get; private set; }
	public StringStyle StringStyle { get; private set; }
	public override bool CanDivide
	{
		get { return true; }
	}
	//単一のボタンフラグ
	//public bool IsButton { get; set; }
	//indexの文字数の前方文字列とindex以降の後方文字列に分割
	public ConsoleStyledString DivideAt(int index, StringMeasure sm)
	{
		//if ((index <= 0)||(index > Text.Length)||this.Error)
		//	return null;
		ConsoleStyledString ret = DivideAt(index);
		if (ret == null)
			return null;
		SetWidth(sm, XsubPixel);
		ret.SetWidth(sm, XsubPixel);
		return ret;
	}
	public ConsoleStyledString DivideAt(int index)
	{
		if (index <= 0 || index > Text.Length || Error)
			return null;
		string str = Text[index..];
		Text = Text[..index];
		ConsoleStyledString ret = new()
		{
			Font = Font,
			Text = str,
			Color = Color,
			ButtonColor = ButtonColor,
			colorChanged = colorChanged,
			StringStyle = StringStyle,
			XsubPixel = XsubPixel
		};
		return ret;
	}

	public override void SetWidth(StringMeasure sm, float subPixel)
	{
		if (Error)
		{
			Width = 0;
			return;
		}
		Width = sm.GetDisplayLength(Text, Font);
		XsubPixel = subPixel;

		#region EmuEra-Rikaichan
		if (!rikaichaned && Config.RikaiEnabled)
		{
			rikaichaned = true;
			int len = Text.Length;
			Ends = new int[len];
			for (int i = 0; i < len; i++)
			{
				string temp = Text.Substring(0, i + 1);
				Ends[i] = sm.GetDisplayLength(temp, Font);
			}

		}
		#endregion
	}

	public override void DrawTo(IImageContext graph, int pointY, bool isSelecting, bool isFocus, bool isBackLog, TextDrawingMode mode, bool isButton = false)
	{
		if (Error)
			return;
		EmuColor drawColor = Color;
		EmuColor? backcolor = null;
		if (isFocus)
		{
			if (JSONConfig.Data.UseButtonFocusBackgroundColor)
			{
				if (!(EmuColor.Yellow.R == drawColor.R &&
					EmuColor.Yellow.G == drawColor.G &&
					EmuColor.Yellow.B == drawColor.B) &&
					!string.IsNullOrWhiteSpace(Text))
				{
					backcolor = EmuColor.Gray;
				}
			}
			drawColor = ButtonColor;
		}
		else if (isBackLog && !colorChanged)
		{
			drawColor = Config.LogColor;
		}

		#region EM_私家版_描画拡張
		if (mode == TextDrawingMode.GRAPHICS)
		{
			graph.DrawString(Text, Font, drawColor, new EmuPoint(PointX, pointY));
		}
#if !HEADLESS
		else
		{
			// WinForms rendering path - needs System.Drawing.Graphics
			// This code path is only for WinForms UI mode, not headless
			// For now, we use the IImageContext abstraction which is a no-op in headless mode
			// TODO: When WinForms rendering is re-enabled, this needs a proper bridge
		}
#endif
		#endregion
	}

	//Bitmap Cache
	public void DrawToBitmap(IImageContext graph, bool isSelecting, bool isBackLog, TextDrawingMode mode, int xOffset)
	{
		if (Error)
			return;
		EmuColor drawColor = Color;
		if (isSelecting)
			drawColor = ButtonColor;
		else if (isBackLog && !colorChanged)
			drawColor = Config.LogColor;

		#region EM_私家版_描画拡張
		if (mode == TextDrawingMode.GRAPHICS)
			graph.DrawString(Text, Font, drawColor, new EmuPoint(xOffset, 0));
#if !HEADLESS
		// WinForms rendering path - no-op in headless mode
#endif
		#endregion
	}
}
