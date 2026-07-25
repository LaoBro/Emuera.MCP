using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MinorShift.Emuera;

/// <summary>
/// 列举目录下直接子目录的纯 C# 模块——game-library spec ID2。
/// <para>
/// 用于 Android 目录浏览器弹窗（Vue 端 <c>DirectoryBrowser.vue</c>）——
/// 用户在 Android 上更改主目录时无法用原生 FolderPicker（MAUI 限制），
/// 改用此模块逐层列举子目录让用户点选。
/// </para>
/// <para>
/// 与 <see cref="GameScanner"/> 同级，纯 C# 无平台依赖——MAUI 双平台均可复用。
/// 只列目录不列文件，不递归。
/// </para>
/// </summary>
public static class DirectoryLister
{
    /// <summary>
    /// 列举 <paramref name="dirPath"/> 下的直接子目录名（不递归）。
    /// <para>
    /// <paramref name="dirPath"/> 不存在 / 无权访问 → 返回 <see cref="DirectoryListResult"/>，
    /// 其中 <see cref="DirectoryListResult.SubDirectories"/> 为空列表，
    /// <see cref="DirectoryListResult.ParentPath"/> 仍按规则计算（若可）。
    /// </para>
    /// <para>
    /// <b>父目录计算</b>：<c>Directory.GetParent(dirPath)?.FullName</c>——
    /// 若父级为 <c>null</c>（已到根）或等于 <paramref name="dirPath"/> 自身（根保护）则置 <c>null</c>。
    /// </para>
    /// </summary>
    /// <param name="dirPath">要列举的目录绝对路径。null / 空字符串 → 返空结果。</param>
    /// <returns>包含当前路径、父路径、子目录名排序列表的 <see cref="DirectoryListResult"/>。</returns>
    public static DirectoryListResult ListDirectories(string? dirPath)
    {
        if (string.IsNullOrEmpty(dirPath))
            return new DirectoryListResult(string.Empty, null, Array.Empty<string>());

        // 规范化路径——去掉尾部分隔符避免 GetParent 返自身
        string normalized;
        try
        {
            normalized = Path.GetFullPath(dirPath);
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or System.Security.SecurityException)
        {
            return new DirectoryListResult(dirPath, null, Array.Empty<string>());
        }

        var parent = ComputeParentPath(normalized);

        if (!Directory.Exists(normalized))
            return new DirectoryListResult(normalized, parent, Array.Empty<string>());

        string[] subDirs;
        try
        {
            subDirs = Directory.GetDirectories(normalized);
        }
        catch (UnauthorizedAccessException)
        {
            return new DirectoryListResult(normalized, parent, Array.Empty<string>());
        }

        var names = subDirs
            .Select(Path.GetFileName)
            .Where(n => !string.IsNullOrEmpty(n))
            .Select(n => n!)
            .Order(StringComparer.Ordinal)
            .ToList();
        return new DirectoryListResult(normalized, parent, names);
    }

    /// <summary>
    /// 计算父目录路径——根保护：父级为 null 或等于自身则返 null。
    /// </summary>
    private static string? ComputeParentPath(string normalizedPath)
    {
        try
        {
            var parentInfo = Directory.GetParent(normalizedPath);
            if (parentInfo is null)
                return null;
            var parentFullName = parentInfo.FullName;
            // 根保护：Path.GetFullPath 后父级等于自身（如 "/" 或 "C:\"）则返 null
            if (string.Equals(parentFullName, normalizedPath, StringComparison.Ordinal))
                return null;
            return parentFullName;
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>
/// <see cref="DirectoryLister.ListDirectories"/> 返回结果——game-library spec ID2。
/// </summary>
/// <param name="CurrentPath">当前目录规范化绝对路径（用作 Vue 弹窗「当前: ...」展示）。</param>
/// <param name="ParentPath">
/// 父目录路径——<c>null</c> 表示已在根不可再上。Vue 弹窗据此决定是否显示「返回上级」按钮。
/// </param>
/// <param name="SubDirectories">当前目录下直接子目录名排序列表（仅目录名，非完整路径）。</param>
public sealed record DirectoryListResult(
    string CurrentPath,
    string? ParentPath,
    IReadOnlyList<string> SubDirectories);
