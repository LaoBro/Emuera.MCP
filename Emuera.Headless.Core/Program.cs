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

    // 注意：原 Emuera.Headless.Program 有 static Program() { GamePaths.Resolve(null); } 静态构造。
    // 拆分后不能保留——HeadlessEntry.Main 不在 Program 类内，首次访问 Program.ExeName 时才触发
    // 静态构造，此时 GamePaths.Resolve(null) 会覆盖 HeadlessEntry 已设好的 GamePaths.Current，
    // 导致路径回退到 exe 目录（CLI 崩溃 erb\ 找不到）。HeadlessEntry.Main 会显式调 GamePaths.Resolve，
    // 不需要兜底。MAUI 入口（MauiProgram.CreateMauiApp）也会显式调 Resolve（issue 07）。
}
