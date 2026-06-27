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
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;

namespace MinorShift.Emuera;

static partial class Program
{
    static readonly Option<string> exeDirOption = new(
        name: "--ExeDir",
        description: "游戏目录"
    );
    static readonly Option<string> protocolOption = new(
        name: "--protocol",
        description: "协议模式：auto(默认), jsonl, cli",
        getDefaultValue: () => "auto"
    );
    static readonly Option<bool> serverOption = new(
        name: "--server",
        description: "服务器模式：通过 HTTP 接口提供单会话服务"
    );
    static readonly Option<int> portOption = new(
        name: "--port",
        description: "服务器监听端口",
        getDefaultValue: () => 8080
    );
    static readonly Option<string> termWidthHintOption = new(
        name: "--term-width-hint",
        description: "字符宽度提示：auto(默认,探测), cjk(全角), latin(半角)",
        getDefaultValue: () => "auto"
    );

    [STAThread]
    static void Main(string[] args)
    {
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        TrySetupWindowsConsole();
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;

        var rootCommand = new RootCommand("Emuera.Headless - Emuera 无头模式运行器");

        exeDirOption.AddAlias("-exedir");
        exeDirOption.AddAlias("-EXEDIR");
        rootCommand.AddOption(exeDirOption);

        protocolOption.AddAlias("-protocol");
        protocolOption.AddAlias("-PROTOCOL");
        rootCommand.AddOption(protocolOption);

        serverOption.AddAlias("-server");
        serverOption.AddAlias("-SERVER");
        rootCommand.AddOption(serverOption);

        portOption.AddAlias("-port");
        portOption.AddAlias("-PORT");
        rootCommand.AddOption(portOption);

        termWidthHintOption.AddAlias("-term-width-hint");
        termWidthHintOption.AddAlias("-TERM-WIDTH-HINT");
        rootCommand.AddOption(termWidthHintOption);

        var result = rootCommand.Parse(args);

        if (Array.IndexOf(args, "--help") >= 0 || Array.IndexOf(args, "-h") >= 0 || Array.IndexOf(args, "-?") >= 0)
        {
            Console.WriteLine(rootCommand.Description);
            Console.WriteLine();
            Console.WriteLine("用法: Emuera.Headless [选项]");
            Console.WriteLine();
            Console.WriteLine("选项:");
            Console.WriteLine("  --ExeDir <路径>       游戏目录（包含 CSV/ERB 等子目录）");
            Console.WriteLine("  --protocol <模式>     协议模式：auto(默认), jsonl, cli");
            Console.WriteLine("  --server              服务器模式：通过 HTTP 接口提供单会话服务");
            Console.WriteLine("  --port <端口>         服务器监听端口（默认 8080）");
            Console.WriteLine("  --term-width-hint <模式>  字符宽度提示：auto(默认,探测), cjk(全角), latin(半角)");
            Console.WriteLine("  --help, -h, -?        显示帮助信息");
            Console.WriteLine();
            Console.WriteLine("协议模式说明:");
            Console.WriteLine("  auto    自动检测：stdin 为管道时使用 jsonl，否则使用 cli");
            Console.WriteLine("  jsonl   JSONL 协议：每行一个 JSON 对象，适合程序间通信");
            Console.WriteLine("  cli     CLI 协议：终端交互模式，支持键盘输入");
            Console.WriteLine("  server  HTTP 服务器：通过 REST API 提供单会话服务");
            return;
        }

        var exeDir = result.GetValueForOption(exeDirOption);
        var protocolArg = result.GetValueForOption(protocolOption);
        var server = result.GetValueForOption(serverOption);
        var port = result.GetValueForOption(portOption);
        var termWidthHint = result.GetValueForOption(termWidthHintOption) ?? "auto";

        if (server && ArgsContainProtocol(args))
        {
            Console.Error.WriteLine("[server] server 模式不支持 --protocol 参数");
            Environment.Exit(1);
            return;
        }

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
            RunHeadless(protocolArg, termWidthHint);
    }

    private static void RunHeadless(string protocolArg, string termWidthHint)
    {
        Console.Error.WriteLine($"[headless] Emuera {AssemblyData.EmueraVersionText} 无头模式启动");
        Console.Error.WriteLine($"[headless] 工作目录: {ExeDir}");
        Console.Error.WriteLine($"[headless] 协议模式: {protocolArg}");
        Console.Error.WriteLine($"[headless] 字符宽度提示: {termWidthHint}");

        var ui = new HeadlessConsole();
        var console = new EmueraConsole(ui);

        // 尝试设置终端宽度匹配游戏配置宽度
        TrySetConsoleSize();

        // 应用用户提供的字符宽度提示（cjk/latin 强制覆盖；auto 交由探测决定）
        string hint = (termWidthHint ?? "auto").Trim().ToLowerInvariant();
        TerminalCharWidthConfig charWidthConfig;
        if (hint == "auto")
        {
            // 探测各字符组在当前终端的实际渲染宽度
            // 在 conhost、Windows Terminal(conpty) 中可靠；重定向/mintty 下保持默认
            charWidthConfig = TerminalDisplayWidth.DetectCharWidths();
        }
        else
        {
            charWidthConfig = TerminalDisplayWidth.ApplyWidthHint(hint);
        }
        console.CharWidthConfig = charWidthConfig;

        // 读取并打印当前终端字体名（仅诊断信息，不参与判断）
        DetectConsoleFont();

        // 基于探测结果给出字体配置建议
        PrintTerminalGuidance(charWidthConfig);

        AgentProtocolBase? protocol;
        try
        {
            protocol = SelectProtocol(protocolArg, console, ui);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"[headless] {ex.Message}");
            Environment.Exit(1);
            return;
        }

        if (protocol == null)
        {
            Console.Error.WriteLine("[headless] 无法确定协议模式；请使用 --protocol jsonl 或 --protocol cli");
            Environment.Exit(1);
            return;
        }

        console.SetAgentBridge(protocol);
        console.Initialize().Wait();

        if (protocol is AgentJsonlProtocol jsonl)
            jsonl.RunLoop(enableTimeout: false, CancellationToken.None);
        else if (protocol is AgentCliProtocol)
            RunCliLoop(protocol);
    }

    private static void TrySetConsoleSize()
    {
        if (Console.IsInputRedirected) return;
        try
        {
            int charWidth = Math.Max(Config.FontSize / 2, 1);
            int gameColumns = Config.DrawableWidth / charWidth;
            int gameRows = Config.WindowY / Config.LineHeight;

            if (gameColumns <= 0 || gameRows <= 0) return;

            if (IsWindowsTerminal())
            {
                // Windows Terminal(conpty) 不支持通过 Console.WindowWidth/Height
                // 调整物理窗口——设置伪缓冲区视口会使其与物理窗口不同步，
                // 渲染按伪缓冲区宽度换行但物理窗口更窄，造成错位。
                // 改用 VT 序列 CSI 8 ; rows ; cols t（XTWINOPS）请求调整物理窗口；
                // 若终端不支持则被忽略（无害），渲染仍基于实际 WindowWidth/Height。
                int rows = gameRows + 4;
                Console.Write($"\x1b[8;{rows};{gameColumns}t");
                return;
            }

            // conhost：先设置缓冲区大小（必须 >= 窗口大小），再设置窗口大小
            if (Console.BufferWidth < gameColumns)
                Console.BufferWidth = gameColumns;
            if (Console.BufferHeight < gameRows + 10)
                Console.BufferHeight = gameRows + 10;

            Console.WindowWidth = Math.Min(gameColumns, Console.LargestWindowWidth);
            Console.WindowHeight = Math.Min(gameRows + 4, Console.LargestWindowHeight);
        }
        catch { }
    }

    /// <summary>检测是否运行在 Windows Terminal（conpty）下。</summary>
    private static bool IsWindowsTerminal()
        => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WT_SESSION"));

    private static AgentProtocolBase? SelectProtocol(string protocolArg, EmueraConsole console, IConsoleUI ui)
    {
        return protocolArg.Trim().ToLowerInvariant() switch
        {
            "auto" => DetectProtocol(console, ui),
            "jsonl" => new AgentJsonlProtocol(console, ui),
            "cli" => new AgentCliProtocol(console, ui),
            _ => throw new ArgumentException($"未知协议模式: {protocolArg}", nameof(protocolArg))
        };
    }

    private static AgentProtocolBase? DetectProtocol(EmueraConsole console, IConsoleUI ui)
    {
        if (Console.IsInputRedirected)
        {
            using var stdin = Console.OpenStandardInput();
            if (stdin.CanSeek)
                return null;

            return new AgentJsonlProtocol(console, ui);
        }

        try
        {
            _ = Console.KeyAvailable;
        }
        catch
        {
            return null;
        }

        return new AgentCliProtocol(console, ui);
    }

    private static void RunCliLoop(AgentProtocolBase protocol)
    {
        if (protocol is AgentCliProtocol cli)
            cli.RunCliLoop();
        else
            Console.Error.WriteLine("[headless] 非 CLI 协议，无法启动终端交互");
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

    private static bool ArgsContainProtocol(string[] args)
    {
        foreach (var arg in args)
        {
            if (arg.Equals("--protocol", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("-protocol", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
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

    public static bool AnsiEnabled { get; private set; }

    private static void TrySetupWindowsConsole()
    {
        if (!OperatingSystem.IsWindows()) return;
        if (Console.IsInputRedirected) return;
        TryEnableVirtualTerminal();
        // 不再自动设置终端字体：SetCurrentConsoleFontEx 在 Windows Terminal(conpty)
        // 和 Git Bash(mintty/winpty) 下均无效，仅在老式 conhost 中生效。
        // 强行设置会导致不同终端效果不一致，改为探测渲染宽度 + 提示用户手动配置。
    }

    private static void TryEnableVirtualTerminal()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            IntPtr hOut = GetStdHandle(STD_OUTPUT_HANDLE);
            if (hOut == IntPtr.Zero || hOut == INVALID_HANDLE_VALUE) return;
            if (!GetConsoleMode(hOut, out uint mode)) return;
            if ((mode & ENABLE_VIRTUAL_TERMINAL_PROCESSING) != 0)
            {
                AnsiEnabled = true;
                return;
            }
            if (SetConsoleMode(hOut, mode | ENABLE_VIRTUAL_TERMINAL_PROCESSING))
                AnsiEnabled = true;
        }
        catch { }
    }

    private static void DetectConsoleFont()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            // 尝试通过 CONOUT$ 获取真正的控制台句柄
            // GetStdHandle 在管道重定向时可能返回非控制台句柄
            IntPtr hOut = CreateFileW("CONOUT$", GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            bool openedConout = hOut != IntPtr.Zero && hOut != INVALID_HANDLE_VALUE;

            if (!openedConout)
            {
                hOut = GetStdHandle(STD_OUTPUT_HANDLE);
                if (hOut == IntPtr.Zero || hOut == INVALID_HANDLE_VALUE)
                {
                    Console.Error.WriteLine("[terminal] Console font: failed to get console handle");
                    return;
                }
            }

            var info = new CONSOLE_FONT_INFO_EX();
            info.cbSize = (uint)Marshal.SizeOf<CONSOLE_FONT_INFO_EX>();
            if (!GetCurrentConsoleFontEx(hOut, false, ref info))
            {
                int err = Marshal.GetLastWin32Error();
                Console.Error.WriteLine($"[terminal] Console font: GetCurrentConsoleFontEx failed, error={err}");
                if (openedConout) CloseHandle(hOut);
                return;
            }
            Console.Error.WriteLine($"[terminal] Console font: {info.FaceName}, size={info.dwFontSizeX}x{info.dwFontSizeY}");
            Console.Error.WriteLine("[terminal] (注意: Windows Terminal/Git Bash 下字体名可能不准确，以宽度探测结果为准)");

            if (openedConout) CloseHandle(hOut);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[terminal] Console font: exception - {ex.Message}");
        }
    }

    /// <summary>
    /// 基于字符宽度探测结果，向用户给出补偿说明与字体建议。
    /// 探测解决的是终端 cell allocation（占位列数），输出时补空格/替换补偿的是对齐。
    /// 但字体字形宽度与 cell allocation 不匹配时仍会视觉异常：
    ///   - cell=1 但字形全角（如 WT+MS Gothic 的 █）→ 字形溢出与相邻字符重叠
    ///   - cell=2（含补空格）但字形半角（如 WT 的 ━）→ 空白列产生线条空缺
    /// 因此仍需建议用户选择字形宽度与 cell allocation 匹配的字体。
    /// </summary>
    private static void PrintTerminalGuidance(TerminalCharWidthConfig config)
    {
        bool anySymbolHalf = !config.BoxDrawingIsWide
                          || !config.GeometricIsWide
                          || !config.MiscSymbolsIsWide;
        bool blockReplaced = config.BlockElementsIsWide;

        if (anySymbolHalf)
        {
            Console.Error.WriteLine("[terminal] 检测到制表符/几何/符号字符被渲染为半角，已自动补空格补偿对齐。");
        }
        if (blockReplaced)
        {
            Console.Error.WriteLine("[terminal] 检测到方块字符被渲染为全角，已自动替换为盲文点阵以保持游戏布局。");
        }
        if (anySymbolHalf || blockReplaced)
        {
            Console.Error.WriteLine("[terminal] 建议：选择字形宽度与终端占位匹配的字体可改善视觉效果。");
            Console.Error.WriteLine("[terminal]   若占位半角但字形全角（字符重叠），选字形本身为半角的字体（如 Cascadia Mono）。");
            Console.Error.WriteLine("[terminal]   若补空格后出现线条空缺，选字形本身为全角的字体（如 MS Gothic）。");
            Console.Error.WriteLine("[terminal]   理想字体：各字符组的字形宽度恰好等于终端的占位列数。");
        }
        if (!anySymbolHalf && !blockReplaced)
        {
            Console.Error.WriteLine("[terminal] 字符宽度检测正常，无需额外补偿。");
        }
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetCurrentConsoleFontEx(IntPtr hConsoleOutput, bool bMaximumWindow, ref CONSOLE_FONT_INFO_EX lpConsoleCurrentFontEx);

    private const int STD_OUTPUT_HANDLE = -11;
    private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);
    private const uint ENABLE_VIRTUAL_TERMINAL_PROCESSING = 0x0004;
    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ = 0x00000001;
    private const uint FILE_SHARE_WRITE = 0x00000002;
    private const uint OPEN_EXISTING = 3;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CONSOLE_FONT_INFO_EX
    {
        public uint cbSize;
        public uint nFont;
        public short dwFontSizeX;
        public short dwFontSizeY;
        public uint FontFamily;
        public uint FontWeight;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string FaceName;
    }

    static Program()
    {
        var baseDirectory = AppContext.BaseDirectory;
        if (Directory.Exists(Path.Combine(baseDirectory, "Data", "erb")))
            baseDirectory = Path.Combine(baseDirectory, "Data");
        SetDirPaths(baseDirectory);
    }
}
