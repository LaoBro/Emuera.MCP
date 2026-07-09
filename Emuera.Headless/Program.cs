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

        // 字体加载迁移至 runners 内 scope 打开后执行（ADR-0008：Pfc 是实例成员，随 scope 生灭）
        if (options.Server)
            await ServerRunner.RunAsync(options.Port, terminalSetup);
        else
        {
            // ITerminalInput 在 HeadlessRunner 内创建：CLI 模式专属，stdin 重定向
            // 等环境错误由 HeadlessRunner 的 HeadlessFatalException 捕获块统一处理。
            await HeadlessRunner.RunAsync(paths, options.Protocol, options.TermWidthHint, terminalSetup);
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

    /// <summary>
    /// 加载字体文件到当前 scope 的 GlobalStatic.Pfc。
    /// 必须在 OpenScope() 之后调用（ADR-0008：Pfc 是实例成员，随 scope 生灭）。
    /// </summary>
    internal static void LoadFonts()
    {
        var fontDir = GamePaths.Current.FontDir;
        if (!Directory.Exists(fontDir)) return;
        foreach (string fontFile in Directory.GetFiles(fontDir, "*.ttf", SearchOption.AllDirectories))
            GlobalStatic.Pfc.AddFontFile(fontFile);
        foreach (string fontFile in Directory.GetFiles(fontDir, "*.otf", SearchOption.AllDirectories))
            GlobalStatic.Pfc.AddFontFile(fontFile);
    }

    static Program()
    {
        GamePaths.Resolve(null);
    }
}
