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
	public sealed class SpriteStateMethod : FunctionMethod
	{
		public SpriteStateMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArrayEx = [new ArgTypeList{ ArgTypes = { ArgType.String | ArgType.DisallowVoid } }];
			CanRestructure = false;
		}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			string imgname = arguments[0].GetStrValue(exm);
			ASprite img = AppContents.GetSprite(imgname);
			if (img == null || !img.IsCreated)
				return 0;
			switch (Name)
			{
				case "SPRITECREATED":
					return 1;
				case "SPRITEWIDTH":
					return img.DestBaseSize.Width;
				case "SPRITEHEIGHT":
					return img.DestBaseSize.Height;
				case "SPRITEPOSX":
					return img.DestBasePosition.X;
				case "SPRITEPOSY":
					return img.DestBasePosition.Y;
			}
			throw new ExeEE("SpriteStateMethod:" + Name + ":異常な分岐");
		}
	}

	public sealed class SpriteSetPosMethod : FunctionMethod
	{
		public SpriteSetPosMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArrayEx = [new ArgTypeList{ ArgTypes = { ArgType.String | ArgType.DisallowVoid, ArgType.Int | ArgType.DisallowVoid, ArgType.Int | ArgType.DisallowVoid } }];
			CanRestructure = false;
		}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			string imgname = arguments[0].GetStrValue(exm);
			ASprite img = AppContents.GetSprite(imgname);
			if (img == null || !img.IsCreated)
				return 0;
			EmuPoint p = ReadPoint(Name, exm, arguments, 1);
			switch (Name)
			{
				case "SPRITEMOVE":
#if HEADLESS
					// headless: sprite 无实际渲染，位置偏移无意义
#else
					img.DestBasePosition.Offset(p);
#endif
					return 1;
				case "SPRITESETPOS":
					img.DestBasePosition = p;
					return 1;
			}
			throw new ExeEE("SpriteStateMethod:" + Name + ":異常な分岐");
		}
	}

	public sealed class SpriteGetColorMethod : FunctionMethod
	{
		public SpriteGetColorMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArrayEx = [new ArgTypeList{ ArgTypes = { ArgType.String | ArgType.DisallowVoid, ArgType.Int | ArgType.DisallowVoid, ArgType.Int | ArgType.DisallowVoid } }];
			CanRestructure = false;
		}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			string imgname = arguments[0].GetStrValue(exm);
			ASprite img = AppContents.GetSprite(imgname);
			//他と違って失敗は0ではなく負の値
			if (img == null || !img.IsCreated)
				return -1;
			EmuPoint p = ReadPoint(Name, exm, arguments, 1);
			if (p.X < 0 || p.X >= img.DestBaseSize.Width)
				return -1;
			if (p.Y < 0 || p.Y >= img.DestBaseSize.Height)
				return -1;
			EmuColor c = img.SpriteGetColor(p.X, p.Y);
			//Color.ToArgb()はInt32の負の値をとることがあり、Int64にうまく変換できない？（と思ったが気のせいだった
			return c.ToArgb() & 0xFFFFFFFFL;
		}
	}

	public sealed class SpriteCreateMethod : FunctionMethod
	{
		public SpriteCreateMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArrayEx = [
					new ArgTypeList{ ArgTypes = { ArgType.String, ArgType.Int } },
					new ArgTypeList{ ArgTypes = { ArgType.String, ArgType.Int, ArgType.Int, ArgType.Int, ArgType.Int, ArgType.Int } },
				];
			CanRestructure = false;
		}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			RequireGDIPlus(Name);
			string imgname = arguments[0].GetStrValue(exm);
			if (string.IsNullOrEmpty(imgname))
				return 0;
			ASprite img = AppContents.GetSprite(imgname);
			if (img != null && img.IsCreated)
				return 0;
			GraphicsImage? g = TryGetCreatedGraphics(Name, exm, arguments, 1);
			if (g == null)
				return 0;

			EmuRectangle rect = new(0, 0, g.Width, g.Height);
			if (arguments.Count == 6)
			{//四角形は正でも負でもよいが親画像の外を指してはいけない
				rect = ReadRectangle(Name, exm, arguments, 2);
				#region EM_私家版_SPRITECREATE範囲制限緩和

				if (!rect.IntersectsWith(new EmuRectangle(0, 0, g.Width, g.Height)))
					throw new CodeEE(string.Format(trerror.ImgRefOutOfRange.Text, Name));
				#endregion
			}
			AppContents.CreateSpriteG(imgname, g, rect);
			return 1;
		}
	}

	public sealed class SpriteDisposeMethod : FunctionMethod
	{
		public SpriteDisposeMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArrayEx = [new ArgTypeList{ ArgTypes = { ArgType.String | ArgType.DisallowVoid } }];
			CanRestructure = false;
		}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			string imgname = arguments[0].GetStrValue(exm);
			ASprite img = AppContents.GetSprite(imgname);
			if (img == null || !img.IsCreated)
				return 0;
			AppContents.SpriteDispose(imgname);
			return 1;
		}
	}

	public sealed class SpriteDisposeAllMethod : FunctionMethod
	{
		public SpriteDisposeAllMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArrayEx = [new ArgTypeList{ ArgTypes = { ArgType.Int | ArgType.DisallowVoid } }];
			CanRestructure = false;
		}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			return AppContents.SpriteDisposeAll(arguments[0].GetIntValue(exm) != 0);
		}
	}

	public sealed class GraphicsDrawSpriteMethod : FunctionMethod
	{
		public GraphicsDrawSpriteMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArrayEx = [
					new ArgTypeList{ ArgTypes = { ArgType.Int, ArgType.String } },
					new ArgTypeList{ ArgTypes = { ArgType.Int, ArgType.String, ArgType.Int, ArgType.Int } },
					new ArgTypeList{ ArgTypes = { ArgType.Int, ArgType.String, ArgType.Int, ArgType.Int, ArgType.Int, ArgType.Int, ArgType.RefInt2D | ArgType.AllowConstRef }, OmitStart = 6 },
					new ArgTypeList{ ArgTypes = { ArgType.Int, ArgType.String, ArgType.Int, ArgType.Int, ArgType.Int, ArgType.Int, ArgType.RefInt3D | ArgType.AllowConstRef }, OmitStart = 6 },
				];
			CanRestructure = false;
			HasUniqueRestructure = true;
		}

		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			GraphicsImage? dest = TryGetCreatedGraphics(Name, exm, arguments, 0);
			if (dest == null)
				return 0;

			string imgname = arguments[1].GetStrValue(exm);
			ASprite img = AppContents.GetSprite(imgname);
			if (img == null || !img.IsCreated)
				return 0;

			EmuRectangle destRect = new(0, 0, img.DestBaseSize.Width, img.DestBaseSize.Height);
			if (arguments.Count == 2)
			{
				GraphicsImage.GDrawCImg(img, destRect);
				return 1;
			}
			if (arguments.Count == 4)
			{
				EmuPoint p = ReadPoint(Name, exm, arguments, 2);
				destRect = destRect with { X = p.X, Y = p.Y };
				GraphicsImage.GDrawCImg(img, destRect);
				return 1;
			}
			if (arguments.Count == 6)
			{
				destRect = ReadRectangle(Name, exm, arguments, 2);
				GraphicsImage.GDrawCImg(img, destRect);
				return 1;
			}
			destRect = ReadRectangle(Name, exm, arguments, 2);
			float[][] cm = ReadColormatrix(Name, exm, arguments, 6);
			GraphicsImage.GDrawCImg(img, destRect, cm);
			return 1;
		}

		public override bool UniqueRestructure(ExpressionMediator exm, List<AExpression> arguments)
		{
			for (int i = 0; i < arguments.Count; i++)
			{
				if (arguments[i] == null)
					continue;
				//7番目の引数はColorMatrixの配列を指しているので定数にしてはいけない
				if (i == 6)
					arguments[i].Restructure(exm);
				else
					arguments[i] = arguments[i].Restructure(exm);
			}
			return false;
		}
	}

	public sealed class SpriteAnimeCreateMethod : FunctionMethod
	{
		public SpriteAnimeCreateMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArrayEx = [new ArgTypeList{ ArgTypes = { ArgType.String | ArgType.DisallowVoid, ArgType.Int | ArgType.DisallowVoid, ArgType.Int | ArgType.DisallowVoid } }];
			CanRestructure = false;
		}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			RequireGDIPlus(Name);
			string imgname = arguments[0].GetStrValue(exm);
			if (string.IsNullOrEmpty(imgname))
				return 0;
			//リソースチェック・既に存在しているならば失敗
			ASprite img = AppContents.GetSprite(imgname);
			if (img != null && img.IsCreated)
				return 0;
			EmuPoint pos = ReadPoint(Name, exm, arguments, 1);
			if (pos.X <= 0)//{0}関数:GraphicsのWidthに0以下の値({1})が指定されました
				throw new CodeEE(string.Format(trerror.GParamIsNegative.Text, Name, "Width", pos.X));
			else if (pos.X > AbstractImage.MAX_IMAGESIZE)//{0}関数:GraphicsのWidthに{2}以上の値({1})が指定されました
				throw new CodeEE(string.Format(trerror.GParamTooLarge.Text, Name, "Width", AbstractImage.MAX_IMAGESIZE, pos.X));
			if (pos.Y <= 0)//{0}関数:GraphicsのHeightに0以下の値({1})が指定されました
				throw new CodeEE(string.Format(trerror.GParamIsNegative.Text, Name, "Height", pos.Y));
			else if (pos.Y > AbstractImage.MAX_IMAGESIZE)//{0}関数:GraphicsのHeightに{2}以上の値({1})が指定されました
				throw new CodeEE(string.Format(trerror.GParamTooLarge.Text, Name, "Height", AbstractImage.MAX_IMAGESIZE, pos.Y));
			AppContents.CreateSpriteAnime(imgname, pos.X, pos.Y);
			return 1;
		}
	}

	public sealed class SpriteAnimeAddFrameMethod : FunctionMethod
	{
		public SpriteAnimeAddFrameMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArrayEx = [new ArgTypeList{ ArgTypes = { ArgType.String | ArgType.DisallowVoid, ArgType.Int | ArgType.DisallowVoid, ArgType.Int | ArgType.DisallowVoid, ArgType.Int | ArgType.DisallowVoid, ArgType.Int | ArgType.DisallowVoid, ArgType.Int | ArgType.DisallowVoid, ArgType.Int | ArgType.DisallowVoid, ArgType.Int | ArgType.DisallowVoid, ArgType.Int | ArgType.DisallowVoid } }];
			CanRestructure = false;
		}

		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			RequireGDIPlus(Name);
			string imgname = arguments[0].GetStrValue(exm);
			if (string.IsNullOrEmpty(imgname))
				return 0;
			if (AppContents.GetSprite(imgname) == null)
				return 0;
			SpriteAnime img = (AppContents.GetSprite(imgname) as SpriteAnime)!;
			if (img == null || !img.IsCreated)
				return 0;
			GraphicsImage? g = TryGetCreatedGraphics(Name, exm, arguments, 1);
			if (g == null)
				return 0;
			EmuRectangle rect = ReadRectangle(Name, exm, arguments, 2);
			//四角形は正でなければならず、かつ親画像の外を指してはいけない
			if (rect.Width <= 0 || rect.Height <= 0 ||
				rect.X < 0 || rect.X + rect.Width > g.Width || rect.Y < 0 || rect.Y + rect.Height > g.Height)
				return 0;
			EmuPoint offset = ReadPoint(Name, exm, arguments, 6);
			long delay = arguments[8].GetIntValue(exm);
			if (delay <= 0 || delay > int.MaxValue)
				return 0;
			SpriteAnime.AddFrame(g, rect, offset, (int)delay);
			return 1;
		}
	}
}
