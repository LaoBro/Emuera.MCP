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
	private sealed class GetcharaMethod : FunctionMethod
	{
		public GetcharaMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArrayEx = [
					new ArgTypeList{ ArgTypes = { ArgType.Int, ArgType.Int }, OmitStart = 1 },
				];
			CanRestructure = false;
		}

		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			long integer = arguments[0].GetIntValue(exm);
			if (!Config.CompatiSPChara)
			{
				return exm.VEvaluator.GetChara(integer);
			}
			//以下互換性用の旧処理
			bool CheckSp = false;
			if ((arguments.Count > 1) && (arguments[1] != null) && (arguments[1].GetIntValue(exm) != 0))
				CheckSp = true;
			if (CheckSp)
			{
				long chara = exm.VEvaluator.GetChara_UseSp(integer, false);
				if (chara != -1)
					return chara;
				else
					return exm.VEvaluator.GetChara_UseSp(integer, true);
			}
			else
				return exm.VEvaluator.GetChara_UseSp(integer, false);
		}
	}

	private sealed class GetspcharaMethod : FunctionMethod
	{
		public GetspcharaMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArray = [typeof(long)];
			CanRestructure = false;
		}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			if (!Config.CompatiSPChara)
				throw new CodeEE(trerror.SPCharacterFeatureDisabled.Text);
			long integer = arguments[0].GetIntValue(exm);
			return exm.VEvaluator.GetChara_UseSp(integer, true);
		}
	}

	private sealed class CsvStrDataMethod : FunctionMethod
	{
		readonly CharacterStrData charaStr;
		public CsvStrDataMethod()
		{
			ReturnType = typeof(string);
			argumentTypeArray = null!;
			charaStr = CharacterStrData.NAME;
			argumentTypeArrayEx = [
					new ArgTypeList{ ArgTypes = { ArgType.Int, ArgType.Int }, OmitStart = 1 },
				];
			CanRestructure = true;
		}
		public CsvStrDataMethod(CharacterStrData cStr)
		{
			ReturnType = typeof(string);
			argumentTypeArrayEx = [
					new ArgTypeList{ ArgTypes = { ArgType.Int, ArgType.Int }, OmitStart = 1 },
				];
			charaStr = cStr;
			CanRestructure = true;
		}
		public override string GetStrValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			long x = arguments[0].GetIntValue(exm);
			long y = (arguments.Count > 1 && arguments[1] != null) ? arguments[1].GetIntValue(exm) : 0;
			if (!Config.CompatiSPChara && y != 0)
				throw new CodeEE(trerror.SPCharacterFeatureDisabled.Text);
			return exm.VEvaluator.GetCharacterStrfromCSVData(x, charaStr, y != 0, 0);
		}
	}

	private sealed class CsvcstrMethod : FunctionMethod
	{
		public CsvcstrMethod()
		{
			ReturnType = typeof(string);
			argumentTypeArrayEx = [
					new ArgTypeList{ ArgTypes = { ArgType.Int, ArgType.Int, ArgType.Int }, OmitStart = 2 },
				];
			CanRestructure = true;
		}
		public override string GetStrValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			long x = arguments[0].GetIntValue(exm);
			long y = arguments[1].GetIntValue(exm);
			long z = (arguments.Count == 3 && arguments[2] != null) ? arguments[2].GetIntValue(exm) : 0;
			if (!Config.CompatiSPChara && z != 0)
				throw new CodeEE(trerror.SPCharacterFeatureDisabled.Text);
			return exm.VEvaluator.GetCharacterStrfromCSVData(x, CharacterStrData.CSTR, z != 0, y);
		}
	}

	private sealed class CsvDataMethod : FunctionMethod
	{
		readonly CharacterIntData charaInt;
		public CsvDataMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArrayEx = [
					new ArgTypeList{ ArgTypes = { ArgType.Int, ArgType.Int, ArgType.Int }, OmitStart = 2 },
				];
			charaInt = CharacterIntData.BASE;
			CanRestructure = true;
		}
		public CsvDataMethod(CharacterIntData cInt)
		{
			ReturnType = typeof(long);
			argumentTypeArrayEx = [
					new ArgTypeList{ ArgTypes = { ArgType.Int, ArgType.Int, ArgType.Int }, OmitStart = 2 },
				];
			charaInt = cInt;
			CanRestructure = true;
		}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			long x = arguments[0].GetIntValue(exm);
			long y = arguments[1].GetIntValue(exm);
			long z = (arguments.Count == 3 && arguments[2] != null) ? arguments[2].GetIntValue(exm) : 0;
			if (!Config.CompatiSPChara && z != 0)
				throw new CodeEE(trerror.SPCharacterFeatureDisabled.Text);
			return exm.VEvaluator.GetCharacterIntfromCSVData(x, charaInt, z != 0, y);
		}
	}

	private sealed class FindcharaMethod : FunctionMethod
	{
		public FindcharaMethod(bool last)
		{
			ReturnType = typeof(long);
			argumentTypeArrayEx = [
					new ArgTypeList{ ArgTypes = { ArgType.CharacterData | ArgType.Any, ArgType.SameAsFirst, ArgType.Int, ArgType.Int }, OmitStart = 2 },
				];
			CanRestructure = false;
			isLast = last;
		}

		readonly bool isLast;
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			VariableTerm vTerm = (VariableTerm)arguments[0];
			VariableToken varID = vTerm.Identifier;

			long elem = 0;
			if (vTerm.Identifier.IsArray1D)
				elem = vTerm.GetElementInt(1, exm);
			else if (vTerm.Identifier.IsArray2D)
			{
				elem = vTerm.GetElementInt(1, exm) << 32;
				elem += vTerm.GetElementInt(2, exm);
			}
			long startindex = 0;
			long lastindex = exm.VEvaluator.CHARANUM;
			if (arguments.Count >= 3 && arguments[2] != null)
				startindex = arguments[2].GetIntValue(exm);
			if (arguments.Count >= 4 && arguments[3] != null)
				lastindex = arguments[3].GetIntValue(exm);
			if (startindex < 0 || startindex >= exm.VEvaluator.CHARANUM)
				throw new CodeEE(string.Format(trerror.CharacterIndexOutOfRange.Text, Name, 3, startindex));
			if (lastindex < 0 || lastindex > exm.VEvaluator.CHARANUM)
				throw new CodeEE(string.Format(trerror.CharacterIndexOutOfRange.Text, Name, 4, lastindex));
			long ret;
			if (varID.IsString)
			{
				string word = arguments[1].GetStrValue(exm);
				ret = VariableEvaluator.FindChara(varID, elem, word, startindex, lastindex, isLast);
			}
			else
			{
				long word = arguments[1].GetIntValue(exm);
				ret = VariableEvaluator.FindChara(varID, elem, word, startindex, lastindex, isLast);
			}
			return ret;
		}
	}

	private sealed class ExistCsvMethod : FunctionMethod
	{
		public ExistCsvMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArrayEx = [
					new ArgTypeList{ ArgTypes = { ArgType.Int, ArgType.Int }, OmitStart = 1 },
				];
			CanRestructure = true;
		}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			long no = arguments[0].GetIntValue(exm);
			bool isSp = (arguments.Count == 2 && arguments[1] != null) ? (arguments[1].GetIntValue(exm) != 0) : false;
			if (!Config.CompatiSPChara && isSp)
				throw new CodeEE(trerror.SPCharacterFeatureDisabled.Text);

			return exm.VEvaluator.ExistCsv(no, isSp);
		}
	}
}
