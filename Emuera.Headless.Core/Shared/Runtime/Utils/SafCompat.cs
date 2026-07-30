using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using MinorShift.Emuera.Runtime.Utils;

namespace MinorShift.Emuera.Runtime.Utils;

/// <summary>ADR-0019：运行时脚本 File.*/Directory.* 调用的 SAF 兼容包装。
/// <para>SAF content URI 路径走 DirAccessor，本地路径走原始 System.IO。</para>
/// <para>方案 B：写路径（CreateDirectory / OpenWrite / Delete / WriteAllText）已接通，不再静默跳过。</para>
/// </summary>
internal static class SafCompat
{
    private static bool IsContentUri(string path) =>
        path != null && path.StartsWith("content://", System.StringComparison.Ordinal);

    private static IGameDirAccessor RequireAccessor(string op)
    {
        var acc = GamePaths.Current?.DirAccessor;
        if (acc == null)
            throw new IOException($"SAF {op} requires GamePaths.DirAccessor");
        return acc;
    }

    internal static bool FileExists(string path)
    {
        if (!IsContentUri(path)) return File.Exists(path);
        return GamePaths.Current?.DirAccessor?.FileExists(path) ?? false;
    }

    internal static bool DirectoryExists(string path)
    {
        if (!IsContentUri(path)) return Directory.Exists(path);
        return GamePaths.Current?.DirAccessor?.DirectoryExists(path) ?? false;
    }

    internal static string ReadAllText(string path)
    {
        if (!IsContentUri(path)) return File.ReadAllText(path);
        return GamePaths.Current?.DirAccessor?.ReadAllText(path) ?? "";
    }

    internal static string ReadAllText(string path, Encoding encoding)
    {
        if (!IsContentUri(path)) return File.ReadAllText(path, encoding);
        // DirAccessor 当前按字节读；编码由调用方已 Detect 时仍走文本流
        using var stream = OpenRead(path)
            ?? throw new FileNotFoundException($"SAF file not found: {path}", path);
        using var reader = new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    internal static void WriteAllText(string path, string contents, Encoding encoding)
    {
        using var stream = OpenWrite(path);
        using var writer = new StreamWriter(stream, encoding);
        writer.Write(contents);
    }

    internal static void CreateDirectory(string path)
    {
        if (!IsContentUri(path))
        {
            Directory.CreateDirectory(path);
            return;
        }
        RequireAccessor("CreateDirectory").CreateDirectory(path);
    }

    /// <summary>创建或截断写入（对齐 FileMode.Create）。调用方负责 Dispose。</summary>
    internal static Stream OpenWrite(string path)
    {
        if (!IsContentUri(path))
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            return new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        }
        return RequireAccessor("OpenWrite").OpenWrite(path);
    }

    /// <summary>打开只读流；不存在返回 null。</summary>
    internal static Stream? OpenRead(string path)
    {
        if (!IsContentUri(path))
            return File.Exists(path) ? File.OpenRead(path) : null;
        return GamePaths.Current?.DirAccessor?.OpenRead(path);
    }

    internal static string[] GetFiles(string path, string searchPattern, SearchOption searchOption)
    {
        if (!IsContentUri(path)) return Directory.GetFiles(path, searchPattern, searchOption);
        return GamePaths.Current?.DirAccessor?.GetFiles(path, searchPattern, searchOption) ?? [];
    }

    internal static string[] EnumerateFiles(string path, string searchPattern, SearchOption searchOption)
    {
        if (!IsContentUri(path))
            return Directory.EnumerateFiles(path, searchPattern, searchOption).ToArray();
        return GamePaths.Current?.DirAccessor?.GetFiles(path, searchPattern, searchOption) ?? [];
    }

    internal static byte[]? ReadAllBytes(string path)
    {
        if (!IsContentUri(path)) return File.ReadAllBytes(path);
        return GamePaths.Current?.DirAccessor?.ReadAllBytes(path);
    }

    internal static string[] ReadAllLines(string path)
    {
        if (!IsContentUri(path)) return File.ReadAllLines(path);
        return EncodingHandler.ReadAllLinesFromBytes(
            GamePaths.Current?.DirAccessor?.ReadAllBytes(path) ?? []);
    }

    internal static FileAttributes GetAttributes(string path)
    {
        if (!IsContentUri(path)) return File.GetAttributes(path);
        return FileAttributes.Normal;
    }

    internal static void Delete(string path)
    {
        if (!IsContentUri(path))
        {
            if (File.Exists(path))
                File.Delete(path);
            return;
        }
        RequireAccessor("Delete").Delete(path);
    }

    /// <summary>目录 + 文件名组合；content URI 必须走 DirAccessor.CombinePath。</summary>
    internal static string CombinePath(string basePath, string fileName)
    {
        if (IsContentUri(basePath))
            return RequireAccessor("CombinePath").CombinePath(basePath, fileName);

        var trimmed = basePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return Path.Combine(trimmed, fileName);
    }

    /// <summary>解析子目录；content 走 ResolveSubPath，本地 Path.Combine + 尾部分隔符。</summary>
    internal static string ResolveSubPath(string basePath, string subDir)
    {
        if (IsContentUri(basePath))
            return RequireAccessor("ResolveSubPath").ResolveSubPath(basePath, subDir);

        return Path.Combine(basePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), subDir)
            + Path.DirectorySeparatorChar;
    }
}
