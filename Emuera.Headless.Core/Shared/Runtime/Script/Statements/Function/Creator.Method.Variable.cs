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
	private sealed class MatchMethod : FunctionMethod
	{
		readonly bool isCharaRange;
		public MatchMethod()
		{
			ReturnType = typeof(long);
			// argumentTypeArray = null;
			argumentTypeArrayEx = [
					new ArgTypeList{ ArgTypes = { ArgType.RefAny1D | ArgType.AllowConstRef, ArgType.SameAsFirst, ArgType.Int, ArgType.Int }, OmitStart = 2 },
				];
			isCharaRange = false;
			CanRestructure = false;
			HasUniqueRestructure = true;
		}
		public MatchMethod(bool isChara)
		{
			ReturnType = typeof(long);
			// argumentTypeArray = null;
			argumentTypeArrayEx = [
					//new ArgTypeList{ ArgTypes = { ArgType.CharacterData | ArgType.RefAny1D | ArgType.AllowConstRef | ArgType.Any, ArgType.SameAsFirst, ArgType.Int, ArgType.Int }, OmitStart = 2 },
					new ArgTypeList{ ArgTypes = { ArgType.Any, ArgType.SameAsFirst, ArgType.Int, ArgType.Int }, OmitStart = 2 },
				];
			isCharaRange = isChara;
			CanRestructure = false;
			HasUniqueRestructure = true;
		}
		//public override string CheckArgumentType(string name, IOperandTerm[] arguments)
		//{
		//	if (arguments.Count < 2)
		//		return name + "関数には少なくとも2つの引数が必要です";
		//	if (arguments.Count > 4)
		//		return name + "関数の引数が多すぎます";
		//	if (arguments[0] == null)
		//		return name + "関数の1番目の引数は省略できません";
		//	if (!(arguments[0] is VariableTerm))
		//		return name + "関数の1番目の引数が変数ではありません";
		//	VariableTerm varToken = (VariableTerm)arguments[0];
		//	if (isCharaRange && !varToken.Identifier.IsCharacterData)
		//		return name + "関数の1番目の引数がキャラクタ変数ではありません";
		//	if (!isCharaRange && (varToken.Identifier.IsArray2D || varToken.Identifier.IsArray3D))
		//		return name + "関数は二重配列・三重配列には対応していません";
		//	if (!isCharaRange && !varToken.Identifier.IsArray1D)
		//		return name + "関数の1番目の引数が配列変数ではありません";
		//	if (arguments[1] == null)
		//		return name + "関数の2番目の引数は省略できません";
		//	if (arguments[1].GetOperandType() != arguments[0].GetOperandType())
		//		return name + "関数の1番目の引数と2番目の引数の型が異なります";
		//	if ((arguments.Count >= 3) && (arguments[2] != null) && (arguments[2].GetOperandType() != typeof(Int64)))
		//		return name + "関数の3番目の引数の型が正しくありません";
		//	if ((arguments.Count >= 4) && (arguments[3] != null) && (arguments[3].GetOperandType() != typeof(Int64)))
		//		return name + "関数の4番目の引数の型が正しくありません";
		//	return null!;
		//}

		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			VariableTerm varTerm = (arguments[0] as VariableTerm)!;
			long start = (arguments.Count > 2 && arguments[2] != null) ? arguments[2].GetIntValue(exm) : 0;
			long end = (arguments.Count > 3 && arguments[3] != null) ? arguments[3].GetIntValue(exm) : (isCharaRange ? exm.VEvaluator.CHARANUM : varTerm.GetLength());

			FixedVariableTerm p = varTerm.GetFixedVariableTerm(exm);
			if (!isCharaRange)
			{
				p.IsArrayRangeValid(start, end, "MATCH", 3L, 4L);
				if (arguments[0].GetOperandType() == typeof(long))
				{
					long targetValue = arguments[1].GetIntValue(exm);
					return VariableEvaluator.GetMatch(p, targetValue, start, end);
				}
				else
				{
					string targetStr = arguments[1].GetStrValue(exm);
					return VariableEvaluator.GetMatch(p, targetStr, start, end);
				}
			}
			else
			{
				long charaNum = exm.VEvaluator.CHARANUM;
				if (start >= charaNum || start < 0 || end > charaNum || end < 0)
					// throw new CodeEE("CMATCH関数の範囲指定がキャラクタ配列の範囲を超えています(" + start.ToString() + "～" + end.ToString() + ")");
					throw new CodeEE(string.Format(trerror.CharacterRangeInvalid.Text, Name, start, end));
				if (arguments[0].GetOperandType() == typeof(long))
				{
					long targetValue = arguments[1].GetIntValue(exm);
					return VariableEvaluator.GetMatchChara(p, targetValue, start, end);
				}
				else
				{
					string targetStr = arguments[1].GetStrValue(exm);
					return VariableEvaluator.GetMatchChara(p, targetStr, start, end);
				}
			}
		}

		public override bool UniqueRestructure(ExpressionMediator exm, List<AExpression> arguments)
		{
			arguments[0].Restructure(exm);
			for (int i = 1; i < arguments.Count; i++)
			{
				if (arguments[i] == null)
					continue;
				arguments[i] = arguments[i].Restructure(exm);
			}
			return false;
		}
	}

	private sealed class GroupMatchMethod : FunctionMethod
	{
		public GroupMatchMethod()
		{
			ReturnType = typeof(long);
			// argumentTypeArray = null;
			argumentTypeArrayEx = [
					new ArgTypeList{ ArgTypes = { ArgType.Any, ArgType.VariadicSameAsFirst } },
				];
			CanRestructure = false;
		}
		//public override string CheckArgumentType(string name, IOperandTerm[] arguments)
		//{
		//	if (arguments.Count < 2)
		//		return name + "関数には少なくとも2つの引数が必要です";
		//	if (arguments[0] == null)
		//		return name + "関数の1番目の引数は省略できません";
		//	Type baseType = arguments[0].GetOperandType();
		//	for (int i = 1; i < arguments.Count; i++)
		//	{
		//		if (arguments[i] == null)
		//			return name + "関数の" + (i + 1).ToString() + "番目の引数は省略できません";
		//		if (arguments[i].GetOperandType() != baseType)
		//			return name + "関数の" + (i + 1).ToString() + "番目の引数の型が正しくありません";
		//	}
		//	return null!;
		//}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			long ret = 0;
			if (arguments[0].GetOperandType() == typeof(long))
			{
				long baseValue = arguments[0].GetIntValue(exm);
				for (int i = 1; i < arguments.Count; i++)
				{
					if (baseValue == arguments[i].GetIntValue(exm))
						ret += 1;
				}
			}
			else
			{
				string baseString = arguments[0].GetStrValue(exm);
				for (int i = 1; i < arguments.Count; i++)
				{
					if (baseString == arguments[i].GetStrValue(exm))
						ret += 1;
				}
			}
			return ret;
		}
	}

	private sealed class NosamesMethod : FunctionMethod
	{
		public NosamesMethod()
		{
			ReturnType = typeof(long);
			// argumentTypeArray = null;
			argumentTypeArrayEx = [
					new ArgTypeList{ ArgTypes = { ArgType.Any, ArgType.VariadicSameAsFirst } },
				];
			CanRestructure = false;
		}
		//public override string CheckArgumentType(string name, IOperandTerm[] arguments)
		//{
		//	if (arguments.Count < 2)
		//		return name + "関数には少なくとも2つの引数が必要です";
		//	if (arguments[0] == null)
		//		return name + "関数の1番目の引数は省略できません";
		//	Type baseType = arguments[0].GetOperandType();
		//	for (int i = 1; i < arguments.Count; i++)
		//	{
		//		if (arguments[i] == null)
		//			return name + "関数の" + (i + 1).ToString() + "番目の引数は省略できません";
		//		if (arguments[i].GetOperandType() != baseType)
		//			return name + "関数の" + (i + 1).ToString() + "番目の引数の型が正しくありません";
		//	}
		//	return null!;
		//}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			if (arguments[0].GetOperandType() == typeof(long))
			{
				long[] valueArray = new long[arguments.Count];
				for (int i = 0; i < arguments.Count; i++)
				{
					valueArray[i] = arguments[i].GetIntValue(exm);
				}
				var resultArray = valueArray.Distinct();
				if (resultArray.Count() != arguments.Count)
					return 0L;
			}
			else
			{
				string[] stringArray = new string[arguments.Count];
				for (int i = 0; i < arguments.Count; i++)
				{
					stringArray[i] = arguments[i].GetStrValue(exm);
				}
				var resultArray = stringArray.Distinct();
				if (resultArray.Count() != arguments.Count)
					return 0L;
			}
			return 1L;
		}
	}

	private sealed class AllsamesMethod : FunctionMethod
	{
		public AllsamesMethod()
		{
			ReturnType = typeof(long);
			// argumentTypeArray = null;
			argumentTypeArrayEx = [
					new ArgTypeList{ ArgTypes = { ArgType.Any, ArgType.VariadicSameAsFirst } },
				];
			CanRestructure = false;
		}
		//public override string CheckArgumentType(string name, IOperandTerm[] arguments)
		//{
		//	if (arguments.Count < 2)
		//		return name + "関数には少なくとも2つの引数が必要です";
		//	if (arguments[0] == null)
		//		return name + "関数の1番目の引数は省略できません";
		//	Type baseType = arguments[0].GetOperandType();
		//	for (int i = 1; i < arguments.Count; i++)
		//	{
		//		if (arguments[i] == null)
		//			return name + "関数の" + (i + 1).ToString() + "番目の引数は省略できません";
		//		if (arguments[i].GetOperandType() != baseType)
		//			return name + "関数の" + (i + 1).ToString() + "番目の引数の型が正しくありません";
		//	}
		//	return null!;
		//}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			if (arguments[0].GetOperandType() == typeof(long))
			{
				long baseValue = arguments[0].GetIntValue(exm);
				for (int i = 1; i < arguments.Count; i++)
				{
					if (baseValue != arguments[i].GetIntValue(exm))
						return 0L;
				}
			}
			else
			{
				string baseValue = arguments[0].GetStrValue(exm);
				for (int i = 1; i < arguments.Count; i++)
				{
					if (baseValue != arguments[i].GetStrValue(exm))
						return 0L;
				}
			}
			return 1L;
		}
	}

	private sealed class GetbitMethod : FunctionMethod
	{
		public GetbitMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArray = [typeof(long), typeof(long)];
			CanRestructure = true;
		}
		//public override string CheckArgumentType(string name, IOperandTerm[] arguments)
		//{
		//	string ret = base.CheckArgumentType(name, arguments);
		//	if (ret != null)
		//		return ret;
		//	if (arguments[1] is SingleTerm)
		//	{
		//		Int64 m = ((SingleTerm)arguments[1]).Int;
		//		if (m < 0 || m > 63)
		//			return "GETBIT関数の第２引数(" + m.ToString() + ")が範囲(０～６３)を超えています";
		//	}
		//	return null!;
		//}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			long n = arguments[0].GetIntValue(exm);
			long m = arguments[1].GetIntValue(exm);
			if ((m < 0) || (m > 63))
				// throw new CodeEE("GETBIT関数の第２引数(" + m.ToString() + ")が範囲(０～６３)を超えています");
				throw new CodeEE(string.Format(trerror.ArgIsOutOfRange.Text, Name, 2, m, 0, 63));
			int mi = (int)m;
			return (n >> mi) & 1;
		}
	}

	private sealed class GetnumMethod : FunctionMethod
	{
		public GetnumMethod()
		{
			ReturnType = typeof(long);
			// argumentTypeArray = null;
			argumentTypeArrayEx = [
					new ArgTypeList{ ArgTypes = { ArgType.RefAny | ArgType.AllowConstRef, ArgType.String, ArgType.Int }, OmitStart = 2 },
				];
			CanRestructure = true;
			HasUniqueRestructure = true;
		}
		//public override string CheckArgumentType(string name, IOperandTerm[] arguments)
		//{
		//	if (arguments.Count != 2)
		//		return name + "関数には2つの引数が必要です";
		//	if (arguments[0] == null)
		//		return name + "関数の1番目の引数は省略できません";
		//	if (!(arguments[0] is VariableTerm))
		//		return name + "関数の1番目の引数の型が正しくありません";
		//	if (arguments[1] == null)
		//		return name + "関数の2番目の引数は省略できません";
		//	if (arguments[1].GetOperandType() != typeof(string))
		//		return name + "関数の2番目の引数の型が正しくありません";
		//	return null!;
		//}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			VariableTerm vToken = (VariableTerm)arguments[0];
			VariableCode varCode = vToken.Identifier.Code;
			string varname = "";
			#region EE_ERD
			if (arguments.Count > 2)
				varname = vToken.Identifier.Name + "@" + arguments[2].GetIntValue(exm);
			else
				varname = vToken.Identifier.Name;
			#endregion
			string key = arguments[1].GetStrValue(exm);
			#region EE_ERD
			// if (exm.VEvaluator.Constant.TryKeywordToInteger(out int ret, varCode, key, -1))
			if (exm.VEvaluator.Constant.TryKeywordToInteger(out int ret, varCode, key, -1, varname))
				#endregion
				return ret;
			else
				return -1;
		}
		public override bool UniqueRestructure(ExpressionMediator exm, List<AExpression> arguments)
		{
			arguments[1] = arguments[1].Restructure(exm);
			return arguments[1] is SingleTerm;
		}
	}

	private sealed class GetnumBMethod : FunctionMethod
	{
		public GetnumBMethod()
		{
			ReturnType = typeof(Int64);
			argumentTypeArray = new Type[] { typeof(string), typeof(string) };
			CanRestructure = true;
		}
		/*
		public override string CheckArgumentType(string name, IOperandTerm[] arguments)
		{
			string errStr = base.CheckArgumentType(name, arguments);
			if (errStr != null)
				return errStr;
			if (arguments[0] == null)
				return name + "関数の1番目の引数は省略できません";
			if (arguments[0] is SingleTerm)
			{
				string varName = ((SingleTerm)arguments[0]).Text;
				if (GlobalStatic.IdentifierDictionary.GetVariableToken(varName, null, true) == null)
					return name + "関数の1番目の引数が変数名ではありません";
			}
			return null!;
		}
		*/
		public override Int64 GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			VariableToken var = GlobalStatic.IdentifierDictionary.GetVariableToken(arguments[0].GetStrValue(exm), null!, true);
			if (var == null)
				throw new CodeEE("GETNUMBの1番目の引数(\"" + arguments[0].GetStrValue(exm) + "\")が変数名ではありません");
			string key = arguments[1].GetStrValue(exm);
			#region EE_ERD
			//GETNUMBは使ってないのでテストしていない
			// if (exm.VEvaluator.Constant.TryKeywordToInteger(out int ret, var.Code, key, -1))
			if (exm.VEvaluator.Constant.TryKeywordToInteger(out int ret, var.Code, key, -1, arguments[0].GetStrValue(exm)))
			#endregion
				return ret;
			else
				return -1;
		}
	}

	private sealed class GetPalamLVMethod : FunctionMethod
	{
		public GetPalamLVMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArray = [typeof(long), typeof(long)];
			CanRestructure = false;
		}
		//public override string CheckArgumentType(string name, IOperandTerm[] arguments)
		//{
		//	string errStr = base.CheckArgumentType(name, arguments);
		//	if (errStr != null)
		//		return errStr;
		//	if (arguments[0] == null)
		//		return name + "関数の1番目の引数は省略できません";
		//	return null!;
		//}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			long value = arguments[0].GetIntValue(exm);
			long maxLv = arguments[1].GetIntValue(exm);

			return exm.VEvaluator.getPalamLv(value, maxLv);
		}
	}

	private sealed class GetExpLVMethod : FunctionMethod
	{
		public GetExpLVMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArray = [typeof(long), typeof(long)];
			CanRestructure = false;
		}
		//public override string CheckArgumentType(string name, IOperandTerm[] arguments)
		//{
		//	string errStr = base.CheckArgumentType(name, arguments);
		//	if (errStr != null)
		//		return errStr;
		//	if (arguments[0] == null)
		//		return name + "関数の1番目の引数は省略できません";
		//	return null!;
		//}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			long value = arguments[0].GetIntValue(exm);
			long maxLv = arguments[1].GetIntValue(exm);

			return exm.VEvaluator.getExpLv(value, maxLv);
		}
	}
}
