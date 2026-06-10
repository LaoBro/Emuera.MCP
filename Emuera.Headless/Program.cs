using MinorShift.Emuera.GameView;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Runtime.Config.JSON;
using MinorShift.Emuera.Runtime.Utils;
using MinorShift.Emuera.Runtime.Utils.EvilMask;
using MinorShift.Emuera.Server;
using MinorShift.Emuera.UI.Game;
using System;
using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.Parsing;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Runtime;
using System.Threading;

namespace MinorShift.Emuera;

static partial class Program
{
    static readonly Option<string> exeDirOption = new(
        name: "--ExeDir",
        description: "游戏目录"
    );
    static readonly Option<bool> headlessOption = new(
        name: "--headless",
        description: "无头模式：不创建 GUI 窗口，通过 stdin/stdout 进行 JSONL 交互"
    );
    static readonly Option<bool> serverOption = new(
        name: "--server",
        description: "服务器模式：通过 HTTP 接口提供多会话服务"
    );
    static readonly Option<int> portOption = new(
        name: "--port",
        description: "服务器监听端口",
        getDefaultValue: () => 8080
    );

    [STAThread]
    static void Main(string[] args)
    {
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;

        var rootCommand = new RootCommand("Emuera.Headless");

        exeDirOption.AddAlias("-exedir");
        exeDirOption.AddAlias("-EXEDIR");
        rootCommand.AddOption(exeDirOption);

        headlessOption.AddAlias("-headless");
        headlessOption.AddAlias("-HEADLESS");
        rootCommand.AddOption(headlessOption);

        serverOption.AddAlias("-server");
        serverOption.AddAlias("-SERVER");
        rootCommand.AddOption(serverOption);

        portOption.AddAlias("-port");
        portOption.AddAlias("-PORT");
        rootCommand.AddOption(portOption);

        var result = rootCommand.Parse(args);
        var exeDir = result.GetValueForOption(exeDirOption);
        var headless = result.GetValueForOption(headlessOption);
        var server = result.GetValueForOption(serverOption);
        var port = result.GetValueForOption(portOption);

        IsHeadlessMode = true;

        if (exeDir != null)
            SetDirPaths(exeDir);

        ExeName = Path.GetFileNameWithoutExtension(AssemblyData.ExeName);

        ProfileOptimization.SetProfileRoot(exeDir ?? ExeDir);
        ProfileOptimization.StartProfile("profile");

        ConfigData.Instance.LoadConfig();
        JSONConfig.Load();

        Lang.LoadLanguageFiles();
        Lang.SetLanguage();

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

        // 字体文件加载
        if (Directory.Exists(FontDir))
        {
            foreach (string fontFile in Directory.GetFiles(FontDir, "*.ttf", SearchOption.AllDirectories))
                GlobalStatic.Pfc.AddFontFile(fontFile);
            foreach (string fontFile in Directory.GetFiles(FontDir, "*.otf", SearchOption.AllDirectories))
                GlobalStatic.Pfc.AddFontFile(fontFile);
        }

        if (server)
            RunServer(port);
        else
            RunHeadless();
    }

    private static void RunHeadless()
    {
        Console.Error.WriteLine($"[headless] Emuera {AssemblyData.EmueraVersionText} 无头模式启动");
        Console.Error.WriteLine($"[headless] 工作目录: {ExeDir}");
        Console.Error.WriteLine($"[headless] 协议类型: {(Console.IsInputRedirected ? "JSONL (管道)" : "CLI (终端)")}");

        var ui = new HeadlessConsole();
        var console = new EmueraConsole(ui);
        console.Initialize().Wait();

        var protocol = console.AgentBridge;
        if (protocol != null)
        {
            while (!protocol.IsStopped)
                Thread.Sleep(100);
        }
        else
        {
            Console.Error.WriteLine("[headless] 未检测到输入管道");
            Environment.Exit(1);
        }
    }

    private static void RunServer(int port)
    {
        Console.Error.WriteLine($"[server] Emuera {AssemblyData.EmueraVersionText} 服务器模式启动");
        Console.Error.WriteLine($"[server] 监听端口: {port}");

        using var server = new HttpGameServer(port);
        server.Start();

        Console.Error.WriteLine("[server] 按 Enter 键停止服务器...");
        Console.ReadLine();
    }

    [MemberNotNull(nameof(ExeDir), nameof(CsvDir), nameof(ErbDir), nameof(DebugDir), nameof(DatDir), nameof(ContentDir), nameof(SoundDir), nameof(FontDir))]
    private static void SetDirPaths(string exeDir)
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

    public static string ExeDir { get; private set; }
    public static string CsvDir { get; private set; }
    public static string ErbDir { get; private set; }
    public static string DebugDir { get; private set; }
    public static string DatDir { get; private set; }
    public static string ContentDir { get; private set; }
    public static string SoundDir { get; private set; }
    public static string FontDir { get; private set; }
    public static string ExeName { get; private set; }

    public static bool rebootFlag;
    public static bool AnalysisMode;
    public static List<string> AnalysisFiles;
    public static bool DebugMode { get; private set; }
    public static bool IsHeadlessMode { get; private set; }

    static Program()
    {
        var baseDirectory = AppContext.BaseDirectory;
        if (Directory.Exists(Path.Combine(baseDirectory, "Data", "erb")))
            baseDirectory = Path.Combine(baseDirectory, "Data");
        SetDirPaths(baseDirectory);
    }
}
