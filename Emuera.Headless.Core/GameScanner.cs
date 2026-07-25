using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MinorShift.Emuera;

/// <summary>
/// 扫描主目录下游戏列表的纯 C# 模块——game-library spec ID1。
/// <para>
/// 与 <see cref="GamePaths"/> 同级，无平台依赖——MAUI 双平台（Windows + Android）和未来 HTTP server
/// 均可复用。<see cref="Scan"/> 仅检查直接子目录是否含 csv/ + erb/ 子目录（与
/// <see cref="GamePaths.Validate"/> 一致），不读文件内容、不调 <see cref="GamePaths.Validate"/>
/// （不吞抛异常）、不递归。
/// </para>
/// <para>
/// <b>rootDir 不存在时静默返空列表</b>——调用方（BridgeHost / Vue 端）根据空列表情况决定空状态 UI
/// （主目录不存在 vs. 主目录存在但无游戏），故不抛异常区分两类错误。
/// </para>
/// </summary>
public static class GameScanner
{
    /// <summary>
    /// 扫描 <paramref name="rootDir"/> 下的直接子目录，返回含 csv/ + erb/ 子目录的 <see cref="GameEntry"/> 列表。
    /// <para>
    /// 步骤：
    /// <list type="number">
    ///   <item><see cref="Directory.Exists(string)"/> 检查 rootDir——不存在返空列表</item>
    ///   <item><see cref="Directory.GetDirectories(string)"/> 列直接子目录</item>
    ///   <item>每个子目录检查 csv/ + erb/ 子目录是否同时存在——任一缺失跳过</item>
    ///   <item>构造 <see cref="GameEntry"/> 列表，按 <see cref="GameEntry.Name"/> 排序返回</item>
    /// </list>
    /// </para>
    /// <para>
    /// 不读文件内容（仅 <see cref="Directory.Exists"/> 区分文件 vs 目录），
    /// 不递归（只扫一层），不抛异常（rootDir 不存在 / 无权限访问静默返空）。
    /// </para>
    /// </summary>
    /// <param name="rootDir">主目录绝对路径。null / 空字符串 / 不存在 → 返空列表。</param>
    /// <returns>合法游戏列表，按 <see cref="GameEntry.Name"/> 升序排序。</returns>
    public static IReadOnlyList<GameEntry> Scan(string? rootDir)
    {
        if (string.IsNullOrEmpty(rootDir))
            return Array.Empty<GameEntry>();
        if (!Directory.Exists(rootDir))
            return Array.Empty<GameEntry>();

        string[] subDirs;
        try
        {
            subDirs = Directory.GetDirectories(rootDir);
        }
        catch (UnauthorizedAccessException)
        {
            // 无权限访问主目录——视为空列表（与不存在同处理，调用方按空 UI 决策）
            return Array.Empty<GameEntry>();
        }

        var entries = new List<GameEntry>();
        foreach (var subDir in subDirs)
        {
            var csvDir = Path.Combine(subDir, "csv");
            var erbDir = Path.Combine(subDir, "erb");
            // Directory.Exists 天然区分文件 vs 目录——若 csv 或 erb 是文件，返 false 跳过
            if (!Directory.Exists(csvDir) || !Directory.Exists(erbDir))
                continue;
            var name = Path.GetFileName(subDir);
            entries.Add(new GameEntry(name, subDir));
        }

        return entries.OrderBy(e => e.Name, StringComparer.Ordinal).ToList();
    }
}

/// <summary>
/// 扫描结果中的单条游戏条目——game-library spec ID1。
/// </summary>
/// <param name="Name">游戏目录名（Path.GetFileName(FullPath)），UI 列表展示用。</param>
/// <param name="FullPath">游戏目录绝对路径，加载时投递给 <see cref="GamePaths.Resolve"/>。</param>
public sealed record GameEntry(string Name, string FullPath);
