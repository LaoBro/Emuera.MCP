#if HEADLESS
using MinorShift.Emuera.Primitives;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Runtime.Utils;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace MinorShift.Emuera.UI.Game.Image;

static class AppContents
{
	static ConcurrentDictionary<int, GraphicsImage> gList = [];

	public static GraphicsImage GetGraphics(int i)
	{
		if (gList.TryGetValue(i, out GraphicsImage? value))
			return value;
		GraphicsImage g = new(i);
		gList[i] = g;
		return g;
	}

	public static ASprite GetSprite(string name) => null;
	public static void SpriteDispose(string name) { }
	public static long SpriteDisposeAll(bool delCsvImage) => 0;
	public static void CreateSpriteG(string imgName, GraphicsImage parent, EmuRectangle rect) { }
	internal static void CreateSpriteAnime(string imgName, int w, int h) { }
	public static Exception LoadContents(bool reload) => null;
	public static void UnloadContents() { foreach (var graph in gList.Values) graph.GDispose(); gList.Clear(); }
	public static void UnloadGraphicList() { foreach (var graph in gList.Values) graph.GDispose(); gList.Clear(); }
	public static void UnloadTempLoadedConstImageNames() { }
	public static void UnloadTempLoadedGraphicsImageNames() { }
}
#else
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Runtime.Utils;
using MinorShift.Emuera.Runtime.Utils.EvilMask;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using trerror = MinorShift.Emuera.Runtime.Utils.EvilMask.Lang.Error;

namespace MinorShift.Emuera.UI.Game.Image;

static class AppContents
{
	static ConcurrentDictionary<string, AbstractImage> resourceDic = new(Config.StrComper);
	static ConcurrentDictionary<string, ASprite> imageDictionary = new(Config.StrComper);
	static ConcurrentDictionary<int, GraphicsImage> gList = [];
	static ConcurrentDictionary<string, ASprite> resourceImageDictionary = new(Config.StrComper);

	public static HashSet<ConstImage> tempLoadedConstImages = [];
	public static HashSet<GraphicsImage> tempLoadedGraphicsImages = [];

	static public GraphicsImage GetGraphics(int i)
	{
		if (gList.TryGetValue(i, out GraphicsImage value))
			return value;
		GraphicsImage g = new(i);
		gList[i] = g;
		return g;
	}

	static public ASprite GetSprite(string name)
	{
		if (name == null) return null;
		name = name.ToUpper();
		if (!imageDictionary.TryGetValue(name, out ASprite value)) return null;
		return value;
	}

	static public void SpriteDispose(string name)
	{
		if (name == null) return;
		name = name.ToUpper();
		if (!imageDictionary.TryGetValue(name, out ASprite value)) return;
		value.Dispose();
		imageDictionary.TryRemove(name, out _);
	}

	static public long SpriteDisposeAll(bool delCsvImage)
	{
		int sprites = imageDictionary.Count;
		int csprites = resourceImageDictionary.Count;
		if (delCsvImage)
		{
			imageDictionary.Clear();
			resourceImageDictionary.Clear();
			return sprites;
		}
		else
		{
			imageDictionary = new ConcurrentDictionary<string, ASprite>(resourceImageDictionary);
			return sprites - csprites;
		}
	}

	static public void CreateSpriteG(string imgName, GraphicsImage parent, Rectangle rect)
	{
		if (string.IsNullOrEmpty(imgName)) throw new ArgumentOutOfRangeException();
		imgName = imgName.ToUpper();
		SpriteG newCImg = new(imgName, parent, rect);
		imageDictionary[imgName] = newCImg;
	}

	internal static void CreateSpriteAnime(string imgName, int w, int h)
	{
		if (string.IsNullOrEmpty(imgName)) throw new ArgumentOutOfRangeException();
		imgName = imgName.ToUpper();
		SpriteAnime newCImg = new(imgName, new Size(w, h));
		imageDictionary[imgName] = newCImg;
	}

	static public Exception LoadContents(bool reload)
	{
		if (!Directory.Exists(Program.ContentDir)) return null;
		try
		{
			var csvFiles = Directory.EnumerateFiles(Program.ContentDir, "*.csv", SearchOption.AllDirectories);
			foreach (var filepath in csvFiles)
			{
				if (reload)
				{
					foreach (string key in resourceImageDictionary.Keys)
						imageDictionary.TryRemove(key, out _);
					resourceImageDictionary.Clear();
					foreach (var img in resourceDic.Values) img.Dispose();
					resourceDic.Clear();
				}
			}
			csvFiles.AsParallel()
				.Where(path => Path.GetExtension(path).Equals(".csv", StringComparison.OrdinalIgnoreCase))
				.ForAll(path =>
				{
					SpriteAnime currentAnime = null;
					string directory = Path.GetDirectoryName(path) + "\\";
					string filename = Path.GetFileName(path);
					string[] lines = File.ReadAllLines(path, EncodingHandler.DetectEncoding(path));
					int lineNo = 0;
					foreach (var line in lines)
					{
						lineNo++;
						if (line.Length == 0) continue;
						string str = line.Trim();
						if (str.Length == 0 || str.StartsWith(';')) continue;
						string[] tokens = str.Split(',');
						ScriptPosition? sp = new(filename, lineNo);
						if (CreateFromCsv(tokens, directory, currentAnime, sp) is ASprite item)
						{
							currentAnime = item as SpriteAnime;
							if (reload && resourceImageDictionary.ContainsKey(item.Name))
								resourceImageDictionary.Remove(item.Name, out _);
							if (!resourceImageDictionary.TryAdd(item.Name, item))
							{
								ParserMediator.Warn(string.Format(trerror.SpriteNameAlreadyUsed.Text, item.Name), sp, 0);
								item.Dispose();
							}
						}
					}
				});
		}
		catch (Exception e) { return e; }
		imageDictionary = new ConcurrentDictionary<string, ASprite>(resourceImageDictionary);
		return null;
	}

	static public void UnloadContents()
	{
		foreach (var img in resourceDic.Values) img.Dispose();
		resourceDic.Clear();
		imageDictionary.Clear();
		resourceImageDictionary.Clear();
		foreach (var graph in gList.Values) graph.GDispose();
		gList.Clear();
	}

	static public void UnloadGraphicList()
	{
		foreach (var graph in gList.Values) graph.GDispose();
		gList.Clear();
	}

	static public void UnloadTempLoadedConstImageNames()
	{
		lock (tempLoadedConstImages)
		{
			foreach (ConstImage img in tempLoadedConstImages) img.Dispose();
			tempLoadedConstImages.Clear();
		}
	}

	static public void UnloadTempLoadedGraphicsImageNames()
	{
		lock (tempLoadedGraphicsImages)
		{
			foreach (GraphicsImage img in tempLoadedGraphicsImages)
				if (img.useImgList) img.UnLoad();
			tempLoadedGraphicsImages.Clear();
		}
	}

	static private AContentItem CreateFromCsv(string[] tokens, string dir, SpriteAnime currentAnime, ScriptPosition? sp)
	{
		if (tokens.Length < 2) return null;
		string name = tokens[0].Trim().ToUpper();
		string arg2 = tokens[1];
		if (name.Length == 0 || arg2.Length == 0) return null;
		if (arg2.Equals("ANIME", StringComparison.OrdinalIgnoreCase))
		{
			if (tokens.Length < 4) { ParserMediator.Warn(trerror.NotDeclaredAnimationSpriteSize.Text, sp, 1); return null; }
			int[] sizeValue = new int[2]; bool sccs = true;
			for (int i = 0; i < 2; i++) sccs &= int.TryParse(tokens[i + 2], out sizeValue[i]);
			if (!sccs || sizeValue[0] <= 0 || sizeValue[1] <= 0 || sizeValue[0] > AbstractImage.MAX_IMAGESIZE || sizeValue[1] > AbstractImage.MAX_IMAGESIZE)
			{ ParserMediator.Warn(trerror.InvalidAnimationSpriteSize.Text, sp, 1); return null; }
			SpriteAnime anime = new(name, new Size(sizeValue[0], sizeValue[1]));
			return anime;
		}

		if (arg2.IndexOf('.', StringComparison.Ordinal) < 0)
		{ ParserMediator.Warn(string.Format(trerror.MissingSecondArgumentExtension.Text, arg2), sp, 1); return null; }
		string parentName = dir + arg2;

		if (!resourceDic.TryGetValue(parentName, out AbstractImage value))
		{
			string filepath = parentName;
			Bitmap bmp;
			var webpbmp = Utils.LoadImage(filepath);
			if (webpbmp == null)
			{ ParserMediator.Warn(string.Format(trerror.FailedLoadFile.Text, arg2), sp, 1); return null; }
			bmp = webpbmp;
			if (bmp.Width > AbstractImage.MAX_IMAGESIZE || bmp.Height > AbstractImage.MAX_IMAGESIZE)
				ParserMediator.Warn(string.Format(trerror.TooLargeImageFile.Text, AbstractImage.MAX_IMAGESIZE.ToString(), arg2), sp, 1);
			ConstImage img = new(parentName);
			img.CreateFrom(bmp, filepath, Config.TextDrawingMode == TextDrawingMode.WINAPI);
			if (!img.IsCreated)
			{ ParserMediator.Warn(string.Format(trerror.FailedCreateResource.Text, arg2), sp, 1); return null; }
			value = img;
			resourceDic.TryAdd(parentName, value);
			img.Dispose();
		}
		if (value is not ConstImage parentImage || !parentImage.IsCreated)
		{ ParserMediator.Warn(string.Format(trerror.SpriteCreateFromFailedResource.Text, arg2), sp, 1); return null; }
		Rectangle rect = new(0, 0, parentImage.Width, parentImage.Height);
		Size size = rect.Size;
		Point pos = new();
		int delay = 1000;
		if (tokens.Length >= 6)
		{
			int[] outValue = new int[4]; bool sccs = true;
			for (int i = 0; i < 4; i++) sccs &= int.TryParse(tokens[i + 2], out outValue[i]);
			if (sccs)
			{
				rect = new Rectangle(outValue[0], outValue[1], outValue[2], outValue[3]);
				size = rect.Size;
				if (rect.Width <= 0 || rect.Height <= 0) { ParserMediator.Warn(string.Format(trerror.SpriteSizeIsNegatibe.Text, name), sp, 1); return null; }
				if (!rect.IntersectsWith(new Rectangle(0, 0, parentImage.Width, parentImage.Height)))
				{ ParserMediator.Warn(string.Format(trerror.OoRParentImage.Text, name), sp, 1); return null; }
			}
			if (tokens.Length >= 8) { sccs = true; for (int i = 0; i < 2; i++) sccs &= int.TryParse(tokens[i + 6], out outValue[i]); if (sccs) pos = new Point(outValue[0], outValue[1]); }
			if (tokens.Length >= 9) { sccs = int.TryParse(tokens[8], out delay); if (sccs && delay <= 0) { ParserMediator.Warn(string.Format(trerror.FrameTimeIsNegative.Text, name), sp, 1); return null; } }
			if (tokens.Length >= 11) { sccs = true; for (int i = 0; i < 2; i++) sccs &= int.TryParse(tokens[i + 9], out outValue[i]); if (sccs) size = new Size(outValue[0], outValue[1]); }
		}
		if (currentAnime != null && currentAnime.Name == name)
		{
			if (!currentAnime.AddFrame(parentImage, rect, pos, delay))
			{ ParserMediator.Warn(string.Format(trerror.FailedAddSpriteFrame.Text, arg2), sp, 1); return null; }
			return null;
		}
		ASprite image = new SpriteF(name, parentImage, rect, pos, size);
		return image;
	}
}
#endif
