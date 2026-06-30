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

    private static string DetectDefault()
    {
        var baseDirectory = AppContext.BaseDirectory;
        if (Directory.Exists(Path.Combine(baseDirectory, "Data", "erb")))
            baseDirectory = Path.Combine(baseDirectory, "Data");
        return baseDirectory;
    }

    public void Validate()
    {
        if (!Directory.Exists(CsvDir))
        {
            Console.Error.WriteLine($"[error] CSV 目录不存在: {CsvDir}");
            Environment.Exit(1);
        }
        if (!Directory.Exists(ErbDir))
        {
            Console.Error.WriteLine($"[error] ERB 目录不存在: {ErbDir}");
            Environment.Exit(1);
        }
    }
}
