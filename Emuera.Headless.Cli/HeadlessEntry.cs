using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Terminal.Platform;
using System;
using System.Threading.Tasks;

namespace MinorShift.Emuera;

/// <summary>
/// Cli 入口点。拆分自原 Emuera.Headless.Program.Main（issue 01）。
/// <para>
/// 三项目拆分后，Core 是 Library（无 Main），Cli 是 Exe——入口由 Emuera.Headless.Cli.dll 提供。
/// <c>Program</c> 留 Core 仅含路径转发 + 状态字段（50+ 处 Shared/ 调用零改动）。
/// </para>
/// <para>
/// issue 02 已抽取 <see cref="EmueraRuntimeInitializer.Initialize"/> 封装共享运行时初始化
/// （encoding/culture/terminalSetup/Program.ExeName/ConfigData/Config/JSONConfig/Lang/Validate），
/// Main 仅保留 <see cref="HeadlessOptions.Parse"/> + <see cref="GamePaths.Resolve"/> + 校验失败处置
/// + runner 分流（<see cref="ServerRunner"/> / <see cref="HeadlessRunner"/>）。MAUI 入口（issue 07 落地）
/// 同调 <see cref="EmueraRuntimeInitializer.Initialize"/>。
/// </para>
/// </summary>
internal static class HeadlessEntry
{
    [STAThread]
    internal static async Task Main(string[] args)
    {
        // Console.OutputEncoding 必须在 HeadlessOptions.Parse 之前设置——Parse 失败时
        // Console.Error.WriteLine 输出含中文（如 "server 模式不支持 --protocol 参数"），
        // 默认系统代码页（如 GBK）会乱码。Encoding.RegisterProvider 留 Initialize（ERB 读取需要）。
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        var options = HeadlessOptions.Parse(args);
        if (options == null) return;

        var paths = GamePaths.Resolve(options.ExeDir);

        ConfigData configData;
        ITerminalSetup terminalSetup;
        try
        {
            (configData, terminalSetup) = EmueraRuntimeInitializer.Initialize(paths);
        }
        catch (GamePathValidationException ex)
        {
            // T-025 D1/D2：GamePaths.Validate 失败按模式分流——
            // - server 模式：降级 warn 继续（空闲启动，浏览器可打开选择器，真正的加载推迟到 /load-game）
            // - CLI 模式：打印提示 + 等待玩家按回车再退出（不再静默 Environment.Exit，避免双击时窗口一闪即关）
            //
            // CLI 等回车在非交互终端的边界（D17）：stdin 重定向到 /dev/null 或文件时 Console.ReadLine
            // 立即返 null/EOF → 进程退出，等同原 Environment.Exit(1)；CI/CD 不受影响。stdin 完全无句柄
            // 时 ReadLine 可能抛 InvalidOperationException——catch 兜底等同 EOF 退出。仅 stdin 是管道且
            // 管道不关闭（如 `echo | exe`）才会挂起，此场景罕见，可接受。
            Console.Error.WriteLine($"[error] {ex.Code}: {ex.Message}");
            if (options.Server)
            {
                // D1：server 模式空闲启动——降级 warn 继续，真正的游戏加载推迟到 /load-game
                Console.Error.WriteLine("[server] 游戏目录校验失败，进入空闲模式。请在浏览器中选择游戏目录。");
                // 空闲启动需要 ConfigData + ITerminalSetup——降级构造（不经 Initialize 的共享初始化路径）
                // 以确保 ServerRunner 能创建 KestrelGameServer。encoding/culture/lang 等不初始化无碍 idle 模式。
                configData = new ConfigData();
                terminalSetup = OperatingSystem.IsWindows()
                    ? (ITerminalSetup)new WindowsTerminalSetup()
                    : new PosixTerminalSetup();
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
}
