using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MinorShift.Emuera.Runtime.Utils;

namespace MinorShift.Emuera.UI.Game.Image;

/// <summary>
/// sprite 名 → 相对游戏根文件路径 映射表。
/// <para>
/// era 游戏 HTML <c>&lt;img src='Face_1'&gt;</c> 的 src 是 sprite 名（如 <c>FACE_1</c>），真实文件是
/// <c>resources/1_Face.png</c>——映射在 <c>resources/*.csv</c> 第一、二列（<c>FACE_1,1_Face.png,0,0,180,180</c>）。
/// WinForms 的 <see cref="AppContents.LoadContents"/> 扫描 resources csv 构建 sprite 表，无头下该函数是 stub——
/// 本表只解析「名→文件路径」（不加载位图），供尺寸探针与资源通道复用。
/// </para>
/// <para>
/// 懒加载 + 按游戏根缓存：<c>TryResolve</c> 首次调用（或游戏根变化）时扫描重建。
/// 扫描经 <see cref="IGameDirAccessor.GetFiles"/> 枚举——SAF（安卓）下 GetFiles 已实现（ContentResolver 查询），
/// 但本类的相对路径计算用 <see cref="Path"/> API（Windows 文件路径语义），对 content:// URI 无效——
/// 安卓 sprite 名解析待 URI 形态适配（记入 spec，当前 Windows 场景不受影响）。
/// </para>
/// </summary>
internal static class ImageNameTable
{
    private static readonly object _sync = new();
    private static string? _loadedRoot;
    private static Dictionary<string, string> _map = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>解析 sprite 名 → 相对游戏根的文件路径（正斜杠分隔）。失败返回 false。</summary>
    public static bool TryResolve(IGameDirAccessor accessor, string gameRoot, string name, out string? relativePath)
    {
        relativePath = null;
        if (accessor == null || string.IsNullOrEmpty(gameRoot) || string.IsNullOrEmpty(name))
            return false;
        EnsureLoaded(accessor, gameRoot);
        lock (_sync)
        {
            return _map.TryGetValue(name, out relativePath) && relativePath != null;
        }
    }

    private static void EnsureLoaded(IGameDirAccessor accessor, string gameRoot)
    {
        lock (_sync)
        {
            if (_loadedRoot == gameRoot)
                return; // 已为当前游戏根加载（空表也缓存，避免每请求重扫）
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                // ContentDir = <root>/resources（与 GamePaths.ContentDir 同规则）
                var contentDir = accessor.ResolveSubPath(gameRoot, "resources");
                string[] csvFiles = accessor.GetFiles(contentDir, "*.csv", SearchOption.AllDirectories);
                foreach (var csvPath in csvFiles)
                {
                    var bytes = accessor.ReadAllBytes(csvPath);
                    if (bytes == null || bytes.Length == 0)
                        continue;
                    var text = EncodingHandler.DetectEncoding(bytes).GetString(bytes);
                    // BOM（U+FEFF）在 GetString 中保留、Trim 不保证剥离——显式剥除，否则首行 name 前缀 BOM 永 miss
                    text = text.TrimStart('\uFEFF');
                    var csvDirRel = GetRelativeDir(gameRoot, csvPath);
                    foreach (var line in text.Split('\n'))
                    {
                        var trimmed = line.Trim();
                        if (trimmed.Length == 0 || trimmed.StartsWith(';'))
                            continue;
                        var tokens = trimmed.Split(',');
                        if (tokens.Length < 2)
                            continue;
                        var name = tokens[0].Trim();
                        var file = tokens[1].Trim();
                        // 无头下动画 sprite（ANIME 行）无单一文件可解析——按 spec 决定跳过；
                        // 第二列必须含扩展名（WinForms 同款校验）
                        if (name.Length == 0 || file.Length == 0 || file.IndexOf('.') < 0)
                            continue;
                        if (file.Equals("ANIME", StringComparison.OrdinalIgnoreCase))
                            continue;
                        var rel = csvDirRel.Length == 0 ? file : csvDirRel + "/" + file;
                        if (!map.ContainsKey(name))
                            map[name] = rel; // 重复名 first-wins（WinForms 打 SpriteNameAlreadyUsed 告警，无头静默）
                    }
                }
            }
            catch
            {
                // 扫描失败（目录不存在/IO）→ 空表，查询自然失败
            }
            _loadedRoot = gameRoot;
            _map = map;
        }
    }

    /// <summary>
    /// csv 文件所在目录相对游戏根（正斜杠）。
    /// 注意：仅适用于文件系统路径；content:// URI 需 URI 形态适配（见类 remarks）。
    /// </summary>
    private static string GetRelativeDir(string gameRoot, string csvPath)
    {
        var dir = Path.GetDirectoryName(csvPath) ?? "";
        var rel = Path.GetRelativePath(gameRoot, dir);
        return rel == "." ? "" : rel.Replace('\\', '/');
    }
}
