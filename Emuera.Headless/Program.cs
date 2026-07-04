using MinorShift.Emuera.GameView;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Runtime.Config.JSON;
using MinorShift.Emuera.Runtime.Utils;
using MinorShift.Emuera.Runtime.Utils.EvilMask;
using MinorShift.Emuera.Terminal.Platform;
using MinorShift.Emuera.UI;
using System;
using System.Collections.Generic;
using System.CommandLine;
using System.Globalization;
using System.IO;
using System.Runtime;
using System.Threading.Tasks;

namespace MinorShift.Emuera;

static partial class Program
{
    [STAThread]
    static async Task Main(string[] args)
    {
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        var terminalSetup = CreateTerminalSetup();
        terminalSetup.TryEnableAnsi();

        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;

        var options = HeadlessOptions.Parse(args);
        if (options == null) return;

        var paths = GamePaths.Resolve(options.ExeDir);
        ExeName = Path.GetFileNameWithoutExtension(AssemblyData.ExeName);

        ProfileOptimization.SetProfileRoot(options.ExeDir ?? paths.ExeDir);
        ProfileOptimization.StartProfile("profile");

        ConfigData.Instance.LoadConfig();
        JSONConfig.Load();

        Lang.LoadLanguageFiles();
        Lang.SetLanguage();

        paths.Validate();

        if (Directory.Exists(paths.FontDir))
        {
            foreach (string fontFile in Directory.GetFiles(paths.FontDir, "*.ttf", SearchOption.AllDirectories))
                GlobalStatic.Pfc.AddFontFile(fontFile);
            foreach (string fontFile in Directory.GetFiles(paths.FontDir, "*.otf", SearchOption.AllDirectories))
                GlobalStatic.Pfc.AddFontFile(fontFile);
        }

        if (options.Server)
            ServerRunner.Run(options.Port, terminalSetup);
        else
        {
            using ITerminalInput terminalInput = OperatingSystem.IsWindows()
                ? new WindowsTerminalInput()
                : new PosixTerminalInput();
            await HeadlessRunner.RunAsync(paths, options.Protocol, options.TermWidthHint, terminalSetup, terminalInput);
        }
    }

    // === 路径属性转发（保持共享文件零改动）===
    public static string ExeDir => GamePaths.Current.ExeDir;
    public static string CsvDir => GamePaths.Current.CsvDir;
    public static string ErbDir => GamePaths.Current.ErbDir;
    public static string DebugDir => GamePaths.Current.DebugDir;
    public static string DatDir => GamePaths.Current.DatDir;
    public static string ContentDir => GamePaths.Current.ContentDir;
    public static string SoundDir => GamePaths.Current.SoundDir;
    public static string FontDir => GamePaths.Current.FontDir;
    public static string ExeName { get; private set; } = "";

    // === 运行时状态 ===
    public static bool rebootFlag;
    public static bool AnalysisMode;
    public static List<string> AnalysisFiles = new();
    public static bool DebugMode { get; private set; }

    private static ITerminalSetup CreateTerminalSetup()
    {
        if (OperatingSystem.IsWindows())
            return new WindowsTerminalSetup();

        return new PosixTerminalSetup();
    }

    static Program()
    {
        GamePaths.Resolve(null);
    }
}
