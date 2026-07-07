using MinorShift.Emuera.GameView;
using MinorShift.Emuera.Runtime.Utils;
using MinorShift.Emuera.Runtime.Utils.EvilMask;
using MinorShift.Emuera.Terminal.Platform;
using MinorShift.Emuera.UI;
using MinorShift.Emuera.UI.Game;
using MinorShift.Emuera.Runtime.Config;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace MinorShift.Emuera;

internal static class HeadlessRunner
{
    public static async Task RunAsync(GamePaths paths, string protocolArg, string termWidthHint, ITerminalSetup terminalSetup)
    {
        Console.Error.WriteLine($"[headless] Emuera {AssemblyData.EmueraVersionText} 无头模式启动");
        Console.Error.WriteLine($"[headless] 工作目录: {paths.ExeDir}");
        Console.Error.WriteLine($"[headless] 协议模式: {protocolArg}");
        Console.Error.WriteLine($"[headless] 字符宽度提示: {termWidthHint}");

        try
        {
            ITerminalInput terminalInput;
            try
            {
                terminalInput = OperatingSystem.IsWindows()
                    ? new WindowsTerminalInput()
                    : new PosixTerminalInput();
            }
            catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException)
            {
                // stdin 重定向 / 终端不支持 raw 模式等环境错误——视为 fatal。
                // ADR-0005：CLI 协议层 VT-only，无法在非交互式终端运行。
                throw new HeadlessFatalException(
                    "终端输入初始化失败：" + ex.Message + "。" +
                    "请在真实交互式终端运行（如 Windows Terminal / ConPTY / 现代 SSH），" +
                    "或改用 --server 模式。",
                    ex);
            }
            using (terminalInput)
            {
                await RunAsyncCore(paths, protocolArg, termWidthHint, terminalSetup, terminalInput);
            }
        }
        catch (HeadlessFatalException ex)
        {
            // ADR-0005：CLI 协议层 VT 初始化失败等不可恢复的环境问题。
            // 输出 stderr 提示（含原因 + 解决方案）+ 写 AgentLog，然后非零退出。
            Console.Error.WriteLine($"[headless] 致命错误: {ex.Message}");
            Console.Error.WriteLine("[headless] 进程将以非零退出码终止。");
            Console.Error.Flush();
            AgentLog.Instance.Write("HeadlessFatalException: " + ex);
            Environment.Exit(1);
        }
    }

    private static async Task RunAsyncCore(GamePaths paths, string protocolArg, string termWidthHint, ITerminalSetup terminalSetup, ITerminalInput terminalInput)
    {
        var ui = new HeadlessConsole();
        var console = new EmueraConsole(ui, terminalSetup);

        int charWidth = Math.Max(Config.FontSize / 2, 1);
        int gameColumns = Config.DrawableWidth / charWidth;
        int gameRows = Config.WindowY / Config.LineHeight;
        if (gameColumns > 0 && gameRows > 0)
            terminalSetup.TrySetConsoleSize(gameColumns, gameRows + 4);

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

        terminalSetup.DetectFont();
        PrintTerminalGuidance(charWidthConfig);

        AgentProtocolBase? protocol;
        try
        {
            protocol = SelectProtocol(protocolArg, console, ui, terminalSetup, terminalInput);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"[headless] {ex.Message}");
            Environment.Exit(1);
            return;
        }

        if (protocol == null)
        {
            Console.Error.WriteLine("[headless] 无法确定协议模式；请使用 --protocol cli 或 --server 模式");
            Environment.Exit(1);
            return;
        }

        console.SetAgentBridge(protocol);

        try
        {
            await console.Initialize();

            if (protocol is AgentCliProtocol cli)
                cli.RunCliLoop();
            else
                Console.Error.WriteLine("[headless] 无法启动终端交互；请使用 --protocol cli 或 --server 模式");
        }
        catch (GameExitException)
        {
            // 脚本 QUIT/EXIT：静默退出 0
        }
        // HeadlessFatalException 不在此处捕获——向上传播到 RunAsync 统一处理。
    }

    private static AgentCliProtocol? SelectProtocol(string protocolArg, EmueraConsole console, IConsoleUI ui, ITerminalSetup terminalSetup, ITerminalInput terminalInput)
    {
        return protocolArg.Trim().ToLowerInvariant() switch
        {
            "auto" => DetectProtocol(console, ui, terminalSetup, terminalInput),
            "cli" => new AgentCliProtocol(console, ui, terminalSetup, terminalInput),
            "jsonl" => throw new ArgumentException(
                "stdin 管道 JSONL 模式已废弃（T-024），请使用 --server 模式", nameof(protocolArg)),
            _ => throw new ArgumentException($"未知协议模式: {protocolArg}", nameof(protocolArg))
        };
    }

    private static AgentCliProtocol? DetectProtocol(EmueraConsole console, IConsoleUI ui, ITerminalSetup terminalSetup, ITerminalInput terminalInput)
    {
        // T-024：stdin 管道模式已废弃，非 server 模式仅支持交互式 CLI 终端。
        if (Console.IsInputRedirected)
        {
            Console.Error.WriteLine("[headless] stdin 管道模式已废弃（T-024），请使用 --server 模式或交互式终端");
            return null;
        }

        try
        {
            _ = Console.KeyAvailable;
        }
        catch
        {
            return null;
        }

        return new AgentCliProtocol(console, ui, terminalSetup, terminalInput);
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
