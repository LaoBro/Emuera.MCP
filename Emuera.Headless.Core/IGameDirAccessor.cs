using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace MinorShift.Emuera;

/// <summary>
/// 游戏目录访问抽象——隐藏平台文件系统差异（SAF content:// URI vs 传统文件路径）。
/// 所有文件 IO 必须通过此接口，禁止直接调用 File.* / Directory.*。
/// </summary>
public interface IGameDirAccessor
{
    // ── 目录选择 ──────────────────────────────────
    Task<string?> PickDirectoryAsync(CancellationToken ct = default);

    // ── 目录枚举 ──────────────────────────────────
    bool DirectoryExists(string path);
    string[] GetDirectories(string path);
    string[] GetDirectories(string path, string searchPattern, SearchOption searchOption);

    // ── 文件枚举 ──────────────────────────────────
    bool FileExists(string path);
    string[] GetFiles(string path);
    string[] GetFiles(string path, string searchPattern, SearchOption searchOption);

    // ── 文件读取 ──────────────────────────────────
    string ReadAllText(string path);
    byte[]? ReadAllBytes(string path);
    Stream? OpenRead(string path);

    // ── 路径操作 ──────────────────────────────────
    string ResolveSubPath(string basePath, string subDir);
    string CombinePath(string basePath, string filename);
    string GetFileName(string path);
}
