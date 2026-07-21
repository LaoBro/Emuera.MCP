using MinorShift.Emuera.Runtime.Utils;
using System.Collections.Generic;
using System.IO;

namespace MinorShift.Emuera;

/// <summary>
/// 共享运行时上下文（路径转发 + 状态字段）。拆分自原 Emuera.Headless.Program（issue 01）。
/// 50+ 处 Shared/ 源码调用 <c>Program.xxx</c> 转发到 <see cref="GamePaths.Current"/>——为保持零改动，
/// 此类必须留在 Core 且名字不变（Phase 2 重命名为 <c>EmueraContext</c>）。
/// <para>
/// <c>Main</c> 已移到 <c>Emuera.Headless.Cli/HeadlessEntry.cs</c>（Cli 项目为 Exe 入口）。
/// <c>LoadFonts</c> 暂留 Core——由 GameLoopComposer.RunAsync 调用（issue 02 将删除）。
/// </para>
/// </summary>
static class Program
{
    // === 路径属性转发（保持共享文件零改动）===
    public static string ExeDir => GamePaths.Current.ExeDir;
    public static string CsvDir => GamePaths.Current.CsvDir;
    public static string ErbDir => GamePaths.Current.ErbDir;
    public static string DebugDir => GamePaths.Current.DebugDir;
    public static string DatDir => GamePaths.Current.DatDir;
    public static string ContentDir => GamePaths.Current.ContentDir;
    public static string SoundDir => GamePaths.Current.SoundDir;
    public static string FontDir => GamePaths.Current.FontDir;
    /// <summary>
    /// ExeName 的 setter 改 internal（原 private）——拆分后由 Cli 的 <c>HeadlessEntry.Main</c> 设置。
    /// </summary>
    public static string ExeName { get; internal set; } = "";

    // === 运行时状态 ===
    public static bool rebootFlag;
    public static bool AnalysisMode;
    public static List<string> AnalysisFiles = new();
    public static bool DebugMode { get; private set; }

    /// <summary>
    /// 加载字体文件到当前 scope 的 GlobalStatic.Pfc。
    /// 必须在 OpenScope() 之后调用（ADR-0008：Pfc 是实例成员，随 scope 生灭）。
    /// <para>
    /// issue 02 将删除此方法——<see cref="HeadlessFontCollection.AddFontFile"/> 是空实现 no-op，
    /// 删除后 GameLoopComposer.RunAsync 内的调用一并移除，零行为变化。
    /// </para>
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

    // 注意：静态构造函数必须保留，但改为惰性兜底——
    //
    // 原实现 `static Program() { GamePaths.Resolve(null); }` 无条件覆盖 Current，在拆分后
    // 导致 CLI bug：HeadlessEntry.Main L43 调 GamePaths.Resolve(options.ExeDir) 设置 Current 后，
    // L44 访问 Program.ExeName 触发静态构造，Resolve(null) 把 Current 覆盖回 exe 目录。
    //
    // 改为「Current is null 才兜底」后：
    // - CLI/Server：HeadlessEntry.Main 先 Resolve(exeDir) → Current 非 null → 静态构造跳过
    // - 单元测试：不调 HeadlessEntry.Main，直接 new ConfigData() → Program.ExeDir → 静态构造
    //   → Current is null → Resolve(null) 兜底初始化（指向测试 bin 目录）
    // - MAUI：MauiProgram.CreateMauiApp 先 Resolve(gameDir) → 同 CLI
    static Program()
    {
        if (GamePaths.Current is null)
            GamePaths.Resolve(null);
    }
}
