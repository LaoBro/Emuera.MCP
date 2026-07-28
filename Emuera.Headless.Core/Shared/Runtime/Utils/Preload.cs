using System;
using System.Collections.Concurrent;
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
    static readonly ConcurrentDictionary<string, string[]> files = new(StringComparer.OrdinalIgnoreCase);

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
        Debug.WriteLine($"Load: {path} : Start");

        if (dirAccessor.DirectoryExists(path))
        {
            var allFiles = dirAccessor.GetFiles(path, "*", SearchOption.AllDirectories);
            var targetFiles = allFiles.Where(f =>
            {
                var ext = Path.GetExtension(f);
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
            var value = ReadAllLinesViaAccessor(path, dirAccessor);
            files[path] = value;
        }

        Debug.WriteLine($"Load: {path} : End in {(DateTime.Now - startTime).TotalMilliseconds}ms");
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
            if (bytes == null) return [];
            return EncodingHandler.ReadAllLinesFromBytes(bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ParserMediator.Warn(string.Format(trerror.FileUsingOtherProcess.Text, path), new ScriptPosition(path, 0), 0, "");
            return [];
        }
        catch (Exception)
        {
            ParserMediator.Warn(trerror.AbnormalEncode.Text, new ScriptPosition(path, 0), 0, "");
            return [];
        }
    }
}
