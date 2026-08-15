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
			// argumentTypeArray = null;
			argumentTypeArrayEx = [
					new ArgTypeList{ ArgTypes = { ArgType.Int, ArgType.Int }, OmitStart = 1 },
				];
			CanRestructure = false;
		}

		//public override string CheckArgumentType(string name, IOperandTerm[] arguments)
		//{
		//	//通常２つ、１つ省略可能で１～２の引数が必要。
		//	if (arguments.Count < 1)
		//		return name + "関数には少なくとも1つの引数が必要です";
		//	if (arguments.Count > 2)
		//		return name + "関数の引数が多すぎます";

		//	if (arguments[0] == null)
		//		return name + "関数の1番目の引数は省略できません";
		//	if (arguments[0].GetOperandType() != typeof(Int64))
		//		return name + "関数の1番目の引数の型が正しくありません";
		//	//2は省略可能
		//	if ((arguments.Count == 2) && (arguments[1] != null) && (arguments[1].GetOperandType() != typeof(Int64)))
		//		return name + "関数の2番目の引数の型が正しくありません";
		//	return null!;
		//}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			long integer = arguments[0].GetIntValue(exm);
			if (!Config.CompatiSPChara)
			{
				//if ((arguments.Count > 1) && (arguments[1] != null) && (arguments[1].GetIntValue(exm) != 0))
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
				// throw new CodeEE("SPキャラ関係の機能は標準では使用できません(互換性オプション「SPキャラを使用する」をONにしてください)");
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
			// argumentTypeArray = null;
			argumentTypeArrayEx = [
					new ArgTypeList{ ArgTypes = { ArgType.Int, ArgType.Int }, OmitStart = 1 },
				];
			charaStr = cStr;
			CanRestructure = true;
		}
		//public override string CheckArgumentType(string name, IOperandTerm[] arguments)
		//{
		//	if (arguments.Count < 1)
		//		return name + "関数には少なくとも1つの引数が必要です";
		//	if (arguments.Count > 2)
		//		return name + "関数の引数が多すぎます";
		//	if (arguments[0] == null)
		//		return name + "関数の1番目の引数は省略できません";
		//	if (!arguments[0].IsInteger)
		//		return name + "関数の1番目の引数が数値ではありません";
		//	if (arguments.Count == 1)
		//		return null!;
		//	if ((arguments[1] != null) && (arguments[1].GetOperandType() != typeof(Int64)))
		//		return name + "関数の2番目の変数が数値ではありません";
		//	return null!;
		//}
		public override string GetStrValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			long x = arguments[0].GetIntValue(exm);
			long y = (arguments.Count > 1 && arguments[1] != null) ? arguments[1].GetIntValue(exm) : 0;
			if (!Config.CompatiSPChara && y != 0)
				// throw new CodeEE("SPキャラ関係の機能は標準では使用できません(互換性オプション「SPキャラを使用する」をONにしてください)");
				throw new CodeEE(trerror.SPCharacterFeatureDisabled.Text);
			return exm.VEvaluator.GetCharacterStrfromCSVData(x, charaStr, y != 0, 0);
		}
	}

	private sealed class CsvcstrMethod : FunctionMethod
	{
		public CsvcstrMethod()
		{
			ReturnType = typeof(string);
			// argumentTypeArray = null;
			argumentTypeArrayEx = [
					new ArgTypeList{ ArgTypes = { ArgType.Int, ArgType.Int, ArgType.Int }, OmitStart = 2 },
				];
			CanRestructure = true;
		}
		//public override string CheckArgumentType(string name, IOperandTerm[] arguments)
		//{
		//	if (arguments.Count < 2)
		//		return name + "関数には少なくとも2つの引数が必要です";
		//	if (arguments.Count > 3)
		//		return name + "関数の引数が多すぎます";
		//	if (arguments[0] == null)
		//		return name + "関数の1番目の引数は省略できません";
		//	if (!arguments[0].IsInteger)
		//		return name + "関数の1番目の引数が数値ではありません";
		//	if (arguments[1] == null)
		//		return name + "関数の2番目の引数は省略できません";
		//	if (arguments[1].GetOperandType() != typeof(Int64))
		//		return name + "関数の2番目の変数が数値ではありません";
		//	if (arguments.Count == 2)
		//		return null!;
		//	if ((arguments[2] != null) && (arguments[2].GetOperandType() != typeof(Int64)))
		//		return name + "関数の3番目の変数が数値ではありません";
		//	return null!;
		//}
		public override string GetStrValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			long x = arguments[0].GetIntValue(exm);
			long y = arguments[1].GetIntValue(exm);
			long z = (arguments.Count == 3 && arguments[2] != null) ? arguments[2].GetIntValue(exm) : 0;
			if (!Config.CompatiSPChara && z != 0)
				// throw new CodeEE("SPキャラ関係の機能は標準では使用できません(互換性オプション「SPキャラを使用する」をONにしてください)");
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
			// argumentTypeArray = null;
			argumentTypeArrayEx = [
					new ArgTypeList{ ArgTypes = { ArgType.Int, ArgType.Int, ArgType.Int }, OmitStart = 2 },
				];
			charaInt = CharacterIntData.BASE;
			CanRestructure = true;
		}
		public CsvDataMethod(CharacterIntData cInt)
		{
			ReturnType = typeof(long);
			// argumentTypeArray = null;
			argumentTypeArrayEx = [
					new ArgTypeList{ ArgTypes = { ArgType.Int, ArgType.Int, ArgType.Int }, OmitStart = 2 },
				];
			charaInt = cInt;
			CanRestructure = true;
		}
		//public override string CheckArgumentType(string name, IOperandTerm[] arguments)
		//{
		//	if (arguments.Count < 2)
		//		return name + "関数には少なくとも2つの引数が必要です";
		//	if (arguments.Count > 3)
		//		return name + "関数の引数が多すぎます";
		//	if (arguments[0] == null)
		//		return name + "関数の1番目の引数は省略できません";
		//	if (!arguments[0].IsInteger)
		//		return name + "関数の1番目の引数が数値ではありません";
		//	if (arguments[1] == null)
		//		return name + "関数の2番目の引数は省略できません";
		//	if (arguments[1].GetOperandType() != typeof(Int64))
		//		return name + "関数の2番目の変数が数値ではありません";
		//	if (arguments.Count == 2)
		//		return null!;
		//	if ((arguments[2] != null) && (arguments[2].GetOperandType() != typeof(Int64)))
		//		return name + "関数の3番目の変数が数値ではありません";
		//	return null!;
		//}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			long x = arguments[0].GetIntValue(exm);
			long y = arguments[1].GetIntValue(exm);
			long z = (arguments.Count == 3 && arguments[2] != null) ? arguments[2].GetIntValue(exm) : 0;
			if (!Config.CompatiSPChara && z != 0)
				// throw new CodeEE("SPキャラ関係の機能は標準では使用できません(互換性オプション「SPキャラを使用する」をONにしてください)");
				throw new CodeEE(trerror.SPCharacterFeatureDisabled.Text);
			return exm.VEvaluator.GetCharacterIntfromCSVData(x, charaInt, z != 0, y);
		}
	}

	private sealed class FindcharaMethod : FunctionMethod
	{
		public FindcharaMethod(bool last)
		{
			ReturnType = typeof(long);
			// argumentTypeArray = null;
			argumentTypeArrayEx = [
					new ArgTypeList{ ArgTypes = { ArgType.CharacterData | ArgType.Any, ArgType.SameAsFirst, ArgType.Int, ArgType.Int }, OmitStart = 2 },
				];
			CanRestructure = false;
			isLast = last;
		}

		readonly bool isLast;
		//public override string CheckArgumentType(string name, IOperandTerm[] arguments)
		//{
		//	//通常3つ、1つ省略可能で2～3の引数が必要。
		//	if (arguments.Count < 2)
		//		return name + "関数には少なくとも2つの引数が必要です";
		//	if (arguments.Count > 4)
		//		return name + "関数の引数が多すぎます";

		//	if (arguments[0] == null)
		//		return name + "関数の1番目の引数は省略できません";
		//	if (!(arguments[0] is VariableTerm))
		//		return name + "関数の1番目の引数の型が正しくありません";
		//	if (!(((VariableTerm)arguments[0]).Identifier.IsCharacterData))
		//		return name + "関数の1番目の引数の変数がキャラクタ変数ではありません";
		//	if (arguments[1] == null)
		//		return name + "関数の2番目の引数は省略できません";
		//	if (arguments[1].GetOperandType() != arguments[0].GetOperandType())
		//		return name + "関数の2番目の引数の型が正しくありません";
		//	//3番目は省略可能
		//	if ((arguments.Count >= 3) && (arguments[2] != null) && (arguments[2].GetOperandType() != typeof(Int64)))
		//		return name + "関数の3番目の引数の型が正しくありません";
		//	//4番目は省略可能
		//	if ((arguments.Count >= 4) && (arguments[3] != null) && (arguments[3].GetOperandType() != typeof(Int64)))
		//		return name + "関数の4番目の引数の型が正しくありません";
		//	return null!;
		//}
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
				// throw new CodeEE((isLast ? "" : "") + "関数の第3引数(" + startindex.ToString() + ")はキャラクタ位置の範囲外です");
				throw new CodeEE(string.Format(trerror.CharacterIndexOutOfRange.Text, Name, 3, startindex));
			if (lastindex < 0 || lastindex > exm.VEvaluator.CHARANUM)
				// throw new CodeEE((isLast ? "" : "") + "関数の第4引数(" + lastindex.ToString() + ")はキャラクタ位置の範囲外です");
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
			// argumentTypeArray = null;
			argumentTypeArrayEx = [
					new ArgTypeList{ ArgTypes = { ArgType.Int, ArgType.Int }, OmitStart = 1 },
				];
			CanRestructure = true;
		}
		//public override string CheckArgumentType(string name, IOperandTerm[] arguments)
		//{
		//	if (arguments.Count < 1)
		//		return name + "関数には少なくとも1つの引数が必要です";
		//	if (arguments.Count > 2)
		//		return name + "関数の引数が多すぎます";
		//	if (arguments[0] == null)
		//		return name + "関数の1番目の引数は省略できません";
		//	if (!arguments[0].IsInteger)
		//		return name + "関数の1番目の引数が数値ではありません";
		//	if (arguments.Count == 1)
		//		return null!;
		//	if ((arguments[1] != null) && (arguments[1].GetOperandType() != typeof(Int64)))
		//		return name + "関数の2番目の変数が数値ではありません";
		//	return null!;
		//}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			long no = arguments[0].GetIntValue(exm);
			bool isSp = (arguments.Count == 2 && arguments[1] != null) ? (arguments[1].GetIntValue(exm) != 0) : false;
			if (!Config.CompatiSPChara && isSp)
				// throw new CodeEE("SPキャラ関係の機能は標準では使用できません(互換性オプション「SPキャラを使用する」をONにしてください)");
				throw new CodeEE(trerror.SPCharacterFeatureDisabled.Text);

			return exm.VEvaluator.ExistCsv(no, isSp);
		}
	}
}
