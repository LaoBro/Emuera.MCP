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

        ConfigData configData = new();
        configData.LoadConfig();
        // 候选 2 / ADR-0009：配置仅经 ambient scope 注入，再无静态单例。
        // 此处提前绑定，使 Lang.SetLanguage（scope 外）即可读取 ConfigData.Current / Config.Current。
        ConfigData.SetCurrent(configData);
        Config.SetCurrent(configData);
        JSONConfig.Load();

        Lang.LoadLanguageFiles();
        Lang.SetLanguage();

        // T-025 D1/D2：GamePaths.Validate 失败按模式分流——
        // - server 模式：降级 warn 继续（空闲启动，浏览器可打开选择器，真正的加载推迟到 /load-game）
        // - CLI 模式：打印提示 + 等待玩家按回车再退出（不再静默 Environment.Exit，避免双击时窗口一闪即关）
        //
        // CLI 等回车在非交互终端的边界（D17）：stdin 重定向到 /dev/null 或文件时 Console.ReadLine
        // 立即返 null/EOF → 进程退出，等同原 Environment.Exit(1)；CI/CD 不受影响。stdin 完全无句柄
        // 时 ReadLine 可能抛 InvalidOperationException——catch 兜底等同 EOF 退出。仅 stdin 是管道且
        // 管道不关闭（如 `echo | exe`）才会挂起，此场景罕见，可接受。
        try
        {
            paths.Validate();
        }
        catch (GamePathValidationException ex)
        {
            Console.Error.WriteLine($"[error] {ex.Code}: {ex.Message}");
            if (options.Server)
            {
                // D1：server 模式空闲启动——降级 warn 继续，真正的游戏加载推迟到 /load-game
                Console.Error.WriteLine("[server] 游戏目录校验失败，进入空闲模式。请在浏览器中选择游戏目录。");
            }
            else
            {
                // D2/D17：CLI 模式——打印提示后等回车再退出
                Console.Error.WriteLine("按回车键退出...");
                try
                {
                    Console.ReadLine();
                }
                catch (InvalidOperationException)
                {
                    // stdin 完全无句柄（CI 中非重定向而是无 stdin）——等同 EOF 退出
                }
                return;
            }
        }

        // 字体加载迁移至 runners 内 scope 打开后执行（ADR-0008：Pfc 是实例成员，随 scope 生灭）
        if (options.Server)
            await ServerRunner.RunAsync(options.Port, terminalSetup, configData);
        else
        {
            // ITerminalInput 在 HeadlessRunner 内创建：CLI 模式专属，stdin 重定向
            // 等环境错误由 HeadlessRunner 的 HeadlessFatalException 捕获块统一处理。
            await HeadlessRunner.RunAsync(paths, options.Protocol, options.TermWidthHint, terminalSetup, configData);
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
