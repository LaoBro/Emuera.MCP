using MinorShift.Emuera.GameView;
using MinorShift.Emuera.Runtime.Utils;
using MinorShift.Emuera.Runtime.Utils.EvilMask;
using MinorShift.Emuera.UI;
using MinorShift.Emuera.UI.Game;
using System;
using System.Threading;

namespace MinorShift.Emuera;

internal static class HeadlessRunner
{
    public static void Run(GamePaths paths, string protocolArg, string termWidthHint)
    {
        Console.Error.WriteLine($"[headless] Emuera {AssemblyData.EmueraVersionText} 无头模式启动");
        Console.Error.WriteLine($"[headless] 工作目录: {paths.ExeDir}");
        Console.Error.WriteLine($"[headless] 协议模式: {protocolArg}");
        Console.Error.WriteLine($"[headless] 字符宽度提示: {termWidthHint}");

        var ui = new HeadlessConsole();
        var console = new EmueraConsole(ui);

        WindowsConsoleHelper.TrySetConsoleSize();

        string hint = (termWidthHint ?? "auto").Trim().ToLowerInvariant();
        TerminalCharWidthConfig charWidthConfig;
        if (hint == "auto")
        {
            charWidthConfig = TerminalDisplayWidth.DetectCharWidths();
        }
        else
        {
            charWidthConfig = TerminalDisplayWidth.ApplyWidthHint(hint);
        }
        console.CharWidthConfig = charWidthConfig;

        WindowsConsoleHelper.DetectConsoleFont();
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

        try
        {
            console.Initialize().GetAwaiter().GetResult();

            if (protocol is AgentJsonlProtocol jsonl)
                jsonl.RunLoopAsync(enableTimeout: false, CancellationToken.None).GetAwaiter().GetResult();
            else if (protocol is AgentCliProtocol cli)
                cli.RunCliLoop();
            else
                Console.Error.WriteLine("[headless] 非 CLI 协议，无法启动终端交互");
        }
        catch (GameExitException)
        {
            // 脚本 QUIT/EXIT：静默退出 0
        }
    }

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
}
