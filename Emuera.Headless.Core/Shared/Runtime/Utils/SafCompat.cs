using System.Collections.Generic;
using System.IO;
using System.Linq;
using MinorShift.Emuera.Runtime.Utils;

namespace MinorShift.Emuera.Runtime.Utils;

/// <summary>ADR-0019：运行时脚本 File.*/Directory.* 调用的 SAF 兼容包装。
/// <para>SAF content URI 路径走 DirAccessor，本地路径走原始 System.IO。</para>
/// </summary>
internal static class SafCompat
{
    private static bool IsContentUri(string path) =>
        path != null && path.StartsWith("content://", System.StringComparison.Ordinal);

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

    internal static string ReadAllText(string path, System.Text.Encoding encoding)
    {
        if (!IsContentUri(path)) return File.ReadAllText(path, encoding);
        return GamePaths.Current?.DirAccessor?.ReadAllText(path) ?? "";
    }

    internal static void WriteAllText(string path, string contents, System.Text.Encoding encoding)
    {
        if (!IsContentUri(path)) { File.WriteAllText(path, contents, encoding); return; }
        // SAF 写路径暂不支持——静默跳过（避免抛异常导致游戏崩溃）
        ParserMediator.Warn($"SAF write not supported: {path}", null, 0, "");
    }

    internal static void CreateDirectory(string path)
    {
        if (!IsContentUri(path)) { Directory.CreateDirectory(path); return; }
        // SAF 创建目录暂不支持
        ParserMediator.Warn($"SAF directory create not supported: {path}", null, 0, "");
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
        if (!IsContentUri(path)) { File.Delete(path); return; }
        ParserMediator.Warn($"SAF delete not supported: {path}", null, 0, "");
    }
}
