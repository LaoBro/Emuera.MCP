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

    private HeadlessOptions(string? exeDir, string protocol, bool server, int port, string termWidthHint)
    {
        ExeDir = exeDir;
        Protocol = protocol;
        Server = server;
        Port = port;
        TermWidthHint = termWidthHint;
    }

    internal static readonly Option<string> ExeDirOption = new(
        name: "--ExeDir",
        description: "游戏目录"
    );
    internal static readonly Option<string> ProtocolOption = new(
        name: "--protocol",
        description: "协议模式：auto(默认), jsonl, cli",
        getDefaultValue: () => "auto"
    );
    internal static readonly Option<bool> ServerOption = new(
        name: "--server",
        description: "服务器模式：通过 HTTP 接口提供单会话服务"
    );
    internal static readonly Option<int> PortOption = new(
        name: "--port",
        description: "服务器监听端口",
        getDefaultValue: () => 8080
    );
    internal static readonly Option<string> TermWidthHintOption = new(
        name: "--term-width-hint",
        description: "字符宽度提示：auto(默认,探测), cjk(全角), latin(半角)",
        getDefaultValue: () => "auto"
    );

    public static HeadlessOptions? Parse(string[] args)
    {
        var rootCommand = new RootCommand("Emuera.Headless - Emuera 无头模式运行器");

        ExeDirOption.AddAlias("-exedir");
        ExeDirOption.AddAlias("-EXEDIR");
        rootCommand.AddOption(ExeDirOption);

        ProtocolOption.AddAlias("-protocol");
        ProtocolOption.AddAlias("-PROTOCOL");
        rootCommand.AddOption(ProtocolOption);

        ServerOption.AddAlias("-server");
        ServerOption.AddAlias("-SERVER");
        rootCommand.AddOption(ServerOption);

        PortOption.AddAlias("-port");
        PortOption.AddAlias("-PORT");
        rootCommand.AddOption(PortOption);

        TermWidthHintOption.AddAlias("-term-width-hint");
        TermWidthHintOption.AddAlias("-TERM-WIDTH-HINT");
        rootCommand.AddOption(TermWidthHintOption);

        var result = rootCommand.Parse(args);

        if (Array.IndexOf(args, "--help") >= 0 || Array.IndexOf(args, "-h") >= 0 || Array.IndexOf(args, "-?") >= 0)
        {
            PrintHelp(rootCommand.Description);
            return null;
        }

        var exeDir = result.GetValueForOption(ExeDirOption);
        var protocol = result.GetValueForOption(ProtocolOption) ?? "auto";
        var server = result.GetValueForOption(ServerOption);
        var port = result.GetValueForOption(PortOption);
        var termWidthHint = result.GetValueForOption(TermWidthHintOption) ?? "auto";

        if (server && ArgsContainProtocol(args))
        {
            Console.Error.WriteLine("[server] server 模式不支持 --protocol 参数");
            Environment.Exit(1);
            return null;
        }

        return new HeadlessOptions(exeDir, protocol, server, port, termWidthHint);
    }

    private static void PrintHelp(string description)
    {
        Console.WriteLine(description);
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
