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
using System.Xml;
using trerror = MinorShift.Emuera.Runtime.Utils.EvilMask.Lang.Error;

namespace MinorShift.Emuera.GameData.Function;

internal static partial class FunctionMethodCreator
{
	private sealed class XmlGetMethod : FunctionMethod
	{
		public XmlGetMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArrayEx = [
					new ArgTypeList{ ArgTypes = { ArgType.Any, ArgType.String, ArgType.Int, ArgType.Int}, OmitStart = 2 },
					new ArgTypeList{ ArgTypes = { ArgType.Any, ArgType.String, ArgType.RefString1D, ArgType.Int}, OmitStart = 3 },
				];
			CanRestructure = false;
		}
		public XmlGetMethod(bool byname) : this()
		{
			byName = byname;
			argumentTypeArrayEx = [
					new ArgTypeList{ ArgTypes = { ArgType.Any, ArgType.String, ArgType.Int, ArgType.Int}, OmitStart = 2 },
					new ArgTypeList{ ArgTypes = { ArgType.Any, ArgType.String, ArgType.RefString1D, ArgType.Int}, OmitStart = 3 },
				];
		}
		private bool byName;
		private static void OutPutNode(XmlNode node, string[] array, int i, long style)
		{
			switch (style)
			{
				case 1: array[i] = node.InnerText; break;
				case 2: array[i] = node.InnerXml; break;
				case 3: array[i] = node.OuterXml; break;
				case 4: array[i] = node.Name; break;
				default: array[i] = node.Value!; break;
			}
		}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			XmlDocument doc = null!;
			XmlNodeList nodes = null!;
			if (arguments[0].GetOperandType() == typeof(long) || (byName && arguments[0].GetOperandType() == typeof(string)))
			{
				var idx = arguments[0].GetOperandType() == typeof(string) ? arguments[0].GetStrValue(exm) : arguments[0].GetIntValue(exm).ToString();
				var dict = exm.VEvaluator.VariableData.DataXmlDocument;
				if (dict.TryGetValue(idx, out var temp)) doc = temp!;
				else return -1;
			}
			else
			{
				doc = new XmlDocument();
				var xml = arguments[0].GetStrValue(exm);
				try
				{
					doc.LoadXml(xml);
				}
				catch (XmlException e)
				{
					throw new CodeEE(string.Format(trerror.XmlGetError.Text, xml, e.Message));
				}
			}
			string path = arguments[1].GetStrValue(exm);
			try
			{
				nodes = doc.SelectNodes(path)!;
			}
			catch (System.Xml.XPath.XPathException e)
			{
				throw new CodeEE(string.Format(trerror.XmlGetPathError.Text, path, e.Message));
			}
			long outputStyle = arguments.Count == 4 ? arguments[3].GetIntValue(exm) : 0;

			if (arguments.Count >= 3)
			{
				if (arguments[2].GetOperandType() == typeof(long) && arguments[2].GetIntValue(exm) != 0)
				{
					for (int i = 0; i < Math.Min(nodes.Count, exm.VEvaluator.RESULTS_ARRAY.Length); i++)
						OutPutNode(nodes[i]!, exm.VEvaluator.RESULTS_ARRAY, i, outputStyle);
				}
				else
				{
					var arr = ((arguments[2] as VariableTerm)!.Identifier.GetArray() as string[])!;
					for (int i = 0; i < Math.Min(nodes.Count, arr.Length); i++)
						OutPutNode(nodes[i]!, arr, i, outputStyle);
				}
			}
			return nodes.Count;
		}
	}

	private sealed class XmlDocumentMethod : FunctionMethod
	{
		public enum Operation { Create, Check, Release };
		public XmlDocumentMethod(Operation type)
		{
			op = type;
			ReturnType = typeof(long);
			if (op == Operation.Create)
				argumentTypeArrayEx = [
						new ArgTypeList{ ArgTypes = { ArgType.Any, ArgType.String } },
					];
			else
				argumentTypeArrayEx = [
						new ArgTypeList{ ArgTypes = { ArgType.Any } },
					];
			CanRestructure = false;
		}
		private Operation op;
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			string idx = arguments[0].GetOperandType() == typeof(string) ? arguments[0].GetStrValue(exm) : arguments[0].GetIntValue(exm).ToString();
			var xmlDict = exm.VEvaluator.VariableData.DataXmlDocument;
			if (op == Operation.Create)
			{
				string xml = arguments[1].GetStrValue(exm);
				if (xmlDict.ContainsKey(idx))
				{
					return 0;
				}
				XmlDocument doc = new();
				try
				{
					doc.LoadXml(xml);
				}
				catch (XmlException e)
				{
					throw new CodeEE(string.Format(trerror.XmlGetError.Text, xml, e.Message));
				}
				xmlDict.Add(idx, doc);
			}
			else
			{
				if (xmlDict.ContainsKey(idx))
				{
					if (op == Operation.Check) return 1;
					xmlDict.Remove(idx);
				}
				else return 0;
			}
			return 1;
		}
	}

	private sealed class XmlSetMethod : FunctionMethod
	{
		public XmlSetMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArrayEx = [
					new ArgTypeList{ ArgTypes = { ArgType.Int, ArgType.String, ArgType.String, ArgType.Int, ArgType.Int }, OmitStart = 3 },
					new ArgTypeList{ ArgTypes = { ArgType.RefString, ArgType.String, ArgType.String, ArgType.Int, ArgType.Int }, OmitStart = 3 },
				];
			CanRestructure = false;
		}
		public XmlSetMethod(bool byname) : this()
		{
			byName = byname;
			argumentTypeArrayEx = [
					new ArgTypeList{ ArgTypes = { ArgType.String, ArgType.String, ArgType.String, ArgType.Int, ArgType.Int }, OmitStart = 3 },
				];
		}
		private bool byName;
		private static void SetNode(XmlNode node, string val, long style)
		{
			switch (style)
			{
				case 1: node.InnerText = val; break;
				case 2: node.InnerXml = val; break;
				default: node.Value = val; break;
			}
		}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			XmlDocument doc;
			bool saveToArg0 = true;
			if (arguments[0].GetOperandType() == typeof(long) || (byName && arguments[0].GetOperandType() == typeof(string)))
			{
				saveToArg0 = false;
				var idx = arguments[0].GetOperandType() == typeof(string) ? arguments[0].GetStrValue(exm) : arguments[0].GetIntValue(exm).ToString();
				var dict = exm.VEvaluator.VariableData.DataXmlDocument;
				if (dict.TryGetValue(idx, out var temp)) doc = temp!;
				else return -1;
			}
			else
			{
				string xml = arguments[0].GetStrValue(exm);
				doc = new XmlDocument();
				try
				{
					doc.LoadXml(xml);
				}
				catch (XmlException e)
				{
					throw new CodeEE(string.Format(trerror.XmlParseError.Text, Name, xml, e.Message));
				}
			}

			string path = arguments[1].GetStrValue(exm);
			XmlNodeList nodes = null!;
			try
			{
				nodes = doc.SelectNodes(path)!;
			}
			catch (System.Xml.XPath.XPathException e)
			{
				throw new CodeEE(string.Format(trerror.XmlXPathParseError.Text, Name, path, e.Message));
			}
			bool setAllNodes = arguments.Count >= 4 ? arguments[3].GetIntValue(exm) != 0 : false;
			var style = arguments.Count == 5 ? arguments[4].GetIntValue(exm) : 0;
			if (style > 2 || style < 0) style = 0;
			var val = arguments[2].GetStrValue(exm);
			if (nodes.Count > 0)
			{
				if (nodes.Count != 1)
				{
					if (setAllNodes)
						for (int i = 0; i < nodes.Count; i++) SetNode(nodes[i]!, val, style);
				}
				else SetNode(nodes[0]!, val, style);
				if (saveToArg0)
				{
					(arguments[0] as VariableTerm)!.SetValue(doc.OuterXml, exm);
				}
			}
			return nodes.Count;
		}
	}

	private sealed class XmlToStrMethod : FunctionMethod
	{
		public XmlToStrMethod()
		{
			ReturnType = typeof(string);
			argumentTypeArrayEx = [
					new ArgTypeList{ ArgTypes = { ArgType.Any } },
				];
			CanRestructure = false;
		}
		public override string GetStrValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			string idx = arguments[0].GetOperandType() == typeof(string) ? arguments[0].GetStrValue(exm) : arguments[0].GetIntValue(exm).ToString();
			var xmlDict = exm.VEvaluator.VariableData.DataXmlDocument;
			if (!xmlDict.TryGetValue(idx, out var doc)) return string.Empty;
			return doc.OuterXml;
		}
	}

	private sealed class XmlAddNodeMethod : FunctionMethod
	{
		public enum Operation { Node, Attribute };
		public XmlAddNodeMethod(Operation op)
		{
			ReturnType = typeof(long);
			if (op == Operation.Node)
				argumentTypeArrayEx = [
						new ArgTypeList{ ArgTypes = { ArgType.Int, ArgType.String, ArgType.String, ArgType.Int, ArgType.Int }, OmitStart = 3 },
						new ArgTypeList{ ArgTypes = { ArgType.RefString, ArgType.String, ArgType.String, ArgType.Int, ArgType.Int }, OmitStart = 3 }
					];
			else
				argumentTypeArrayEx = [
						new ArgTypeList{ ArgTypes = { ArgType.Int, ArgType.String, ArgType.String, ArgType.String, ArgType.Int, ArgType.Int }, OmitStart = 3 },
						new ArgTypeList{ ArgTypes = { ArgType.RefString, ArgType.String, ArgType.String, ArgType.String, ArgType.Int, ArgType.Int }, OmitStart = 3 }
					];
			CanRestructure = false;
			this.op = op;
		}
		public XmlAddNodeMethod(Operation op, bool byname) : this(op)
		{
			byName = byname;
			if (op == Operation.Node)
				argumentTypeArrayEx = [
						new ArgTypeList{ ArgTypes = { ArgType.String, ArgType.String, ArgType.String, ArgType.Int, ArgType.Int }, OmitStart = 3 },
					];
			else
				argumentTypeArrayEx = [
						new ArgTypeList{ ArgTypes = { ArgType.String, ArgType.String, ArgType.String, ArgType.String, ArgType.Int, ArgType.Int }, OmitStart = 3 },
					];
		}
		private bool byName;
		Operation op;
		bool Insert(XmlNode node, XmlNode child, int method)
		{
			if (op == Operation.Node)
			{
				switch (method)
				{
					case 0: node.AppendChild(child); break;
					case 1:
						if (node.ParentNode == null) return false;
						node.ParentNode.InsertBefore(child, node);
						break;
					case 2:
						if (node.ParentNode == null) return false;
						node.ParentNode.InsertAfter(child, node);
						break;
				}
				return true;
			}
			else
			{
				if (child is XmlAttribute newAttr)
				{
					XmlAttribute attr;
					if (method > 0 && !(node is XmlAttribute)) return false;
					attr = (method == 0 ? null : node as XmlAttribute)!;
					switch (method)
					{
						case 0: node.Attributes!.Append(newAttr); break;
						case 1: attr!.OwnerElement!.Attributes.InsertBefore(newAttr, attr); break;
						case 2: attr!.OwnerElement!.Attributes.InsertAfter(newAttr, attr); break;
					}
					return true;
				}
			}
			return false;
		}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			XmlDocument doc;
			int methodPos = op == Operation.Node ? 4 : 5;
			int method = arguments.Count >= methodPos ? (int)arguments[methodPos - 1].GetIntValue(exm) : 0;
			if (method > 2 || method < 0) method = 0;
			bool saveToArg0 = true;
			if (arguments[0].GetOperandType() == typeof(long) || (byName && arguments[0].GetOperandType() == typeof(string)))
			{
				saveToArg0 = false;
				var idx = arguments[0].GetOperandType() == typeof(string) ? arguments[0].GetStrValue(exm) : arguments[0].GetIntValue(exm).ToString();
				var dict = exm.VEvaluator.VariableData.DataXmlDocument;
				if (dict.TryGetValue(idx, out var temp)) doc = temp!;
				else return -1;
			}
			else
			{
				string xml = arguments[0].GetStrValue(exm);
				doc = new XmlDocument();
				try
				{
					doc.LoadXml(xml);
				}
				catch (XmlException e)
				{
					throw new CodeEE(string.Format(trerror.XmlParseError.Text, Name, xml, e.Message));
				}
			}

			string path = arguments[1].GetStrValue(exm);
			XmlNodeList nodes;
			try
			{
				nodes = doc.SelectNodes(path)!;
			}
			catch (System.Xml.XPath.XPathException e)
			{
				throw new CodeEE(string.Format(trerror.XmlXPathParseError.Text, Name, path, e.Message));
			}
			if (nodes.Count > 0)
			{
				int setAllPos = op == Operation.Node ? 5 : 6;
				bool setAllNodes = arguments.Count == setAllPos ? arguments[setAllPos - 1].GetIntValue(exm) != 0 : false;
				XmlNode child;
				if (op == Operation.Node)
				{
					var childNode = new XmlDocument();
					var xml = arguments[2].GetStrValue(exm);
					try
					{
						childNode.LoadXml(xml);
					}
					catch (XmlException e)
					{
						throw new CodeEE(string.Format(trerror.XmlParseError.Text, Name, xml, e.Message));
					}
					var newNode = childNode.DocumentElement!;
					child = doc.CreateNode(newNode.NodeType, newNode.Name, newNode.NamespaceURI)!;
					for (int i = 0; i < newNode.Attributes.Count; i++)
					{
						var xattr = newNode.Attributes![i]!;
						var attr = doc.CreateAttribute(xattr.Name);
						attr.Value = xattr.Value;
						child.Attributes!.Append(attr);
					}
					child.InnerXml = newNode.InnerXml;
				}
				else
				{
					child = doc.CreateAttribute(arguments[2].GetStrValue(exm));
					if (arguments.Count >= 4) child.Value = arguments[3].GetStrValue(exm);
				}
				if (nodes.Count != 1)
				{
					if (setAllNodes)
						for (int i = 0; i < nodes.Count; i++) Insert(nodes[i]!, child, method);
				}
				else if (!Insert(nodes[0]!, child, method) && method > 0) return 0;
				if (saveToArg0)
				{
					(arguments[0] as VariableTerm)!.SetValue(doc.OuterXml, exm);
				}
			}
			return nodes.Count;
		}
	}

	private sealed class XmlRemoveNodeMethod : FunctionMethod
	{
		public enum Operation { Node, Attribute };
		public XmlRemoveNodeMethod(Operation op)
		{
			ReturnType = typeof(long);
			argumentTypeArrayEx = [
						new ArgTypeList{ ArgTypes = { ArgType.Int, ArgType.String, ArgType.Int }, OmitStart = 2 },
						new ArgTypeList{ ArgTypes = { ArgType.RefString, ArgType.String, ArgType.Int }, OmitStart = 2 }
					];
			CanRestructure = false;
			this.op = op;
		}
		public XmlRemoveNodeMethod(Operation op, bool byname) : this(op)
		{
			byName = byname;
			argumentTypeArrayEx = [
						new ArgTypeList{ ArgTypes = { ArgType.String, ArgType.String, ArgType.Int }, OmitStart = 2 },
					];
		}
		private bool byName;
		Operation op;
		bool Remove(XmlNode node)
		{
			if (op == Operation.Attribute)
			{
				if (node is XmlAttribute attr)
				{
					attr.OwnerElement!.Attributes.Remove(attr);
					return true;
				}
			}
			else
			{
				if (node.ParentNode != null)
				{
					var parent = node.ParentNode;
					node.ParentNode.RemoveChild(node);
					return true;
				}
			}
			return false;
		}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			XmlDocument doc;
			int method = arguments.Count >= 4 ? (int)arguments[3].GetIntValue(exm) : 0;
			if (method > 2 || method < 0) method = 0;
			bool saveToArg0 = true;
			if (arguments[0].GetOperandType() == typeof(long) || (byName && arguments[0].GetOperandType() == typeof(string)))
			{
				saveToArg0 = false;
				var idx = arguments[0].GetOperandType() == typeof(string) ? arguments[0].GetStrValue(exm) : arguments[0].GetIntValue(exm).ToString();
				var dict = exm.VEvaluator.VariableData.DataXmlDocument;
				if (dict.TryGetValue(idx, out var temp)) doc = temp!;
				else return -1;
			}
			else
			{
				string xml = arguments[0].GetStrValue(exm);
				doc = new XmlDocument();
				try
				{
					doc.LoadXml(xml);
				}
				catch (XmlException e)
				{
					throw new CodeEE(string.Format(trerror.XmlParseError.Text, Name, xml, e.Message));
				}
			}

			string path = arguments[1].GetStrValue(exm);
			XmlNodeList nodes;
			try
			{
				nodes = doc.SelectNodes(path)!;
			}
			catch (System.Xml.XPath.XPathException e)
			{
				throw new CodeEE(string.Format(trerror.XmlXPathParseError.Text, Name, path, e.Message));
			}
			if (nodes.Count > 0)
			{
				bool setAllNodes = arguments.Count == 3 ? arguments[2].GetIntValue(exm) != 0 : false;
				if (nodes.Count != 1)
				{
					if (setAllNodes)
						for (int i = 0; i < nodes.Count; i++) Remove(nodes[i]!);
				}
				else if (!Remove(nodes[0]!)) return 0;
				if (saveToArg0)
				{
					(arguments[0] as VariableTerm)!.SetValue(doc.OuterXml, exm);
				}
			}
			return nodes.Count;
		}
	}

	private sealed class XmlReplaceMethod : FunctionMethod
	{
		public XmlReplaceMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArrayEx = [
						new ArgTypeList{ ArgTypes = { ArgType.Any, ArgType.String } },
						new ArgTypeList{ ArgTypes = { ArgType.Int, ArgType.String, ArgType.String, ArgType.Int }, OmitStart = 3 },
						new ArgTypeList{ ArgTypes = { ArgType.RefString, ArgType.String, ArgType.String, ArgType.Int }, OmitStart = 3 },
					];
			CanRestructure = false;
		}
		public XmlReplaceMethod(bool byname) : this()
		{
			byName = byname;
			argumentTypeArrayEx = [
						new ArgTypeList{ ArgTypes = { ArgType.String, ArgType.String, ArgType.String, ArgType.Int }, OmitStart = 3 },
					];
		}
		private bool byName;

		static bool Replace(XmlNode node, XmlNode newNode)
		{
			if (node.ParentNode != null)
			{
				node.ParentNode.ReplaceChild(newNode, node);
				return true;
			}
			return false;
		}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			XmlDocument newXml = new();
			{
				string xml = arguments.Count > 2 ? arguments[2].GetStrValue(exm) : arguments[1].GetStrValue(exm);
				try
				{
					newXml.LoadXml(xml);
				}
				catch (XmlException e)
				{
					throw new CodeEE(string.Format(trerror.XmlParseError.Text, Name, xml, e.Message));
				}
			}
			bool saveToArg0 = true;
			XmlDocument doc = null!;
			if (arguments[0].GetOperandType() == typeof(long) || (byName && arguments[0].GetOperandType() == typeof(string)) || (arguments[0].GetOperandType() == typeof(string) && arguments.Count == 2))
			{
				saveToArg0 = false;
				var idx = arguments[0].GetOperandType() == typeof(string) ? arguments[0].GetStrValue(exm) : arguments[0].GetIntValue(exm).ToString();
				var dict = exm.VEvaluator.VariableData.DataXmlDocument;
				if (!dict.ContainsKey(idx)) return -1;
				if (arguments.Count == 2)
				{
					dict[idx] = newXml;
					return 1;
				}
				doc = dict[idx];
			}
			else
			{
				string xml = arguments[0].GetStrValue(exm);
				doc = new XmlDocument();
				try
				{
					doc.LoadXml(xml);
				}
				catch (XmlException e)
				{
					throw new CodeEE(string.Format(trerror.XmlParseError.Text, Name, xml, e.Message));
				}
			}
			string path = arguments[1].GetStrValue(exm);
			XmlNodeList nodes;
			try
			{
				nodes = doc.SelectNodes(path)!;
			}
			catch (System.Xml.XPath.XPathException e)
			{
				throw new CodeEE(string.Format(trerror.XmlXPathParseError.Text, Name, path, e.Message));
			}
			if (nodes.Count > 0)
			{
				var newNode = newXml.DocumentElement!;
				var child = doc.CreateNode(newNode.NodeType, newNode.Name, newNode.NamespaceURI)!;
				for (int i = 0; i < newNode.Attributes!.Count; i++)
				{
					var xattr = newNode.Attributes[i];
					var attr = doc.CreateAttribute(xattr.Name);
					attr.Value = xattr.Value;
					child.Attributes!.Append(attr);
				}
				child.InnerXml = newNode.InnerXml;
				bool setAllNodes = arguments.Count >= 4 ? arguments[3].GetIntValue(exm) != 0 : false;
				if (nodes.Count != 1)
				{
					if (setAllNodes)
						for (int i = 0; i < nodes.Count; i++) Replace(nodes[i]!, child);
				}
				else if (!Replace(nodes[0]!, child)) return 0;
				if (saveToArg0)
				{
					(arguments[0] as VariableTerm)!.SetValue(doc.OuterXml, exm);
				}
			}
			return nodes.Count;
		}
	}
}
