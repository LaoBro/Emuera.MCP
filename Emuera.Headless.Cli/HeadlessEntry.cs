using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Runtime.Config.JSON;
using MinorShift.Emuera.Runtime.Utils;
using MinorShift.Emuera.Runtime.Utils.EvilMask;
using MinorShift.Emuera.Terminal.Platform;
using System;
using System.Globalization;
using System.IO;
using System.Runtime;
using System.Threading.Tasks;

namespace MinorShift.Emuera;

/// <summary>
/// Cli 入口点。拆分自原 Emuera.Headless.Program.Main（issue 01）。
/// <para>
/// 三项目拆分后，Core 是 Library（无 Main），Cli 是 Exe——入口由 Emuera.Headless.Cli.dll 提供。
/// <c>Program</c> 留 Core 仅含路径转发 + 状态字段（50+ 处 Shared/ 调用零改动）。
/// </para>
/// <para>
/// issue 02 将抽取 <c>EmueraRuntimeInitializer.Initialize(GamePaths paths)</c> 封装下面共享初始化
/// （encoding/culture/terminalSetup/ConfigData/Config/JSONConfig/Lang/Validate），届时 Main 仅保留
/// HeadlessOptions.Parse + GamePaths.Resolve + runner 分流。
/// </para>
/// </summary>
internal static class HeadlessEntry
{
    [STAThread]
    internal static async Task Main(string[] args)
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
        Program.ExeName = Path.GetFileNameWithoutExtension(AssemblyData.ExeName);

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

    private static ITerminalSetup CreateTerminalSetup()
    {
        if (OperatingSystem.IsWindows())
            return new WindowsTerminalSetup();

        return new PosixTerminalSetup();
    }
}
