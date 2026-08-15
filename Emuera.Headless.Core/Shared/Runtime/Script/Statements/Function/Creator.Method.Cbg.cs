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
	public sealed class CBGClearMethod : FunctionMethod
	{
		public CBGClearMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArray = [];
			CanRestructure = false;
		}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			// CBG_Clear: WinForms-only stub removed in Headless
			return 1;
		}
	}

	public sealed class CBGRemoveRangeMethod : FunctionMethod
	{
		public CBGRemoveRangeMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArray = [typeof(long), typeof(long)];
			CanRestructure = false;
		}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{

			long x64 = arguments[0].GetIntValue(exm);
			long y64 = arguments[1].GetIntValue(exm);
			unchecked
			{
				// CBG_ClearRange: WinForms-only stub removed in Headless
			}
			return 1;
		}
	}

	public sealed class CBGClearButtonMethod : FunctionMethod
	{
		public CBGClearButtonMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArray = [];
			CanRestructure = false;
		}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			// CBG_ClearButton: WinForms-only stub removed in Headless
			return 1;
		}
	}

	public sealed class CBGRemoveBMapMethod : FunctionMethod
	{
		public CBGRemoveBMapMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArray = [];
			CanRestructure = false;
		}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			// CBG_ClearBMap: WinForms-only stub removed in Headless
			return 1;
		}
	}

	public sealed class CBGSetGraphicsMethod : FunctionMethod
	{
		public CBGSetGraphicsMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArray = [typeof(long), typeof(long), typeof(long), typeof(long)];
			CanRestructure = false;
		}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			if (Config.TextDrawingMode == TextDrawingMode.WINAPI)
				throw new CodeEE(string.Format(trerror.GDIPlusOnly.Text, Name));

			GraphicsImage g = ReadGraphics(Name, exm, arguments, 0);
			if (!g.IsCreated || GraphicsImage.Bitmap == null)
				return 0;
			EmuPoint p = ReadPoint(Name, exm, arguments, 1);
			long z64 = arguments[3].GetIntValue(exm);
			if (z64 < int.MinValue || z64 > int.MaxValue || z64 == 0)
				throw new CodeEE(string.Format(trerror.ArgIsOutOfRangeExcept.Text, Name, 4, z64, int.MinValue, int.MaxValue, 0));
			// CBG_SetGraphics: WinForms-only stub removed in Headless
			return 1;

		}
	}

	public sealed class CBGSetBMapGMethod : FunctionMethod
	{
		public CBGSetBMapGMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArray = [typeof(long)];
			CanRestructure = false;
		}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			if (Config.TextDrawingMode == TextDrawingMode.WINAPI)
				throw new CodeEE(string.Format(trerror.GDIPlusOnly.Text, Name));

			GraphicsImage g = ReadGraphics(Name, exm, arguments, 0);
			if (!g.IsCreated || GraphicsImage.Bitmap == null)
				return 0;
			// CBG_SetButtonMap: WinForms-only stub removed in Headless
			return 1;

		}
	}

	public sealed class CBGSetCIMGMethod : FunctionMethod
	{
		public CBGSetCIMGMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArray = [typeof(string), typeof(long), typeof(long), typeof(long)];
			CanRestructure = false;
		}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{

			string imgname = arguments[0].GetStrValue(exm);
			ASprite img = AppContents.GetSprite(imgname);
			if (img == null || !img.IsCreated)
				return 0;
			EmuPoint p = ReadPoint(Name, exm, arguments, 1);
			long z64 = arguments[3].GetIntValue(exm);
			if (z64 < int.MinValue || z64 > int.MaxValue || z64 == 0)
				throw new CodeEE(string.Format(trerror.ArgIsOutOfRangeExcept.Text, Name, 4, z64, int.MinValue, int.MaxValue, 0));
			// CBG_SetImage: WinForms-only stub removed in Headless
			return 1;

		}
	}

	public sealed class CBGSETButtonSpriteMethod : FunctionMethod
	{
		public CBGSETButtonSpriteMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArrayEx = [
					new ArgTypeList{ ArgTypes = { ArgType.Int, ArgType.String, ArgType.String, ArgType.Int, ArgType.Int, ArgType.Int, ArgType.String }, OmitStart = 6 },
				];
			CanRestructure = false;
		}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			if (Config.TextDrawingMode == TextDrawingMode.WINAPI)
				throw new CodeEE(string.Format(trerror.GDIPlusOnly.Text, Name));

			long b64 = arguments[0].GetIntValue(exm);
			if (b64 < 0 || b64 > 0xFFFFFF)
				return 0;
			string imgnameN = arguments[1].GetStrValue(exm);
			ASprite imgN = AppContents.GetSprite(imgnameN);
			string imgnameB = arguments[2].GetStrValue(exm);
			ASprite imgB = AppContents.GetSprite(imgnameB);

			EmuPoint p = ReadPoint(Name, exm, arguments, 3);
			long z64 = arguments[5].GetIntValue(exm);
			if (z64 < int.MinValue || z64 > int.MaxValue || z64 == 0)
				throw new CodeEE(string.Format(trerror.ArgIsOutOfRangeExcept.Text, Name, 6, z64, int.MinValue, int.MaxValue, 0));
			string tooltip = null!;
			if (arguments.Count > 6)
				tooltip = arguments[6].GetStrValue(exm);
			// CBG_SetButtonImage: WinForms-only stub removed in Headless
			return 1;

		}
	}
}
