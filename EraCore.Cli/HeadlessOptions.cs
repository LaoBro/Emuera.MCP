using MinorShift.Emuera.GameView;
using System;
using System.CommandLine;
using System.CommandLine.Parsing;

namespace MinorShift.Emuera;

internal sealed class HeadlessOptions
{
    public string? ExeDir { get; }
    public string Protocol { get; }
    public bool Server { get; }
    public int Port { get; }
    public string TermWidthHint { get; }
    public bool NoLoadingReport { get; }

    private HeadlessOptions(string? exeDir, string protocol, bool server, int port, string termWidthHint, bool noLoadingReport)
    {
        ExeDir = exeDir;
        Protocol = protocol;
        Server = server;
        Port = port;
        TermWidthHint = termWidthHint;
        NoLoadingReport = noLoadingReport;
    }

    // 2.0 GA API：构造器直接接收 name + 别名，描述与默认值通过初始化器设置
    internal static readonly Option<string> ExeDirOption = new(
        "--ExeDir", "-exedir", "-EXEDIR")
    {
        Description = "游戏目录（包含 CSV/ERB 等子目录）"
    };

    internal static readonly Option<string> ProtocolOption = new(
        "--protocol", "-protocol", "-PROTOCOL")
    {
        Description = "协议模式：auto(默认,检测终端), cli。stdin 管道模式已废弃（T-024），脚本/自动化请使用 --server",
        DefaultValueFactory = _ => "auto"
    };

    internal static readonly Option<bool> ServerOption = new(
        "--server", "-server", "-SERVER")
    {
        Description = "服务器模式：通过 HTTP 接口提供单会话服务"
    };

    internal static readonly Option<int> PortOption = new(
        "--port", "-port", "-PORT")
    {
        Description = "服务器监听端口（默认 8080）",
        DefaultValueFactory = _ => 8080
    };

    internal static readonly Option<string> TermWidthHintOption = new(
        "--term-width-hint", "-term-width-hint", "-TERM-WIDTH-HINT")
    {
        Description = "字符宽度提示：auto(默认,探测), cjk(全角), latin(半角)",
        DefaultValueFactory = _ => "auto"
    };

    internal static readonly Option<bool> NoLoadingReportOption = new(
        "--no-loading-report")
    {
        Description = "覆盖游戏配置的加载时显示报告（DisplayReport）为 off，隐藏启动读取日志（agent 用）"
    };

    public static HeadlessOptions? Parse(string[] args)
    {
        var rootCommand = new RootCommand("EraCore - Emuera 无头模式运行器");
        rootCommand.Options.Add(ExeDirOption);
        rootCommand.Options.Add(ProtocolOption);
        rootCommand.Options.Add(ServerOption);
        rootCommand.Options.Add(PortOption);
        rootCommand.Options.Add(TermWidthHintOption);
        rootCommand.Options.Add(NoLoadingReportOption);

        var result = rootCommand.Parse(args);

        // --help / -h / -? / --version：交给内置 Invoke() 输出后退出
        if (Array.IndexOf(args, "--help") >= 0 ||
            Array.IndexOf(args, "-h") >= 0 ||
            Array.IndexOf(args, "-?") >= 0 ||
            Array.IndexOf(args, "--version") >= 0)
        {
            result.Invoke();
            return null;
        }

        // 解析错误：输出错误信息后退出
        if (result.Errors.Count > 0)
        {
            foreach (var err in result.Errors)
                EmueraLog.Error("options", err.Message);
            return null;
        }

        var exeDir = result.GetValue(ExeDirOption);
        var protocol = result.GetValue(ProtocolOption) ?? "auto";
        var server = result.GetValue(ServerOption);
        var port = result.GetValue(PortOption);
        var termWidthHint = result.GetValue(TermWidthHintOption) ?? "auto";
        var noLoadingReport = result.GetValue(NoLoadingReportOption);

        if (server && ArgsContainProtocol(args))
        {
            EmueraLog.Error("server", "server 模式不支持 --protocol 参数");
            Environment.Exit(1);
            return null;
        }

        return new HeadlessOptions(exeDir, protocol, server, port, termWidthHint, noLoadingReport);
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
}
