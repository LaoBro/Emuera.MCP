using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace MinorShift.Emuera.Runtime.Utils;

public static class EncodingHandler
{
	public static readonly Encoding UTF8Encoding = new UTF8Encoding(false, true);
	public static readonly Encoding shiftjisEncoding = GetEncoding(932);
	public static readonly Encoding UTF8BOMEncoding = new UTF8Encoding(true, true);


	/// <summary>
	/// 单次 I/O 读取文件全部字节并完成 BOM 检测 + UTF-8/SHIFT-JIS 解码尝试。
	/// 替代原先 DetectEncoding + File.ReadAllLines 的多次 I/O 路径（决策零）。
	/// 行分割语义与 <see cref="File.ReadAllLines(string, Encoding)"/> 一致（StringReader.ReadLine 处理 \r\n / \r / \n）。
	/// </summary>
	public static string[] ReadAllLinesWithDetection(string path)
	{
		byte[] bytes = File.ReadAllBytes(path);
		var bomEnc = DetectBomEncoding(bytes);
		if (bomEnc != null)
		{
			int bomLen = bomEnc == UTF8BOMEncoding ? 3 : 2;
			return SplitLines(bomEnc.GetString(bytes, bomLen, bytes.Length - bomLen));
		}
		// 无 BOM：尝试 UTF-8（严格模式，遇无效字节抛 DecoderFallbackException）
		try
		{
			return SplitLines(UTF8Encoding.GetString(bytes));
		}
		catch (DecoderFallbackException)
		{
			// UTF-8 失败 → 假设 SHIFT-JIS（CP 932 对绝大多数日文文件可解码）
			return SplitLines(shiftjisEncoding.GetString(bytes));
		}
	}

	/// <summary>
	/// 检查字节序列的 BOM 标记，返回对应编码；无 BOM 返回 null。
	/// 供 ReadAllLinesWithDetection 与 DetectEncoding 共享，避免 BOM 级联逻辑重复。
	/// </summary>
	private static Encoding? DetectBomEncoding(byte[] bytes)
	{
		if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
			return UTF8BOMEncoding;
		if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
			return Encoding.Unicode;
		if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
			return Encoding.BigEndianUnicode;
		return null;
	}

	/// <summary>
	/// 将已解码的字符串按 \r\n / \r / \n 分割为行数组。
	/// 语义与 <see cref="File.ReadAllLines(string, Encoding)"/> 一致（不包含末尾空行）。
	/// </summary>
	private static string[] SplitLines(string content)
	{
		if (string.IsNullOrEmpty(content))
			return [];
		var lines = new List<string>();
		using var sr = new StringReader(content);
		string? line;
		while ((line = sr.ReadLine()) != null)
			lines.Add(line);
		return lines.ToArray();
	}

	public static Encoding DetectEncoding(string filePath)
	{
		try
		{
			// 决策零：合并 I/O — 一次 ReadAllBytes 同时完成 BOM 检查 + UTF-8 验证，
			// 不再经 StreamReader.ReadToEnd() 浪费一次完整扫描。
			byte[] bytes = File.ReadAllBytes(filePath);
			var bomEnc = DetectBomEncoding(bytes);
			if (bomEnc != null)
				return bomEnc;
			try
			{
				UTF8Encoding.GetString(bytes);
				return UTF8Encoding;
			}
			catch (DecoderFallbackException)
			{
				return shiftjisEncoding;
			}
		}
		catch
		{
			return shiftjisEncoding;
		}
	}

	public static Encoding DetectEncoding(Stream stream)
	{
		var pos = stream.Position;
		try
		{
			using var sr = new StreamReader(stream, UTF8Encoding, true, -1, true);
			sr.Peek();
			if (!UTF8Encoding.Equals(sr.CurrentEncoding))
			{
				stream.Seek(pos, SeekOrigin.Begin);
				return sr.CurrentEncoding;
			}
			sr.ReadToEnd();
			sr.Dispose();
			stream.Seek(pos, SeekOrigin.Begin);
			return UTF8Encoding;
		}
		catch
		{
			stream.Seek(pos, SeekOrigin.Begin);
			return shiftjisEncoding;
		}
	}
	public static Encoding GetEncoding(int codePage)
	{
		Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
		return Encoding.GetEncoding(codePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
	}
}
