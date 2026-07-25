using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Maui.Storage;

namespace Emuera.Maui;

/// <summary>
/// 内置游戏资源解压器（issue 07 / spec ID11）。
/// <para>
/// Phase 1 硬编码嵌入 <c>test_game/</c> 内容（<c>csv/</c> + <c>erb/</c>），
/// 打包为 <c>MauiAsset</c>（<c>Link=game\...</c>）。首启动检查
/// <c>FileSystem.AppDataDirectory/emuera/.installed</c> marker 文件——不存在则解压，
/// 存在则跳过（避免重复解压等待）。
/// </para>
/// <para>
/// <c>FileSystem.AppDataDirectory</c> 是 MAUI 跨平台抽象（Windows <c>%LOCALAPPDATA%/&lt;pkg&gt;</c>，
/// Android <c>/data/data/&lt;pkg&gt;/files</c>），可读写，<c>ConfigData.LoadConfig</c> / 存档写入等
/// 现有 Headless 代码零改动。
/// </para>
/// <remarks>
/// Phase 1 限制：文件列表硬编码（<c>test_game/</c> 当前仅 <c>csv/GameBase.csv</c> + <c>erb/TEST.ERB</c>）。
/// MAUI <c>FileSystem.OpenAppPackageFileAsync</c> 只能开单个文件，无跨平台枚举 API。
/// Phase 2 加 <c>FilePicker</c> 时用户选的目录直接传 <c>GamePaths.Resolve(userSelectedDir)</c>，
/// 与 <see cref="EnsureGameDirAsync"/> 路径并行；届时若仍需解压内置资源，可改用平台原生枚举
/// （Windows <c>Directory.GetFiles</c> + Android <c>Assets.List</c>）。
/// </remarks>
internal static class GameResourceExtractor
{
    /// <summary>marker 文件名——存在则视为已解压完成。内容为解压完成时间戳（UTC ISO 8601）。</summary>
    private const string MarkerFileName = ".installed";

    /// <summary>MauiAsset 打包路径前缀（csproj <c>Link="game\%(RecursiveDir)..."</c>）。</summary>
    private const string PackagePrefix = "game/";

    /// <summary>
    /// 确保游戏目录已解压到 <c>FileSystem.AppDataDirectory/emuera/</c>。
    /// <para>
    /// 幂等：marker 文件存在时直接返回目录路径，不重复解压。
    /// </para>
    /// </summary>
    /// <returns>解压后的游戏目录绝对路径（<c>&lt;AppDataDirectory&gt;/emuera/</c>，以分隔符结尾）。</returns>
    public static async Task<string> EnsureGameDirAsync()
    {
#if ANDROID
        Android.Util.Log.Info("EmueraMaui", $"EnsureGameDirAsync starting, AppDataDirectory={FileSystem.AppDataDirectory}");
#endif
        var appData = FileSystem.AppDataDirectory;
        var gameDir = Path.Combine(appData, "emuera") + Path.DirectorySeparatorChar;
        var markerFile = Path.Combine(gameDir, MarkerFileName);

        if (File.Exists(markerFile))
        {
#if ANDROID
            Android.Util.Log.Info("EmueraMaui", $"EnsureGameDirAsync: marker exists, returning existing gameDir={gameDir}");
#endif
            return gameDir;
        }

#if ANDROID
        Android.Util.Log.Info("EmueraMaui", $"EnsureGameDirAsync: no marker, extracting to {gameDir}");
#endif

        // Phase 1 硬编码 test_game/ 文件列表——见类 remarks。
        // 路径相对 MauiAsset 打包根（PackagePrefix="game/"）。
        await ExtractFileAsync(PackagePrefix + "csv/GameBase.csv", Path.Combine(gameDir, "csv", "GameBase.csv"));
        await ExtractFileAsync(PackagePrefix + "erb/TEST.ERB", Path.Combine(gameDir, "erb", "TEST.ERB"));

        // emuera.config 在 test_game/ 中不存在——尝试解压，失败静默跳过（ConfigData 用默认值）。
        await TryExtractFileAsync(PackagePrefix + "emuera.config", Path.Combine(gameDir, "emuera.config"));

        // 写 marker——记录解压完成时间，便于诊断。
        await File.WriteAllTextAsync(markerFile, DateTime.UtcNow.ToString("O"));
#if ANDROID
        Android.Util.Log.Info("EmueraMaui", $"EnsureGameDirAsync completed: gameDir={gameDir}");
#endif
        return gameDir;
    }

    /// <summary>
    /// 从 MauiAsset 解压单个文件到目标路径。目标目录不存在则创建。
    /// </summary>
    /// <exception cref="FileNotFoundException">MauiAsset 中找不到该文件。</exception>
    private static async Task ExtractFileAsync(string packagePath, string destPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
        using var stream = await FileSystem.OpenAppPackageFileAsync(packagePath);
        using var file = File.Create(destPath);
        await stream.CopyToAsync(file);
    }

    /// <summary>
    /// 尝试解压单个文件——文件不存在时静默跳过（如 <c>emuera.config</c> 在 <c>test_game/</c> 中缺失）。
    /// </summary>
    private static async Task TryExtractFileAsync(string packagePath, string destPath)
    {
        try
        {
            await ExtractFileAsync(packagePath, destPath);
        }
        catch (FileNotFoundException)
        {
            // 静默跳过——可选文件缺失不阻断解压流程
        }
        catch (DirectoryNotFoundException)
        {
            // Android Assets.Open 某些情况下对不存在路径抛 DirectoryNotFoundException
        }
    }
}
