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
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using trerror = MinorShift.Emuera.Runtime.Utils.EvilMask.Lang.Error;

namespace MinorShift.Emuera.GameData.Function;

internal static partial class FunctionMethodCreator
{
	private sealed class ExistFileMethod : FunctionMethod
	{
		public ExistFileMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArrayEx = [new ArgTypeList{ ArgTypes = { ArgType.String | ArgType.DisallowVoid } }];
			CanRestructure = false;
		}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			var filepath = Utils.GetValidPath(arguments[0].GetStrValue(exm));
			if (filepath != null && SafCompat.FileExists(filepath)) return 1;
			return 0;
		}
	}

	public sealed class GraphicsCreateFromFileMethod : FunctionMethod
	{
		public GraphicsCreateFromFileMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArrayEx = [
					new ArgTypeList{ ArgTypes = { ArgType.Int, ArgType.String, ArgType.Int }, OmitStart = 2 }
				];
			CanRestructure = false;
		}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			if (Config.TextDrawingMode == TextDrawingMode.WINAPI)
				throw new CodeEE(string.Format(trerror.GDIPlusOnly.Text, Name));
			GraphicsImage g = ReadGraphics(Name, exm, arguments, 0);
			if (g.IsCreated)
				return 0;
#if HEADLESS
			// Headless 模式下不支持图片加载
			return 0;
#else
			string filename = arguments[1].GetStrValue(exm);
			bool isRelative = false;
			if (arguments.Count > 2)
				isRelative = arguments[2].GetIntValue(exm) != 0;

			Bitmap bmp = null;
			try
			{
				string filepath = filename;
				if (!Path.IsPathRooted(filepath))
				{
					if (isRelative)
						filepath = filename;
					else
						filepath = Program.ContentDir + filename;
				}
				if (!SafCompat.FileExists(filepath))
					return 0;
				#region EM_私家版_webp
				bmp = Utils.LoadImage(filepath);
				if (bmp == null) return 0;
				#endregion
				if (bmp.Width > AbstractImage.MAX_IMAGESIZE || bmp.Height > AbstractImage.MAX_IMAGESIZE)
					return 0;
				g.GCreateFromF(bmp, Config.TextDrawingMode == TextDrawingMode.WINAPI);
			}
			catch (Exception e)
			{
				if (e is CodeEE)
					throw;
			}
			finally
			{
				if (bmp != null)
					bmp.Dispose();
			}
			//画像ファイルではなかった、などによる失敗
			if (!g.IsCreated)
				return 0;
			return 1;
#endif
		}
	}

	private sealed class SaveTextMethod : FunctionMethod
	{
		public SaveTextMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArrayEx = [
					new ArgTypeList{ ArgTypes = { ArgType.String, ArgType.Any, ArgType.Int, ArgType.Int }, OmitStart = 2 },
				];
			CanRestructure = false;
		}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			#region EM_私家版_LoadText＆SaveText機能拡張
			string savText = arguments[0].GetStrValue(exm), filepath;
			long i64 = -1;
			bool forceSavdir = arguments.Count > 2 && (arguments[2].GetIntValue(exm) != 0);
			bool forceUTF8 = arguments.Count > 3 && (arguments[3].GetIntValue(exm) != 0);


			if (arguments[1].GetOperandType() == typeof(long))
			{
				i64 = arguments[1].GetIntValue(exm);
				if (i64 < 0 || i64 > int.MaxValue)
					return 0;
				int fileIndex = (int)i64;
				filepath = forceSavdir ?
				GetSaveDataPathText(fileIndex, Config.ForceSavDir) :
				GetSaveDataPathText(fileIndex, Config.SavDir);
			}
			else
			{
				string relativePath = arguments[1].GetStrValue(exm);
				string tmp = Path.HasExtension(relativePath) ? Path.GetExtension(relativePath).ToLowerInvariant().Substring(1) : "";
				if (!Config.ValidExtension.Contains(tmp))
					relativePath = Path.ChangeExtension(relativePath, "txt");
				if (!SafCompat.TryResolveGameRelativePath(relativePath, createParentDirectories: true, out filepath))
					return 0;
			}

			Encoding encoding = forceUTF8 ? EncodingHandler.UTF8BOMEncoding : Config.SaveEncode;
			try
			{
				if (i64 >= 0)
				{
					if (forceSavdir)
						Config.ForceCreateSavDir();
					else
						Config.CreateSavDir();
				}
				SafCompat.WriteAllText(filepath, savText, encoding);
			}
			catch { return 0; }
			#endregion
			return 1;
		}
	}

	private sealed class LoadTextMethod : FunctionMethod
	{
		public LoadTextMethod()
		{
			ReturnType = typeof(string);
			argumentTypeArrayEx = [
					new ArgTypeList{ ArgTypes = { ArgType.Any, ArgType.Int, ArgType.Int }, OmitStart = 1 },
				];
			CanRestructure = false;
		}
		public override string GetStrValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			#region EM_私家版_LoadText＆SaveText機能拡張
			string ret = "", filepath;
			long i64 = -1;
			bool forceSavdir = arguments.Count > 1 && (arguments[1].GetIntValue(exm) != 0);
			bool forceUTF8 = arguments.Count > 2 && (arguments[2].GetIntValue(exm) != 0);
			if (arguments[0].GetOperandType() == typeof(long))
			{
				i64 = arguments[0].GetIntValue(exm);
				if (i64 < 0 || i64 > int.MaxValue)
					return "";
				int fileIndex = (int)i64;
				filepath = forceSavdir ?
				GetSaveDataPathText(fileIndex, Config.ForceSavDir) :
				GetSaveDataPathText(fileIndex, Config.SavDir);
			}
			else
			{
				string relativePath = arguments[0].GetStrValue(exm);
				if (!SafCompat.TryResolveGameRelativePath(relativePath, createParentDirectories: false, out filepath))
					return string.Empty;
				string tmp = Path.HasExtension(relativePath) ? Path.GetExtension(relativePath).ToLowerInvariant().Substring(1) : "";
				if (!Config.ValidExtension.Contains(tmp))
					return "";
			}

			if (!SafCompat.FileExists(filepath))
				return "";
			try
			{
				Encoding encoding = forceUTF8
					? EncodingHandler.UTF8BOMEncoding
					: EncodingHandler.DetectEncoding(SafCompat.ReadAllBytes(filepath) ?? []);
				ret = SafCompat.ReadAllText(filepath, encoding);
			}
			catch { return ""; }
			//一貫性の観点で\rには死んでもらう
			return ret.Replace("\r", "");
			#endregion
		}
	}

	private static string GetSaveDataPathText(int index, string dir) =>
		SafCompat.CombinePath(dir, string.Format(CultureInfo.InvariantCulture, "txt{0:00}.txt", index));

	private static string GetSaveDataPathGraphics(int index) =>
		SafCompat.CombinePath(Config.SavDir, string.Format(CultureInfo.InvariantCulture, "img{0:0000}.png", index));

	public sealed class GraphicsSaveMethod : FunctionMethod
	{
		public GraphicsSaveMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArrayEx = [new ArgTypeList{ ArgTypes = { ArgType.Int | ArgType.DisallowVoid, ArgType.Int | ArgType.DisallowVoid } }];
			CanRestructure = false;
		}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			if (Config.TextDrawingMode == TextDrawingMode.WINAPI)
				throw new CodeEE(string.Format(trerror.GDIPlusOnly.Text, Name));
			GraphicsImage g = ReadGraphics(Name, exm, arguments, 0);
			if (!g.IsCreated)
				return 0;

			long i64 = arguments[1].GetIntValue(exm);
			if (i64 < 0 || i64 > int.MaxValue)
				return 0;

			string filepath = GetSaveDataPathGraphics((int)i64);
			try
			{
				Config.CreateSavDir();
#if HEADLESS
				// headless: 无实际位图可保存
#else
				g.Bitmap.Save(filepath);
#endif
			}
			catch
			{
				return 0;
			}
			return 1;
		}
	}

	public sealed class GraphicsLoadMethod : FunctionMethod
	{
		public GraphicsLoadMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArrayEx = [new ArgTypeList{ ArgTypes = { ArgType.Int | ArgType.DisallowVoid, ArgType.Int | ArgType.DisallowVoid } }];
			CanRestructure = false;
		}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			if (Config.TextDrawingMode == TextDrawingMode.WINAPI)
				throw new CodeEE(string.Format(trerror.GDIPlusOnly.Text, Name));
			GraphicsImage g = ReadGraphics(Name, exm, arguments, 0);
			if (g.IsCreated)
				return 0;
#if HEADLESS
			// Headless 模式下不支持图片加载
			return 0;
#else
			long i64 = arguments[1].GetIntValue(exm);
			if (i64 < 0 || i64 > int.MaxValue)
				return 0;

			string filepath = GetSaveDataPathGraphics((int)i64);
			Bitmap bmp = null;
			try
			{
				if (!SafCompat.FileExists(filepath))
					return 0;
				#region EM_私家版_webp
				bmp = Utils.LoadImage(filepath);
				if (bmp == null) return 0;
				#endregion
				if (bmp.Width > AbstractImage.MAX_IMAGESIZE || bmp.Height > AbstractImage.MAX_IMAGESIZE)
					return 0;
				g.GCreateFromF(bmp, Config.TextDrawingMode == TextDrawingMode.WINAPI);
			}
			catch (Exception e)
			{
				if (e is CodeEE)
					throw;
			}
			finally
			{
				if (bmp != null)
					bmp.Dispose();
			}
			if (!g.IsCreated)
				return 0;
			return 1;
#endif
		}
	}
}
