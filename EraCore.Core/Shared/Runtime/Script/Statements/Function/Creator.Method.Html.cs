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
	private sealed class HtmlStringLenMethod : FunctionMethod
	{
		public HtmlStringLenMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArrayEx = [
					new ArgTypeList{ ArgTypes = { ArgType.String, ArgType.Int}, OmitStart = 1 }
				];
			CanRestructure = true;
		}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			int len = HtmlManager.HtmlLength(arguments[0].GetStrValue(exm));
			if (arguments.Count == 1 || arguments[1].GetIntValue(exm) == 0)
			{
				if (len >= 0)
					return 2 * len / Config.FontSize + ((2 * len % Config.FontSize != 0) ? 1 : 0);
				else
					return 2 * len / Config.FontSize - ((2 * len % Config.FontSize != 0) ? 1 : 0);
			}
			return len;
		}
	}

	private sealed class HtmlSubStringMethod : FunctionMethod
	{
		public HtmlSubStringMethod()
		{
			ReturnType = typeof(string);
			argumentTypeArrayEx = [new ArgTypeList{ ArgTypes = { ArgType.String | ArgType.DisallowVoid, ArgType.Int | ArgType.DisallowVoid } }];
			CanRestructure = false;
		}

		public override string GetStrValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			string str = arguments[0].GetStrValue(exm);
			string[] strs = HtmlManager.HtmlSubString(str, (int)arguments[1].GetIntValue(exm));
			string[] output = GlobalStatic.Process.VEvaluator.RESULTS_ARRAY;
			int outputlength = Math.Min(output.Length, strs.Length);
			Array.Copy(strs, output, outputlength);
			return output[0];
		}
	}

	private sealed class HtmlStringLinesMethod : FunctionMethod
	{
		public HtmlStringLinesMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArrayEx = [new ArgTypeList{ ArgTypes = { ArgType.String | ArgType.DisallowVoid, ArgType.Int | ArgType.DisallowVoid } }];
			CanRestructure = false;
		}

		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			string str = arguments[0].GetStrValue(exm);
			if (string.IsNullOrEmpty(str)) return 0;
			var ret = 0;
			do
			{
				string[] strs = HtmlManager.HtmlSubString(str, (int)arguments[1].GetIntValue(exm));
				str = strs[1];
				ret++;
			} while (!string.IsNullOrEmpty(str));
			return ret;
		}
	}

	private sealed class HtmlGetPrintedStrMethod : FunctionMethod
	{
		public HtmlGetPrintedStrMethod()
		{
			ReturnType = typeof(string);
			argumentTypeArrayEx = [
					new ArgTypeList{ ArgTypes = { ArgType.Int }, OmitStart = 0 }
				];
			CanRestructure = false;
		}

		public override string GetStrValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			long lineNo = 0;
			if (arguments.Count > 0)
				lineNo = arguments[0].GetIntValue(exm);
			if (lineNo < 0)
				throw new CodeEE(string.Format(trerror.ArgIsNegative.Text, Name, 1, lineNo));
			ConsoleDisplayLine[] dispLines = exm.Console.GetDisplayLines(lineNo)!;
			if (dispLines == null)
				return "";
			return HtmlManager.DisplayLine2Html(dispLines, true);
		}
	}

	private sealed class HtmlPopPrintingStrMethod : FunctionMethod
	{
		public HtmlPopPrintingStrMethod()
		{
			ReturnType = typeof(string);
			argumentTypeArrayEx = [new ArgTypeList()];
			CanRestructure = false;
		}

		public override string GetStrValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			ConsoleDisplayLine[] dispLines = exm.Console.PopDisplayingLines()!;
			if (dispLines == null)
				return "";
			return HtmlManager.DisplayLine2Html(dispLines, false);
		}
	}

	private sealed class HtmlToPlainTextMethod : FunctionMethod
	{
		public HtmlToPlainTextMethod()
		{
			ReturnType = typeof(string);
			argumentTypeArrayEx = [new ArgTypeList{ ArgTypes = { ArgType.String | ArgType.DisallowVoid } }];
			CanRestructure = false;
		}
		public override string GetStrValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			return HtmlManager.Html2PlainText(arguments[0].GetStrValue(exm));
		}
	}

	private sealed class HtmlEscapeMethod : FunctionMethod
	{
		public HtmlEscapeMethod()
		{
			ReturnType = typeof(string);
			argumentTypeArrayEx = [new ArgTypeList{ ArgTypes = { ArgType.String | ArgType.DisallowVoid } }];
			CanRestructure = false;
		}
		public override string GetStrValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			return HtmlManager.Escape(arguments[0].GetStrValue(exm));
		}
	}
}
