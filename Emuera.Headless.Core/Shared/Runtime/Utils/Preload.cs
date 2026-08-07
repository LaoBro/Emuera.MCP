using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using MinorShift.Emuera.GameView;
using trerror = MinorShift.Emuera.Runtime.Utils.EvilMask.Lang.Error;

namespace MinorShift.Emuera.Runtime.Utils;

static partial class Preload
{
    static readonly ConcurrentDictionary<string, string[]> files = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>ADR-0019：返回所有已缓存文件路径键，保证与 ErbLoader/ErhLoader 查找键一致。</summary>
    public static IEnumerable<string> GetAllCachedKeys() => files.Keys;

    public static string[] GetFileLines(string path) => files[path];

    public static string[]? TryGetFileLines(string path)
    {
        files.TryGetValue(path, out var lines);
        return lines;
    }

    /// <summary>ADR-0019：通过 DirAccessor 加载路径下所有 ERB/CSV/ERH/ERD/ALS 文件到缓存。</summary>
    public static async Task Load(string path, IGameDirAccessor dirAccessor)
    {
        var startTime = DateTime.Now;
        EmueraLog.Info("preload", $"Load start: {path}");

        if (dirAccessor.DirectoryExists(path))
        {
            EmueraLog.Info("preload", $"DirExists OK: {path}");
            var allFiles = dirAccessor.GetFiles(path, "*", SearchOption.AllDirectories);
            EmueraLog.Info("preload", $"GetFiles returned {allFiles.Length} files from {path}");
            var targetFiles = allFiles.Where(f =>
            {
                // SAF content URI 不能对整段 path 用 Path.GetExtension（应取逻辑短名）
                var ext = Path.GetExtension(SafPath.GetLogicalFileName(f));
                return ext.Equals(".csv", StringComparison.OrdinalIgnoreCase) ||
                       ext.Equals(".erb", StringComparison.OrdinalIgnoreCase) ||
                       ext.Equals(".erh", StringComparison.OrdinalIgnoreCase) ||
                       ext.Equals(".erd", StringComparison.OrdinalIgnoreCase) ||
                       ext.Equals(".als", StringComparison.OrdinalIgnoreCase);
            }).ToList();

            await Task.Run(() =>
            {
                Parallel.ForEach(targetFiles, filePath =>
                {
                    var value = ReadAllLinesViaAccessor(filePath, dirAccessor);
                    files[filePath] = value;
                });
            });
        }
        else
        {
            EmueraLog.Warn("preload", $"DirExists FAILED: {path} → caching single entry");
            var value = ReadAllLinesViaAccessor(path, dirAccessor);
            files[path] = value;
        }

        Debug.WriteLine($"Load: {path} : End in {(DateTime.Now - startTime).TotalMilliseconds}ms");
        EmueraLog.Info("preload", $"{path} → {files.Count} files loaded");
    }

    public static async Task Load(IEnumerable<string> paths, IGameDirAccessor dirAccessor)
    {
        foreach (var path in paths)
            await Load(path, dirAccessor);
    }

    public static void Clear() => files.Clear();

    private static string[] ReadAllLinesViaAccessor(string path, IGameDirAccessor dirAccessor)
    {
        try
        {
            var bytes = dirAccessor.ReadAllBytes(path);
            if (bytes == null)
            {
                EmueraLog.Warn("preload", $"EMPTY_BYTES: {path}");
                return [];
            }
            var lines = EncodingHandler.ReadAllLinesFromBytes(bytes);
            if (lines.Length == 0)
                EmueraLog.Warn("preload", $"EMPTY_LINES: {path}");
            return lines;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ParserMediator.Warn(string.Format(trerror.FileUsingOtherProcess.Text, path), new ScriptPosition(path, 0), 0, "");
            EmueraLog.Error("preload", $"IOERR: {path} → {ex.Message}");
            return [];
        }
        catch (Exception)
        {
            ParserMediator.Warn(trerror.AbnormalEncode.Text, new ScriptPosition(path, 0), 0, "");
            EmueraLog.Error("preload", $"ENCERR: {path}");
            return [];
        }
    }
}
