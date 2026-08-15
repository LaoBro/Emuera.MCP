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
using System.Text;
using System.Xml;
using trerror = MinorShift.Emuera.Runtime.Utils.EvilMask.Lang.Error;

namespace MinorShift.Emuera.GameData.Function;

internal static partial class FunctionMethodCreator
{
	private sealed class MapManagementMethod : FunctionMethod
	{
		public enum Operation { Create, Check, Release };
		public MapManagementMethod(Operation type)
		{
			ReturnType = typeof(long);
			argumentTypeArray = [typeof(string)];
			CanRestructure = false;
			op = type;
		}
		private Operation op;
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			string key = arguments[0].GetStrValue(exm);
			var dict = exm.VEvaluator.VariableData.DataStringMaps;
			bool contains = dict.ContainsKey(key);
			switch (op)
			{
				case Operation.Check: { return contains ? 1 : 0; }
				case Operation.Release: { if (contains) dict.Remove(key); return 1; }
			}
			if (contains) return 0;
			dict[key] = [];
			return 1;
		}
	}

	private sealed class MapDataOperationMethod : FunctionMethod
	{
		public enum Operation { Set, Has, Remove, Clear, Size };
		public MapDataOperationMethod(Operation type)
		{
			ReturnType = typeof(long);
			switch (type)
			{
				case Operation.Set:
					argumentTypeArray = [typeof(string), typeof(string), typeof(string)]; break;
				case Operation.Has:
				case Operation.Remove:
					argumentTypeArray = [typeof(string), typeof(string)]; break;
				default:
					argumentTypeArray = [typeof(string)]; break;
			}
			CanRestructure = false;
			op = type;
		}
		private Operation op;
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			var map = arguments[0].GetStrValue(exm);
			var dict = exm.VEvaluator.VariableData.DataStringMaps;
			if (!dict.TryGetValue(map, out var sMap)) return -1;
			if (op == Operation.Clear) sMap.Clear();
			else if (op == Operation.Size) return sMap.Count;
			else
			{
				var key = arguments[1].GetStrValue(exm);
				bool contains = sMap.ContainsKey(key);
				if (op == Operation.Has) return contains ? 1 : 0;
				if (op == Operation.Remove)
					sMap.Remove(key);
				else
					sMap[key] = arguments[2].GetStrValue(exm);
			}
			return 1;
		}
	}

	private sealed class MapGetStrMethod : FunctionMethod
	{
		public enum Operation { Get, ToXml, GetKeys };
		public MapGetStrMethod(Operation type)
		{
			ReturnType = typeof(string);
			switch (type)
			{
				case Operation.Get:
					argumentTypeArray = [typeof(string), typeof(string)]; break;
				case Operation.ToXml:
					argumentTypeArray = [typeof(string)]; break;
				case Operation.GetKeys:
					argumentTypeArrayEx = [
							new ArgTypeList{ ArgTypes = { ArgType.String, ArgType.Int }, OmitStart = 1 },
							new ArgTypeList{ ArgTypes = { ArgType.String, ArgType.RefString1D, ArgType.Int } },
						]; break;
			}
			CanRestructure = false;
			op = type;
		}
		private Operation op;
		public override string GetStrValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			var dict = exm.VEvaluator.VariableData.DataStringMaps;
			var map = arguments[0].GetStrValue(exm);
			if (!dict.TryGetValue(map, out var sMap)) return "";
			if (op == Operation.Get)
			{
				var key = arguments[1].GetStrValue(exm);
				if (sMap.TryGetValue(key, out var val)) return val;
				return "";
			}
			else if (op == Operation.GetKeys && arguments.Count > 1)
			{
				int count = 0;
				string[] array;
				if (arguments.Count == 3) // to array
				{
					var Term = (arguments[1] as VariableTerm)!;
					if (arguments[2].GetIntValue(exm) == 0) return "";
					array = (Term.Identifier.GetArray() as string[])!;
				}
				else if (arguments.Count == 2) // to RESULTS array
				{
					if (arguments[1].GetIntValue(exm) == 0) return "";
					array = exm.VEvaluator.RESULTS_ARRAY;
				}
				else return "";
				foreach (var k in sMap.Keys)
				{
					if (count >= array.Length) break;
					array[count] = k;
					count++;
				}
				exm.VEvaluator.RESULT = sMap.Keys.Count;
				return arguments.Count == 2 ? exm.VEvaluator.RESULTS : "";
			}
			StringBuilder sb = new();
			if (op == Operation.GetKeys)
			{
				bool isNotEmpty = false;
				foreach (var k in sMap.Keys)
				{
					if (isNotEmpty) sb.Append(',').Append(k);
					else
					{
						isNotEmpty = true;
						sb.Append(k);
					}
				}
			}
			else
			{
				sb.Append("<map>");
				foreach (var p in sMap)
					sb.Append(string.Format("<p><k>{0}</k><v>{1}</v></p>", p.Key, p.Value));
				sb.Append("</map>");
			}
			return sb.ToString();
		}
	}

	private sealed class MapFromXmlMethod : FunctionMethod
	{
		public MapFromXmlMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArray = [typeof(string), typeof(string)];
			CanRestructure = false;
		}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			var map = arguments[0].GetStrValue(exm);
			var dict = exm.VEvaluator.VariableData.DataStringMaps;
			if (!dict.TryGetValue(map, out var sMap)) return 0;
			var xml = arguments[1].GetStrValue(exm);
			XmlDocument doc = new();
			XmlNodeList nodes;
			try
			{
				doc.LoadXml(xml);
				nodes = doc.SelectNodes("/map/p")!;
			}
			catch (XmlException e)
			{
				throw new CodeEE(string.Format(trerror.XmlParseError.Text, Name, xml, e.Message));
			}
			for (int i = 0; i < nodes.Count; i++)
			{
				XmlNodeList key, val;
				var node = nodes[i];
				key = node!.SelectNodes("./k")!;
				val = node!.SelectNodes("./v")!;
				if (key.Count != 1 || val.Count != 1) continue;
				sMap[key[0]!.InnerText] = val[0]!.InnerXml;
			}
			return 1;
		}
	}
}
