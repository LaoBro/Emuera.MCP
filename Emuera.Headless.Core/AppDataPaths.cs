using System;
using System.IO;
using System.Threading;

namespace MinorShift.Emuera;

/// <summary>应用私有文件根目录。游戏文件继续由 <see cref="GamePaths"/> 与 <see cref="IGameDirAccessor"/> 管理。</summary>
public static class AppDataPaths
{
    private static string _directory = GetDefaultDirectory();

    public static string Directory => Volatile.Read(ref _directory);

    /// <summary>由平台入口注入应用私有目录；桌面和测试可使用默认目录或显式覆盖。</summary>
    public static void Configure(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
            throw new ArgumentException("App data directory is required.", nameof(directory));
        Volatile.Write(ref _directory, Path.GetFullPath(directory));
    }

    public static string CombinePath(string fileName) => Path.Combine(Directory, fileName);

    private static string GetDefaultDirectory()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
            localAppData = AppContext.BaseDirectory;
        return Path.Combine(localAppData, "Emuera");
    }
}
