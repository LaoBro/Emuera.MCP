using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using trerror = MinorShift.Emuera.Runtime.Utils.EvilMask.Lang.Error;

namespace MinorShift.Emuera.Runtime.Utils;
static partial class Preload
{
	static Dictionary<string, string[]> files = new(StringComparer.OrdinalIgnoreCase);

	public static string[] GetFileLines(string path)
	{
		return files[path];
	}

	/// <summary>
	/// 决策二：错误路径安全读取缓存 — 不抛 KeyNotFoundException，返回 null 由调用方降级。
	/// 供 getRawTextFormFilewithLine 等错误路径使用，避免直接 File.ReadLines 重复 I/O。
	/// </summary>
	public static string[]? TryGetFileLines(string path)
	{
		files.TryGetValue(path, out var lines);
		return lines;
	}

	// 决策零：合并 I/O — 调用 EncodingHandler.ReadAllLinesWithDetection 完成单次读取 + 编码检测 + 行分割。
	// 原实现非 BOM 文件需 3 次文件打开 + 2 次完整扫描（BOM 检查 + DetectEncoding.ReadToEnd + File.ReadAllLines）。
	private static string[] readAllLinesDetectEncoding(string path)
	{
		try
		{
			return EncodingHandler.ReadAllLinesWithDetection(path);
		}
		catch (DecoderFallbackException)
		{
			// ReadAllLinesWithDetection 内部先试 UTF-8（严格模式），失败回退 SHIFT-JIS（CP932 亦用 ExceptionFallback）。
			// 此 catch 仅在两者均抛 DecoderFallbackException 时到达（字节序列对两种编码都无效）。
			ParserMediator.Warn(trerror.AbnormalEncode.Text, new ScriptPosition(path, 0), 0, "");
			return null!;
		}
		catch (IOException)
		{
			ParserMediator.Warn(string.Format(trerror.FileUsingOtherProcess.Text, path), new ScriptPosition(path, 0), 0, "");
			return File.ReadAllLines(path, EncodingHandler.UTF8BOMEncoding);
		}
		catch (Exception)
		{
			// 保留原裸 catch 语义：UnauthorizedAccess/Argument/PathTooLong 等异常降级为警告，不中断加载。
			ParserMediator.Warn(trerror.AbnormalEncode.Text, new ScriptPosition(path, 0), 0, "");
			return null!;
		}
	}

	public static async Task Load(string path)
	{
		var startTime = DateTime.Now;
		Debug.WriteLine($"Load: {path} : Start");

		var dir = new DirectoryInfo(path);
		if (dir.Exists)
		{
			await Task.Run(() =>
			{
				dir.EnumerateFiles("*", SearchOption.AllDirectories)
				.AsParallel()
				.Where(x =>
				{
					var ext = x.Extension;
					return ext.Equals(".csv", StringComparison.OrdinalIgnoreCase) ||
							ext.Equals(".erb", StringComparison.OrdinalIgnoreCase) ||
							ext.Equals(".erh", StringComparison.OrdinalIgnoreCase) ||
							ext.Equals(".erd", StringComparison.OrdinalIgnoreCase) ||
							ext.Equals(".als", StringComparison.OrdinalIgnoreCase);
				}).ForAll((childPath) =>
				{
					var key = childPath;
					var value = readAllLinesDetectEncoding(childPath.ToString());
					lock (files)
					{
						files[key.ToString()] = value;
					}

				});
			});
		}
		else
		{
			var key = path;
			var value = readAllLinesDetectEncoding(path);
			lock (files)
			{
				files[key] = value;
			}
		};

		Debug.WriteLine($"Load: {path} : End in {(DateTime.Now - startTime).TotalMilliseconds}ms");
	}

	public static async Task Load(IEnumerable<string> paths)
	{
		foreach (var path in paths)
		{
			await Load(path);
		}
	}

	public static void Clear()
	{
		files.Clear();
	}
}
