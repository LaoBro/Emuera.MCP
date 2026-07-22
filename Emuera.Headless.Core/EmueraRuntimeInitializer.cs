using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Runtime.Config.JSON;
using MinorShift.Emuera.Runtime.Utils;
using MinorShift.Emuera.Runtime.Utils.EvilMask;
using MinorShift.Emuera.Terminal.Platform;
using System;
using System.Globalization;
using System.IO;
using System.Runtime;

namespace MinorShift.Emuera;

/// <summary>
/// 共享运行时初始化器（issue 02 抽取自 <c>HeadlessEntry.Main</c>）。
/// <para>
/// Cli（<c>HeadlessEntry.Main</c>）与 MAUI（<c>MauiProgram.CreateMauiApp</c>，issue 07 落地）共用此入口，
/// 封装所有与入口无关的运行时 bootstrap：
/// </para>
/// <list type="bullet">
/// <item>encoding（<see cref="System.Text.Encoding.RegisterProvider"/> 注册 CodePagesEncodingProvider，让 ERB 遗留编码可用；<c>Console.OutputEncoding</c> 由 Cli 入口在 Parse 前设置，MAUI 无控制台）</item>
/// <item>culture（InvariantCulture，避免 ERB 数值/日期解析受主机 locale 干扰）</item>
/// <item><see cref="ITerminalSetup"/> 平台分支 + ANSI 启用</item>
/// <item><see cref="Program.ExeName"/> 静态字段绑定</item>
/// <item>ProfileOptimization（启动性能 profile）</item>
/// <item><see cref="ConfigData"/> / <see cref="Config"/> / <see cref="JSONConfig"/> 配置三件套加载</item>
/// <item><see cref="Lang"/> 语言文件加载 + 设置</item>
/// <item><see cref="GamePaths.Validate"/>（失败抛 <see cref="GamePathValidationException"/>，由调用方做模式分流）</item>
/// </list>
/// <para>
/// 设计约束：
/// </para>
/// <list type="bullet">
/// <item>不读 <c>HeadlessOptions</c>——Cli 与 MAUI 的命令行/参数模型不同，共享层不感知</item>
/// <item>不调 runner——Cli 走 <c>ServerRunner</c>/<c>HeadlessRunner</c>，MAUI 走 <c>BridgeHost</c>，分流属入口职责</item>
/// <item><see cref="GamePaths.Validate"/> 失败抛异常而非退出进程——Cli 与 MAUI/Server 对校验失败的处置策略不同
/// （Cli 等回车退出 / Server 空闲启动 / MAUI 弹错误对话框），由调用方捕获处理</item>
/// </list>
/// </summary>
internal static class EmueraRuntimeInitializer
{
    /// <summary>
    /// 执行共享运行时初始化，返回配置好的 <see cref="ConfigData"/> 与 <see cref="ITerminalSetup"/>。
    /// </summary>
    /// <param name="paths">已 Resolve 的游戏路径对象（通常由调用方在调本方法前 <c>GamePaths.Resolve(exeDir)</c>）。</param>
    /// <returns>已加载配置的 <see cref="ConfigData"/> 与已启用 ANSI 的 <see cref="ITerminalSetup"/>。</returns>
    /// <exception cref="GamePathValidationException"><see cref="GamePaths.Validate"/> 失败——由调用方按入口模式处置。</exception>
    internal static (ConfigData ConfigData, ITerminalSetup TerminalSetup) Initialize(GamePaths paths)
    {
        // === encoding ===
        // RegisterProvider 让 Encoding.GetEncoding("shift-jis") 等遗留编码可用于 ERB 文件读取。
        // Console.OutputEncoding 由调用方（Cli 的 HeadlessEntry.Main）在 Parse 前设置——MAUI 无控制台，
        // 不需要；Cli 必须在 Parse 前设以避免错误消息中文乱码。
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

        // === culture ===
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;

        // === terminal setup ===
        var terminalSetup = CreateTerminalSetup();
        terminalSetup.TryEnableAnsi();

        // === Program.ExeName 绑定 ===
        // AssemblyData.ExeName 是 readonly（静态构造时定），Program.ExeName 去扩展名后赋值——
        // 共享源码（Runtime/UI/Game）读 Program.ExeName 用于日志、存档路径等场景。
        Program.ExeName = Path.GetFileNameWithoutExtension(AssemblyData.ExeName);

        // === ProfileOptimization（启动性能 profile）===
        // paths.ExeDir 在 Cli/Server 是 --ExeDir 或检测的 exe 目录，在 MAUI 是解压后的游戏目录
        // （FileSystem.AppDataDirectory/emuera），均可写。MAUI 移动端 profile 落地到 app data，无害。
        ProfileOptimization.SetProfileRoot(paths.ExeDir);
        ProfileOptimization.StartProfile("profile");

        // === 配置三件套加载 ===
        // 候选 2 / ADR-0009：ConfigData/Config 是 AsyncLocal scope 注入，但启动期 scope 未开，
        // 此处先 SetCurrent 让 Lang.SetLanguage（scope 外）能读到 ConfigData.Current / Config.Current。
        // 后续 GlobalStatic.OpenScope(configData) 会重新 SetCurrent（幂等覆盖）。
        ConfigData configData = new();
        configData.LoadConfig();
        ConfigData.SetCurrent(configData);
        Config.SetCurrent(configData);
        JSONConfig.Load();

        // === 语言 ===
        Lang.LoadLanguageFiles();
        Lang.SetLanguage();

        // === 路径校验（失败抛 GamePathValidationException，调用方处置）===
        paths.Validate();

        return (configData, terminalSetup);
    }

    private static ITerminalSetup CreateTerminalSetup()
    {
        if (OperatingSystem.IsWindows())
            return new WindowsTerminalSetup();

        return new PosixTerminalSetup();
    }
}
