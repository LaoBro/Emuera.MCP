using System;
using System.IO;

namespace MinorShift.Emuera;

internal sealed class GamePaths
{
    public static GamePaths Current { get; private set; } = null!;

    public string ExeDir { get; }
    public string CsvDir { get; }
    public string ErbDir { get; }
    public string DebugDir { get; }
    public string DatDir { get; }
    public string ContentDir { get; }
    public string SoundDir { get; }
    public string FontDir { get; }

    private GamePaths(string exeDir)
    {
        ExeDir = Path.GetFullPath(new DirectoryInfo(exeDir).FullName + Path.DirectorySeparatorChar);
        CsvDir = Path.Combine(ExeDir, "csv") + Path.DirectorySeparatorChar;
        ErbDir = Path.Combine(ExeDir, "erb") + Path.DirectorySeparatorChar;
        DebugDir = Path.Combine(ExeDir, "debug") + Path.DirectorySeparatorChar;
        DatDir = Path.Combine(ExeDir, "dat") + Path.DirectorySeparatorChar;
        ContentDir = Path.Combine(ExeDir, "resources") + Path.DirectorySeparatorChar;
        SoundDir = Path.Combine(ExeDir, "sound") + Path.DirectorySeparatorChar;
        FontDir = Path.Combine(ExeDir, "font") + Path.DirectorySeparatorChar;
    }

    public static GamePaths Resolve(string? exeDirArg)
    {
        var baseDirectory = exeDirArg ?? DetectDefault();
        var paths = new GamePaths(baseDirectory);
        Current = paths;
        return paths;
    }

    /// <summary>
    /// 显式重设静态 <see cref="Current"/>——issue 05 /load-game 校验失败时回滚到旧值。
    /// 仅 server 内部使用，CLI / 启动期不调。
    /// </summary>
    internal static void SetCurrent(GamePaths paths)
    {
        Current = paths;
    }

    private static string DetectDefault()
    {
        var baseDirectory = AppContext.BaseDirectory;
        if (Directory.Exists(Path.Combine(baseDirectory, "Data", "erb")))
            baseDirectory = Path.Combine(baseDirectory, "Data");
        return baseDirectory;
    }

    /// <summary>
    /// 校验游戏目录结构。失败时抛 <see cref="GamePathValidationException"/>（issue 05）——
    /// 旧实现调 <c>Environment.Exit(1)</c>，会让 /load-game 校验失败时杀死整个 server 进程，
    /// 故改为抛异常：CLI 启动路径在 Program.Main 捕获后退出，server /load-game 路径捕获后返 400。
    /// </summary>
    public void Validate()
    {
        if (!Directory.Exists(ExeDir))
            throw new GamePathValidationException("DIR_NOT_FOUND", $"目录不存在: {ExeDir}");
        if (!Directory.Exists(CsvDir))
            throw new GamePathValidationException("MISSING_CSV", $"缺少 csv 目录: {CsvDir}");
        if (!Directory.Exists(ErbDir))
            throw new GamePathValidationException("MISSING_ERB", $"缺少 erb 目录: {ErbDir}");
    }
}

/// <summary>
/// 游戏目录校验失败异常（issue 05）。<see cref="Code"/> 与前端错误契约对称：
/// <c>DIR_NOT_FOUND</c> / <c>MISSING_CSV</c> / <c>MISSING_ERB</c>。
/// </summary>
internal sealed class GamePathValidationException : Exception
{
    public string Code { get; }
    public GamePathValidationException(string code, string message) : base(message)
    {
        Code = code;
    }
}
