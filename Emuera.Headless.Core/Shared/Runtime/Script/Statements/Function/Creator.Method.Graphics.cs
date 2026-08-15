using MinorShift.Emuera.GameData.Variable;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Runtime.Script.Data;
using MinorShift.Emuera.Runtime.Script.Parser;
using MinorShift.Emuera.Runtime.Script.Statements;
using MinorShift.Emuera.Runtime.Script.Statements.Expression;
using MinorShift.Emuera.Runtime.Script.Statements.Function;
using MinorShift.Emuera.Runtime.Script.Statements.Variable;
using MinorShift.Emuera.Runtime.Utils;
using MinorShift.Emuera.Runtime.Utils.EvilMask;
using MinorShift.Emuera.Primitives;
using MinorShift.Emuera.UI.Game;
using MinorShift.Emuera.UI.Game.Image;
using System;
using System.Collections.Generic;
using System.Linq;
using trerror = MinorShift.Emuera.Runtime.Utils.EvilMask.Lang.Error;

namespace MinorShift.Emuera.GameData.Function;

internal static partial class FunctionMethodCreator
{
	private static GraphicsImage ReadGraphics(string Name, ExpressionMediator exm, List<AExpression> arguments, int argNo)
	{
		long target = arguments[argNo].GetIntValue(exm);
		if (target < 0)//funcname + "関数:GraphicsIDに負の値(" + target.ToString() + ")が指定されました"
					   // throw new CodeEE(string.Format(Properties.Resources.RuntimeErrMesMethodGraphicsID0, Name, target));
			throw new CodeEE(string.Format(trerror.GIdIsNegative.Text, Name, target));
		else if (target > int.MaxValue)//funcname + "関数:GraphicsIDの値(" + target.ToString() + ")が大きすぎます"
									   // throw new CodeEE(string.Format(Properties.Resources.RuntimeErrMesMethodGraphicsID1, Name, target));
			throw new CodeEE(string.Format(trerror.GIdIsTooLarge.Text, Name, target));
		return AppContents.GetGraphics((int)target);
	}

	public static GraphicsImage ReadGraphics(int target)
	{
		if (target < 0)//funcname + "関数:GraphicsIDに負の値(" + target.ToString() + ")が指定されました"
					   // throw new CodeEE(string.Format(Properties.Resources.RuntimeErrMesMethodGraphicsID0, Name, target));
			throw new CodeEE(string.Format(trerror.GIdIsNegative.Text, "HTML_PRINT", target));
		else if (target > int.MaxValue)//funcname + "関数:GraphicsIDの値(" + target.ToString() + ")が大きすぎます"
									   // throw new CodeEE(string.Format(Properties.Resources.RuntimeErrMesMethodGraphicsID1, Name, target));
			throw new CodeEE(string.Format(trerror.GIdIsTooLarge.Text, "HTML_PRINT", target));
		return AppContents.GetGraphics(target);
	}

	private static EmuColor ReadColor(string Name, ExpressionMediator exm, List<AExpression> arguments, int argNo)
	{
		long c64 = arguments[argNo].GetIntValue(exm);
		if (c64 < 0 || c64 > 0xFFFFFFFF)
			// throw new CodeEE(string.Format(Properties.Resources.RuntimeErrMesMethodColorARGB0, Name, c64));
			throw new CodeEE(string.Format(trerror.InvalidColorARGB.Text, Name, c64));
		return EmuColor.FromArgb((int)(c64 >> 24) & 0xFF, (int)(c64 >> 16) & 0xFF, (int)(c64 >> 8) & 0xFF, (int)c64 & 0xFF);
	}

	private static EmuPoint ReadPoint(string Name, ExpressionMediator exm, List<AExpression> arguments, int argNo)
	{
		long x64 = arguments[argNo].GetIntValue(exm);
		if (x64 < int.MinValue || x64 > int.MaxValue)
			// throw new CodeEE(string.Format(Properties.Resources.RuntimeErrMesMethodDefaultArgumentOutOfRange0, Name,x64, argNo+1));
			throw new CodeEE(string.Format(trerror.ArgIsOutOfRange.Text, Name, argNo + 1, x64, int.MinValue, int.MaxValue));
		long y64 = arguments[argNo + 1].GetIntValue(exm);
		if (y64 < int.MinValue || y64 > int.MaxValue)
			// throw new CodeEE(string.Format(Properties.Resources.RuntimeErrMesMethodDefaultArgumentOutOfRange0, Name,y64, argNo+1+1));
			throw new CodeEE(string.Format(trerror.ArgIsOutOfRange.Text, Name, argNo + 2, y64, int.MinValue, int.MaxValue));
		return new EmuPoint((int)x64, (int)y64);
	}

	private static EmuRectangle ReadRectangle(string Name, ExpressionMediator exm, List<AExpression> arguments, int argNo)
	{
		long x64 = arguments[argNo].GetIntValue(exm);
		if (x64 < int.MinValue || x64 > int.MaxValue)
			// throw new CodeEE(string.Format(Properties.Resources.RuntimeErrMesMethodDefaultArgumentOutOfRange0, Name, x64, argNo + 1));
			throw new CodeEE(string.Format(trerror.ArgIsOutOfRange.Text, Name, argNo + 1, x64, int.MinValue, int.MaxValue));
		long y64 = arguments[argNo + 1].GetIntValue(exm);
		if (y64 < int.MinValue || y64 > int.MaxValue)
			// throw new CodeEE(string.Format(Properties.Resources.RuntimeErrMesMethodDefaultArgumentOutOfRange0, Name, y64, argNo + 1 + 1));
			throw new CodeEE(string.Format(trerror.ArgIsOutOfRange.Text, Name, argNo + 2, y64, int.MinValue, int.MaxValue));

		long w64 = arguments[argNo + 2].GetIntValue(exm);
		if (w64 < int.MinValue || w64 > int.MaxValue || w64 == 0)
			// throw new CodeEE(string.Format(Properties.Resources.RuntimeErrMesMethodDefaultArgumentOutOfRange0, Name, w64, argNo + 2 + 1));
			throw new CodeEE(string.Format(trerror.ArgIsOutOfRangeExcept.Text, Name, argNo + 3, w64, int.MinValue, int.MaxValue, 0));
		long h64 = arguments[argNo + 3].GetIntValue(exm);
		if (h64 < int.MinValue || h64 > int.MaxValue || h64 == 0)
			// throw new CodeEE(string.Format(Properties.Resources.RuntimeErrMesMethodDefaultArgumentOutOfRange0, Name, h64, argNo + 3 + 1));
			throw new CodeEE(string.Format(trerror.ArgIsOutOfRangeExcept.Text, Name, argNo + 4, h64, int.MinValue, int.MaxValue, 0));
		return new EmuRectangle((int)x64, (int)y64, (int)w64, (int)h64);
	}

	private static float[][] ReadColormatrix(string Name, ExpressionMediator exm, List<AExpression> arguments, int argNo)
	{
		//数値型二次元以上配列変数のはず
		FixedVariableTerm p = ((VariableTerm)arguments[argNo]).GetFixedVariableTerm(exm);
		long e1, e2;
		float[][] cm = new float[5][];
		if (p.Identifier.IsArray2D)
		{
			long[,] array;
			if (p.Identifier.IsCharacterData)
			{
				array = (p.Identifier.GetArrayChara((int)p.Index1) as long[,])!;
				e1 = p.Index2;
				e2 = p.Index3;
			}
			else
			{
				array = (p.Identifier.GetArray() as long[,])!;
				e1 = p.Index1;
				e2 = p.Index2;
			}
			if (e1 < 0 || e2 < 0 || e1 + 5 > array.GetLength(0) || e2 + 5 > array.GetLength(1))
				// throw new CodeEE(string.Format(Properties.Resources.RuntimeErrMesMethodGColorMatrix0, Name, e1, e2));
				throw new CodeEE(string.Format(trerror.InvalidColorMatrix.Text, Name, e1, e2));
			for (int x = 0; x < 5; x++)
			{
				cm[x] = new float[5];
				for (int y = 0; y < 5; y++)
				{
					cm[x][y] = array[e1 + x, e2 + y] / 256f;
				}
			}
		}
		if (p.Identifier.IsArray3D)
		{
			long[,,] array; long e3;
			if (p.Identifier.IsCharacterData)
			{
				throw new NotImplCodeEE();
			}
			else
			{
				array = (p.Identifier.GetArray() as long[,,])!;
				e1 = p.Index1;
				e2 = p.Index2;
				e3 = p.Index3;
			}
			if (e1 < 0 || e1 >= array.GetLength(0))
				// throw new CodeEE(string.Format(Properties.Resources.RuntimeErrMesMethodGColorMatrix0, Name, e2, e3));
				throw new CodeEE(string.Format(trerror.InvalidColorMatrix.Text, Name, e2, e3));
			if (e2 < 0 || e3 < 0 || e2 + 5 > array.GetLength(1) || e3 + 5 > array.GetLength(2))
				// throw new CodeEE(string.Format(Properties.Resources.RuntimeErrMesMethodGColorMatrix0, Name, e2, e3));
				throw new CodeEE(string.Format(trerror.InvalidColorMatrix.Text, Name, e2, e3));
			for (int x = 0; x < 5; x++)
			{
				cm[x] = new float[5];
				for (int y = 0; y < 5; y++)
				{
					cm[x][y] = array[e1, e2 + x, e3 + y] / 256f;
				}
			}
		}
		return cm;
	}

	public sealed class GraphicsStateMethod : FunctionMethod
	{
		public GraphicsStateMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArray = [typeof(long)];
			CanRestructure = false;
		}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			if (Config.TextDrawingMode == TextDrawingMode.WINAPI)
				// throw new CodeEE(string.Format(Properties.Resources.RuntimeErrMesMethodGDIPLUSOnly, Name));
				throw new CodeEE(string.Format(trerror.GDIPlusOnly.Text, Name));
			GraphicsImage g = ReadGraphics(Name, exm, arguments, 0);
			if (!g.IsCreated)
				return 0;
			switch (Name)
			{
				case "GCREATED":
					return 1;
				case "GWIDTH":
					return g.Width;
				case "GHEIGHT":
					return g.Height;
				#region EE_GDRAWTEXTに付随する要素
				case "GGETFONTSIZE":
					return g.Fontsize;
				case "GGETFONTSTYLE":
					return g.Fontstyle;
				case "GGETPEN":
#if HEADLESS
				return 0;
#else
				return g.Pen.Color.ToArgb() & 0xffffffffL;
#endif
			case "GGETPENWIDTH":
#if HEADLESS
				return 0;
#else
				return (long)g.Pen.Width;
#endif
			case "GGETBRUSH":
#if HEADLESS
				return 0;
#else
				SolidBrush b = (SolidBrush)g.Brush;
				return b.Color.ToArgb() & 0xffffffffL;
#endif
					#endregion
			}
			throw new ExeEE("GraphicsState:" + Name + ":異常な分岐");
		}
	}

	public sealed class GraphicsStateStrMethod : FunctionMethod
	{
		public GraphicsStateStrMethod()
		{
			ReturnType = typeof(string);
			argumentTypeArray = [typeof(long)];
			CanRestructure = false;
		}
		public override string GetStrValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			if (Config.TextDrawingMode == TextDrawingMode.WINAPI)
				// throw new CodeEE(string.Format(Properties.Resources.RuntimeErrMesMethodGDIPLUSOnly, Name));
				throw new CodeEE(string.Format(trerror.GDIPlusOnly.Text, Name));
			GraphicsImage g = ReadGraphics(Name, exm, arguments, 0);
			if (!g.IsCreated)
				return "";
			switch (Name)
			{
				case "GGETFONT":
					return g.Fontname;
			}
			throw new ExeEE("GraphicsState:" + Name + ":Abnormal branching");
		}
	}

	public sealed class GraphicsGetColorMethod : FunctionMethod
	{
		public GraphicsGetColorMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArray = [typeof(long), typeof(long), typeof(long)];
			CanRestructure = false;
		}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			if (Config.TextDrawingMode == TextDrawingMode.WINAPI)
				// throw new CodeEE(string.Format(Properties.Resources.RuntimeErrMesMethodGDIPLUSOnly, Name));
				throw new CodeEE(string.Format(trerror.GDIPlusOnly.Text, Name));
			GraphicsImage g = ReadGraphics(Name, exm, arguments, 0);
			//失敗したら負の値を返す。他と戻り値違うけど仕方ないね
			if (!g.IsCreated)
				return -1;
			EmuPoint p = ReadPoint(Name, exm, arguments, 1);
			if (p.X < 0 || p.X >= g.Width || p.Y < 0 || p.Y >= g.Height)
				return -1;
			EmuColor c = GraphicsImage.GGetColor(p.X, p.Y);
			//Color.ToArgb()はInt32の負の値をとることがあり、Int64にうまく変換できない?（と思ったが気のせいだった
			return c.ToArgb() & 0xFFFFFFFFL;
		}
	}

	public sealed class GraphicsSetColorMethod : FunctionMethod
	{
		public GraphicsSetColorMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArray = [typeof(long), typeof(long), typeof(long), typeof(long)];
			CanRestructure = false;
		}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			if (Config.TextDrawingMode == TextDrawingMode.WINAPI)
				// throw new CodeEE(string.Format(Properties.Resources.RuntimeErrMesMethodGDIPLUSOnly, Name));
				throw new CodeEE(string.Format(trerror.GDIPlusOnly.Text, Name));
			GraphicsImage g = ReadGraphics(Name, exm, arguments, 0);
			if (!g.IsCreated)
				return 0;
			EmuColor c = ReadColor(Name, exm, arguments, 1);
			EmuPoint p = ReadPoint(Name, exm, arguments, 2);
			if (p.X < 0 || p.X >= g.Width || p.Y < 0 || p.Y >= g.Height)
				return 0;
			GraphicsImage.GSetColor(c, p.X, p.Y);
			return 1;
		}
	}

	public sealed class GraphicsSetBrushMethod : FunctionMethod
	{
		public GraphicsSetBrushMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArray = [typeof(long), typeof(long)];
			CanRestructure = false;
		}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			if (Config.TextDrawingMode == TextDrawingMode.WINAPI)
				// throw new CodeEE(string.Format(Properties.Resources.RuntimeErrMesMethodGDIPLUSOnly, Name));
				throw new CodeEE(string.Format(trerror.GDIPlusOnly.Text, Name));
			GraphicsImage g = ReadGraphics(Name, exm, arguments, 0);
			if (!g.IsCreated)
				return 0;
			EmuColor c = ReadColor(Name, exm, arguments, 1);
#if HEADLESS
			GraphicsImage.GSetBrush(null!);
#else
			g.GSetBrush(new SolidBrush(c));
#endif
			return 1;
		}
	}

	public sealed class GraphicsSetFontMethod : FunctionMethod
	{
		public GraphicsSetFontMethod()
		{
			ReturnType = typeof(long);
			// argumentTypeArray = new Type[] { typeof(Int64), typeof(string), typeof(Int64) };
			// argumentTypeArray = new Type[] { typeof(Int64), typeof(string), typeof(Int64), typeof(Int64) };
			argumentTypeArrayEx = [
					new ArgTypeList{ ArgTypes = { ArgType.Int, ArgType.String, ArgType.Int, ArgType.Int }, OmitStart = 2 }
				];
			CanRestructure = false;
		}
		//public override string CheckArgumentType(string name, IOperandTerm[] arguments)
		//{
		//	if (arguments.Count > 2)
		//		return null!;
		//	return string.Format(Properties.Resources.SyntaxErrMesMethodDefaultArgumentNum1, name, 2);
		//}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			if (Config.TextDrawingMode == TextDrawingMode.WINAPI)
				// throw new CodeEE(string.Format(Properties.Resources.RuntimeErrMesMethodGDIPLUSOnly, Name));
				throw new CodeEE(string.Format(trerror.GDIPlusOnly.Text, Name));
			GraphicsImage g = ReadGraphics(Name, exm, arguments, 0);
			if (!g.IsCreated)
				return 0;
#if HEADLESS
			// Headless 模式下不支持字体设置
			return 0;
#else
			string fontname = arguments[1].GetStrValue(exm);
			long fontsize = arguments[2].GetIntValue(exm);
			FontStyle fs = FontStyle.Regular;
			if (arguments.Count > 3)
			{
				long style = arguments[3].GetIntValue(exm);

				if ((style & 1) != 0)
					fs |= FontStyle.Bold;
				if ((style & 2) != 0)
					fs |= FontStyle.Italic;
				if ((style & 4) != 0)
					fs |= FontStyle.Strikeout;
				if ((style & 8) != 0)
					fs |= FontStyle.Underline;
			}

			Font styledFont;
			try
			{
				#region EE_フォントファイル対応
				foreach (FontFamily ff in GlobalStatic.Pfc.Families)
				{
					if (ff.Name == fontname)
					{
						styledFont = new Font(ff, fontsize, fs, GraphicsUnit.Pixel);
						goto foundfont;
					}
				}
				// styledFont = new Font(fontname, fontsize, FontStyle.Regular, GraphicsUnit.Pixel);
				styledFont = new Font(fontname, fontsize, fs, GraphicsUnit.Pixel);
			}
			catch
			{
				return 0;
			}
		foundfont:
			#endregion
			// g.GSetFont(styledFont);
			g.GSetFont(styledFont, fs);
			return 1;
#endif
		}
	}

	public sealed class GraphicsSetPenMethod : FunctionMethod
	{
		public GraphicsSetPenMethod()
		{
			ReturnType = typeof(long);
			// 私家版のバグだと思う
			// argumentTypeArray = new Type[] { typeof(Int64), typeof(Int64) };
			argumentTypeArray = [typeof(long), typeof(long), typeof(long)];
			CanRestructure = false;
		}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			if (Config.TextDrawingMode == TextDrawingMode.WINAPI)
				// throw new CodeEE(string.Format(Properties.Resources.RuntimeErrMesMethodGDIPLUSOnly, Name));
				throw new CodeEE(string.Format(trerror.GDIPlusOnly.Text, Name));
			GraphicsImage g = ReadGraphics(Name, exm, arguments, 0);
			if (!g.IsCreated)
				return 0;
			EmuColor c = ReadColor(Name, exm, arguments, 1);
			long width = arguments[2].GetIntValue(exm);
#if HEADLESS
			GraphicsImage.GSetPen(null!);
#else
			g.GSetPen(new Pen(c, width));
#endif
			return 1;
		}
	}

	public sealed class GraphicsSetDashStyleMethod : FunctionMethod
	{
		public GraphicsSetDashStyleMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArray = [typeof(long), typeof(long), typeof(long)];
			CanRestructure = false;
		}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			if (Config.TextDrawingMode == TextDrawingMode.WINAPI)
				// throw new CodeEE(string.Format(Properties.Resources.RuntimeErrMesMethodGDIPLUSOnly, Name));
				throw new CodeEE(string.Format(trerror.GDIPlusOnly.Text, Name));
			GraphicsImage g = ReadGraphics(Name, exm, arguments, 0);
			if (!g.IsCreated)
				return 0;

			GraphicsImage.GDashStyle(arguments[1].GetIntValue(exm), arguments[2].GetIntValue(exm));
			return 1;
		}
	}

	public sealed class GraphicsDrawStringMethod : FunctionMethod
	{
		public GraphicsDrawStringMethod()
		{
			ReturnType = typeof(long);
			// argumentTypeArray = new Type[] { typeof(Int64), typeof(string), typeof(Int64), typeof(Int64) };
			argumentTypeArrayEx = [
					new ArgTypeList{ ArgTypes = { ArgType.Int, ArgType.String, ArgType.Int, ArgType.Int }, OmitStart = 2 }
				];
			CanRestructure = false;
		}

		//public override string CheckArgumentType(string name, IOperandTerm[] arguments)
		//{
		//	if (arguments.Count < 2)
		//		return string.Format(Properties.Resources.SyntaxErrMesMethodDefaultArgumentNum1, name, 2);
		//	if (arguments.Count > 4)
		//		return string.Format(Properties.Resources.SyntaxErrMesMethodDefaultArgumentNum2, name);
		//	if (arguments.Count != 2 && arguments.Count != 4)
		//		return string.Format(Properties.Resources.SyntaxErrMesMethodDefaultArgumentNum0, name);

		//	for (int i = 0; i < arguments.Count; i++)
		//	{
		//		if (arguments[i] == null)
		//			return string.Format(Properties.Resources.SyntaxErrMesMethodDefaultArgumentNotNullable0, name, i + 1);

		//		if (i < argumentTypeArray.Length && argumentTypeArray[i] != arguments[i].GetOperandType())
		//			return string.Format(Properties.Resources.SyntaxErrMesMethodDefaultArgumentType0, name, i + 1);
		//	}
		//	if (arguments.Count <= 4)
		//		return null!;
		//	return null!;
		//}

		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			if (Config.TextDrawingMode == TextDrawingMode.WINAPI)
				// throw new CodeEE(string.Format(Properties.Resources.RuntimeErrMesMethodGDIPLUSOnly, Name));
				throw new CodeEE(string.Format(trerror.GDIPlusOnly.Text, Name));
			GraphicsImage g = ReadGraphics(Name, exm, arguments, 0);
			if (!g.IsCreated)
				return 0;
			string text = arguments[1].GetStrValue(exm);
			if (arguments.Count == 2)
			{
				GraphicsImage.GDrawString(text, 0, 0);
			}
			else if (arguments.Count == 4)
			{
				EmuPoint p = ReadPoint(Name, exm, arguments, 2);
				GraphicsImage.GDrawString(text, p.X, p.Y);
			}
#if HEADLESS
			// Headless 模式下无法测量文本尺寸，返回 0
			long[] resultArray0 = exm.VEvaluator.RESULT_ARRAY;
			resultArray0[1] = 0;
			resultArray0[2] = 0;
			return 1;
#else
			//生成する画像のサイズを取得
			var bitmap = new Bitmap(16, 16);
			//Graphics canvas = Graphics.FromImage(bitmap);
			var graphics = Graphics.FromImage(bitmap);
			Font font = g.Fnt;
			if (font == null)
				font = new Font(Config.FontName, 100, GlobalStatic.Console.StringStyle.FontStyle, GraphicsUnit.Pixel);
			var size = graphics.MeasureString(text, font, int.MaxValue, StringFormat.GenericTypographic);

			//TextRenderer
			//Size tsize = TextRenderer.MeasureText(canvas, text, g.Fnt,
			//    new Size(2000, 2000), TextFormatFlags.NoPadding);
			//test用
			long[] resultArray = exm.VEvaluator.RESULT_ARRAY;
			resultArray[1] = (long)size.Width;
			resultArray[2] = (long)size.Height;
			return 1;
#endif
		}
	}

	public sealed class GraphicsGetTextSizeMethod : FunctionMethod
	{
		public GraphicsGetTextSizeMethod()
		{
			ReturnType = typeof(long);
			// argumentTypeArray = new Type[] { typeof(string), typeof(string), typeof(Int64), typeof(Int64) };
			argumentTypeArrayEx = [
					new ArgTypeList{ ArgTypes = { ArgType.String, ArgType.String, ArgType.Int, ArgType.Int }, OmitStart = 3 }
				];
			CanRestructure = false;
		}
		//public override string CheckArgumentType(string name, IOperandTerm[] arguments)
		//{
		//	if (arguments.Count > 2)
		//		return null!;
		//	return string.Format(Properties.Resources.SyntaxErrMesMethodDefaultArgumentNum1, name, 2);
		//}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			if (Config.TextDrawingMode == TextDrawingMode.WINAPI)
				// throw new CodeEE(string.Format(Properties.Resources.RuntimeErrMesMethodGDIPLUSOnly, Name));
				throw new CodeEE(string.Format(trerror.GDIPlusOnly.Text, Name));
			string text = arguments[0].GetStrValue(exm);
#if HEADLESS
			// Headless 模式下无法测量文本尺寸，返回 0
			long[] resultArrayH = exm.VEvaluator.RESULT_ARRAY;
			resultArrayH[1] = 0;
			return 0;
#else
			//生成する画像のサイズを取得
			string fontname = arguments[1].GetStrValue(exm);
			long fontsize = arguments[2].GetIntValue(exm);
			FontStyle fs = FontStyle.Regular;
			if (arguments.Count > 3)
			{
				long style = arguments[3].GetIntValue(exm);
				if ((style & 1) != 0)
					fs |= FontStyle.Bold;
				if ((style & 2) != 0)
					fs |= FontStyle.Italic;
				if ((style & 4) != 0)
					fs |= FontStyle.Strikeout;
				if ((style & 8) != 0)
					fs |= FontStyle.Underline;
			}
			Font fnt = new(fontname, fontsize, fs, GraphicsUnit.Pixel);
			var bitmap = new Bitmap(16, 16);
			//Graphics canvas = Graphics.FromImage(bitmap);
			var graphics = Graphics.FromImage(bitmap);
			var size = graphics.MeasureString(text, fnt, int.MaxValue, StringFormat.GenericTypographic);

			//TextRenderer
			//Size tsize = TextRenderer.MeasureText(canvas, text, fnt,
			//    new Size(2000, 2000), TextFormatFlags.NoPadding);
			long[] resultArray = exm.VEvaluator.RESULT_ARRAY;
			//resultArray[1] = (Int64)tsize.Width;
			resultArray[1] = (long)size.Height;
			return (long)size.Width;
#endif
		}
	}

	public sealed class GraphicsDrawGWithRotateMethod : FunctionMethod
	{
		public GraphicsDrawGWithRotateMethod()
		{
			ReturnType = typeof(long);
			// argumentTypeArray = new Type[] { typeof(Int64), typeof(Int64), typeof(Int64), typeof(Int64), typeof(Int64) };
			argumentTypeArrayEx = [
					new ArgTypeList{ ArgTypes = { ArgType.Int, ArgType.Int, ArgType.Int, ArgType.Int, ArgType.Int }, OmitStart = 3 }
				];
			CanRestructure = false;
		}
		//public override string CheckArgumentType(string name, IOperandTerm[] arguments)
		//{
		//	if (arguments.Count < 3)
		//		return string.Format(Properties.Resources.SyntaxErrMesMethodDefaultArgumentNum1, name, 3);
		//	if (arguments.Count > 5)
		//		return string.Format(Properties.Resources.SyntaxErrMesMethodDefaultArgumentNum2, name);
		//	if (arguments.Count != 3 && arguments.Count != 5)
		//		return string.Format(Properties.Resources.SyntaxErrMesMethodDefaultArgumentNum0, name);
		//	return null!;
		//}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			if (Config.TextDrawingMode == TextDrawingMode.WINAPI)
				// throw new CodeEE(string.Format(Properties.Resources.RuntimeErrMesMethodGDIPLUSOnly, Name));
				throw new CodeEE(string.Format(trerror.GDIPlusOnly.Text, Name));
			GraphicsImage dest = ReadGraphics(Name, exm, arguments, 0);
			if (!dest.IsCreated)
				return 0;
			GraphicsImage src = ReadGraphics(Name, exm, arguments, 1);
			if (!src.IsCreated)
				return 0;
			long angle = arguments[2].GetIntValue(exm);

			//座標省略してたらx/2,y/2で渡す
			if (arguments.Count == 3)
			{
				GraphicsImage.GDrawGWithRotate(src, angle, src.Width / 2, src.Height / 2);
			}
			else
			{
				EmuPoint p = ReadPoint(Name, exm, arguments, 3);
			GraphicsImage.GDrawGWithRotate(src, angle, p.X, p.Y);
			}
			return 1;
		}
	}

	//brushの参照がうまくいかないので保留
	/**
	public sealed class GraphicsGetBrushMethod : FunctionMethod
	{
		public GraphicsGetBrushMethod()
		{
			ReturnType = typeof(Int64);
			 argumentTypeArray = [typeof(Int64)];
			CanRestructure = false;
		}
		public override Int64 GetIntValue(ExpressionMediator exm, IOperandTerm[] arguments)
		{
			Color c = 
			GraphicsImage g = ReadGraphics(Name, exm, arguments, 0);
			return (SolidBrush());
		}
	}
	**/

	public sealed class GraphicsDrawLineMethod : FunctionMethod
	{
		public GraphicsDrawLineMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArray = [typeof(long), typeof(long), typeof(long), typeof(long), typeof(long)];
			CanRestructure = false;
		}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			if (Config.TextDrawingMode == TextDrawingMode.WINAPI)
				throw new CodeEE(string.Format(trerror.GDIPlusOnly.Text, Name));
			GraphicsImage g = ReadGraphics(Name, exm, arguments, 0);
			if (!g.IsCreated)
				return 0;
			EmuPoint fromP = ReadPoint(Name, exm, arguments, 1);
			EmuPoint forP = ReadPoint(Name, exm, arguments, 3);
			GraphicsImage.GDrawLine(fromP.X, fromP.Y, forP.X, forP.Y);
			return 1;
		}
	}

	public sealed class GraphicsCreateMethod : FunctionMethod
	{
		public GraphicsCreateMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArray = [typeof(long), typeof(long), typeof(long)];
			CanRestructure = false;
		}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			if (Config.TextDrawingMode == TextDrawingMode.WINAPI)
				// throw new CodeEE(string.Format(Properties.Resources.RuntimeErrMesMethodGDIPLUSOnly, Name));
				throw new CodeEE(string.Format(trerror.GDIPlusOnly.Text, Name));
			GraphicsImage g = ReadGraphics(Name, exm, arguments, 0);
			if (g.IsCreated)
				return 0;

			EmuPoint p = ReadPoint(Name, exm, arguments, 1);
			int width = p.X; int height = p.Y;
			if (width <= 0)//{0}関数:GraphicsのWidthに0以下の値({1})が指定されました
						   // throw new CodeEE(string.Format(Properties.Resources.RuntimeErrMesMethodGWidth0, Name, width));
				throw new CodeEE(string.Format(trerror.GParamIsNegative.Text, Name, "Width", width));
			else if (width > AbstractImage.MAX_IMAGESIZE)//{0}関数:GraphicsのWidthに{2}以上の値({1})が指定されました
														 // throw new CodeEE(string.Format(Properties.Resources.RuntimeErrMesMethodGWidth1, Name, width, AbstractImage.MAX_IMAGESIZE));
				throw new CodeEE(string.Format(trerror.GParamTooLarge.Text, Name, "Width", AbstractImage.MAX_IMAGESIZE, width));
			if (height <= 0)//{0}関数:GraphicsのHeightに0以下の値({1})が指定されました
							// throw new CodeEE(string.Format(Properties.Resources.RuntimeErrMesMethodGHeight0, Name, height));
				throw new CodeEE(string.Format(trerror.GParamIsNegative.Text, Name, "Height", height));
			else if (height > AbstractImage.MAX_IMAGESIZE)//{0}関数:GraphicsのHeightに{2}以上の値({1})が指定されました
														  // throw new CodeEE(string.Format(Properties.Resources.RuntimeErrMesMethodGHeight1, Name, height, AbstractImage.MAX_IMAGESIZE));
				throw new CodeEE(string.Format(trerror.GParamTooLarge.Text, Name, "Height", AbstractImage.MAX_IMAGESIZE, height));

			g.GCreate(width, height, false);
			return 1;

		}
	}

	public sealed class GraphicsClearMethod : FunctionMethod
	{
		#region EM_私家版_GCLEAR拡張
		public GraphicsClearMethod()
		{
			ReturnType = typeof(long);
			// argumentTypeArray = new Type[] { typeof(Int64), typeof(Int64) };
			argumentTypeArrayEx = [
					new ArgTypeList{ ArgTypes = { ArgType.Int, ArgType.Int } },
					new ArgTypeList{ ArgTypes = { ArgType.Int, ArgType.Int, ArgType.Int, ArgType.Int, ArgType.Int, ArgType.Int } }
				];
			argumentTypeArray = null!;
			CanRestructure = false;
		}
		//public override string CheckArgumentType(string name, IOperandTerm[] arguments)
		//{

		//	if (arguments.Count != 2 && arguments.Count != 6)
		//		return string.Format("{0}関数には2つもしくは6つの引数が必要です", name);
		//	for (int i = 0; i < arguments.Count; i++)
		//	{
		//		if (arguments[i] == null)
		//			return string.Format(Properties.Resources.SyntaxErrMesMethodDefaultArgumentNotNullable0, name, i + 1);
		//		if (arguments[i].GetOperandType() != typeof(Int64))
		//			return string.Format(Properties.Resources.SyntaxErrMesMethodDefaultArgumentType0, name, i + 1);
		//	}
		//	return null!;
		//}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			if (Config.TextDrawingMode == TextDrawingMode.WINAPI)
				// throw new CodeEE(string.Format(Properties.Resources.RuntimeErrMesMethodGDIPLUSOnly, Name));
				throw new CodeEE(string.Format(trerror.GDIPlusOnly.Text, Name));
			GraphicsImage g = ReadGraphics(Name, exm, arguments, 0);
			EmuColor c = ReadColor(Name, exm, arguments, 1);
			if (!g.IsCreated)
				return 0;
			if (arguments.Count == 2)
				GraphicsImage.GClear(c);
			else
				GraphicsImage.GClear(c, (int)arguments[2].GetIntValue(exm), (int)arguments[3].GetIntValue(exm), (int)arguments[4].GetIntValue(exm), (int)arguments[5].GetIntValue(exm));
			return 1;
		}
		#endregion
	}

	public sealed class GraphicsFillRectangleMethod : FunctionMethod
	{
		public GraphicsFillRectangleMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArray = [typeof(long), typeof(long), typeof(long), typeof(long), typeof(long)];
			CanRestructure = false;
		}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			if (Config.TextDrawingMode == TextDrawingMode.WINAPI)
				// throw new CodeEE(string.Format(Properties.Resources.RuntimeErrMesMethodGDIPLUSOnly, Name));
				throw new CodeEE(string.Format(trerror.GDIPlusOnly.Text, Name));
			GraphicsImage g = ReadGraphics(Name, exm, arguments, 0);
			if (!g.IsCreated)
				return 0;
			EmuRectangle rect = ReadRectangle(Name, exm, arguments, 1);
			GraphicsImage.GFillRectangle(rect);
			return 1;
		}
	}

	public sealed class GraphicsDrawGMethod : FunctionMethod
	{
		public GraphicsDrawGMethod()
		{
			ReturnType = typeof(long);
			// argumentTypeArray = null;
			argumentTypeArrayEx = [
					new ArgTypeList{ ArgTypes = { ArgType.Int, ArgType.Int,
							ArgType.Int, ArgType.Int, ArgType.Int, ArgType.Int,
							ArgType.Int, ArgType.Int, ArgType.Int, ArgType.Int, ArgType.RefInt2D | ArgType.AllowConstRef }, OmitStart = 10 },
					new ArgTypeList{ ArgTypes = { ArgType.Int, ArgType.Int,
							ArgType.Int, ArgType.Int, ArgType.Int, ArgType.Int,
							ArgType.Int, ArgType.Int, ArgType.Int, ArgType.Int, ArgType.RefInt3D | ArgType.AllowConstRef }, OmitStart = 10 },
				];
			CanRestructure = false;
			HasUniqueRestructure = true;
		}

		//public override string CheckArgumentType(string name, IOperandTerm[] arguments)
		//{
		//	if (arguments.Count < 10)
		//		return string.Format(Properties.Resources.SyntaxErrMesMethodDefaultArgumentNum1, name, 10);
		//	if (arguments.Count > 11)
		//		return string.Format(Properties.Resources.SyntaxErrMesMethodDefaultArgumentNum2, name);
		//	for (int i = 0; i < 10; i++)
		//	{
		//		if (arguments[i] == null)
		//			return string.Format(Properties.Resources.SyntaxErrMesMethodDefaultArgumentNotNullable0, name, i + 1);
		//		if (typeof(Int64) != arguments[i].GetOperandType())
		//			return string.Format(Properties.Resources.SyntaxErrMesMethodDefaultArgumentType0, name, i + 1);
		//	}
		//	if (arguments.Count == 10)
		//		return null!;
		//	if (!(arguments[10] is VariableTerm varToken) || !varToken.IsInteger || (!varToken.Identifier.IsArray2D && !varToken.Identifier.IsArray3D))
		//		return string.Format(Properties.Resources.SyntaxErrMesMethodGraphicsColorMatrix0, name);
		//	return null!;
		//}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			if (Config.TextDrawingMode == TextDrawingMode.WINAPI)
				// throw new CodeEE(string.Format(Properties.Resources.RuntimeErrMesMethodGDIPLUSOnly, Name));
				throw new CodeEE(string.Format(trerror.GDIPlusOnly.Text, Name));
			GraphicsImage dest = ReadGraphics(Name, exm, arguments, 0);
			if (!dest.IsCreated)
				return 0;
			GraphicsImage src = ReadGraphics(Name, exm, arguments, 1);
			if (!src.IsCreated)
				return 0;
			EmuRectangle destRect = ReadRectangle(Name, exm, arguments, 2);
			EmuRectangle srcRect = ReadRectangle(Name, exm, arguments, 6);
			if (arguments.Count == 10 || arguments[10] == null)
			{
				GraphicsImage.GDrawG(src, destRect, srcRect);
				return 1;
			}
			float[][] cm = ReadColormatrix(Name, exm, arguments, 10);
			GraphicsImage.GDrawG(src, destRect, srcRect, cm);
			return 1;
		}

		public override bool UniqueRestructure(ExpressionMediator exm, List<AExpression> arguments)
		{
			for (int i = 0; i < arguments.Count; i++)
			{
				if (arguments[i] == null)
					continue;
				//11番目の引数はColorMatrixの配列を指しているので定数にしてはいけない
				if (i == 10)
					arguments[i].Restructure(exm);
				else
					arguments[i] = arguments[i].Restructure(exm);
			}
			return false;
		}
	}

	public sealed class GraphicsDrawGWithMaskMethod : FunctionMethod
	{
		public GraphicsDrawGWithMaskMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArray = [typeof(long), typeof(long), typeof(long), typeof(long), typeof(long)];
			CanRestructure = false;
		}


		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			if (Config.TextDrawingMode == TextDrawingMode.WINAPI)
				// throw new CodeEE(string.Format(Properties.Resources.RuntimeErrMesMethodGDIPLUSOnly, Name));
				throw new CodeEE(string.Format(trerror.GDIPlusOnly.Text, Name));
			GraphicsImage dest = ReadGraphics(Name, exm, arguments, 0);
			if (!dest.IsCreated)
				return 0;
			GraphicsImage src = ReadGraphics(Name, exm, arguments, 1);
			if (!src.IsCreated)
				return 0;
			GraphicsImage mask = ReadGraphics(Name, exm, arguments, 2);
			if (!mask.IsCreated)
				return 0;
			if (src.Width != mask.Width || src.Height != mask.Height)
				return 0;
			EmuPoint destPoint = ReadPoint(Name, exm, arguments, 3);
			if (destPoint.X + src.Width > dest.Width || destPoint.Y + src.Height > dest.Height)
				return 0;
			GraphicsImage.GDrawGWithMask(src, mask, destPoint);
			return 1;
		}


	}
}
