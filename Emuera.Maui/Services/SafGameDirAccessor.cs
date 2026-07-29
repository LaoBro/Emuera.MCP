#if ANDROID
using System.Diagnostics.CodeAnalysis;
using System.IO;
using Android.Content;
using Android.Provider;
using AndroidX.Activity.Result;

namespace MinorShift.Emuera;

/// <summary>
/// <see cref="IGameDirAccessor"/> 的 Android SAF 实现。
/// 使用 <c>ACTION_OPEN_DOCUMENT_TREE</c> 选目录，
/// <see cref="DocumentsContract"/> + <see cref="ContentResolver"/> 列举/读文件。
/// </summary>
internal sealed class SafGameDirAccessor : IGameDirAccessor
{
    private readonly Context _context;
    private readonly ActivityResultLauncher _launcher;
    private TaskCompletionSource<Android.Net.Uri?>? _tcs;
    private string? _treeUri;
    private Android.Net.Uri? _treeAndroidUri;

    /// <summary>ADR-0019：MainActivity 创建的全局实例，供 OnReloadGame 在 SAF 路径时使用。</summary>
    internal static SafGameDirAccessor? Instance { get; private set; }

    private const string PrefKey = "saf_tree_uri";

    // ── 辅助 ─────────────────────────────────────

    /// <summary>根据 URI 类型选择正确的文档 ID 提取方法——树 URI 用 GetTreeDocumentId，文档 URI 手动从路径提取。</summary>
    private static string ResolveDocId(Android.Net.Uri docUri)
    {
        var uriStr = docUri.ToString();
        var posDoc = uriStr.LastIndexOf("/document/", StringComparison.Ordinal);
        // 文档 URI（.../document/...）：手动从路径提取完整文档 ID（GetDocumentId 对 %2F 编码只返回第一段）
        if (posDoc >= 0)
        {
            var encodedDocId = uriStr[(posDoc + 10)..]; // "/document/" = 10 chars
            return Uri.UnescapeDataString(encodedDocId);
        }
        // 树 URI（.../tree/...不含 /document/）：用标准的 GetTreeDocumentId
        if (uriStr.Contains("/tree/", StringComparison.Ordinal))
            return DocumentsContract.GetTreeDocumentId(docUri);
        // 回退
        return DocumentsContract.GetDocumentId(docUri);
    }

    public SafGameDirAccessor(Context context, ActivityResultLauncher launcher)
    {
        _context = context;
        _launcher = launcher;
        _treeUri = Microsoft.Maui.Storage.Preferences.Get(PrefKey, null);
        if (_treeUri != null) _treeAndroidUri = Android.Net.Uri.Parse(_treeUri);
        Instance = this;
    }

    // ── 目录选择 ──────────────────────────────────

    public async Task<string?> PickDirectoryAsync(CancellationToken ct = default)
    {
        _tcs?.TrySetCanceled();
        _tcs = new TaskCompletionSource<Android.Net.Uri?>();
        ct.Register(() => _tcs.TrySetCanceled(ct), useSynchronizationContext: false);

        _launcher!.Launch(null);
        var uri = await _tcs.Task;
        if (uri != null)
        {
            var flags = ActivityFlags.GrantReadUriPermission;
            _context.ContentResolver!.TakePersistableUriPermission(uri, flags);
            _treeUri = uri.ToString();
            _treeAndroidUri = uri;
            Microsoft.Maui.Storage.Preferences.Set(PrefKey, _treeUri);
        }
        return uri?.ToString();
    }

    /// <summary>ActivityResultCallback 入口——由 MainActivity 调用。</summary>
    public void OnTreeResult(Android.Net.Uri? uri)
    {
        _tcs?.TrySetResult(uri);
    }

    // ── 目录/文件枚举 ─────────────────────────────

    public bool DirectoryExists(string path)
    {
        try
        {
            if (_treeAndroidUri == null)
            {
                Android.Util.Log.Warn("EmueraMaui", $"DirectoryExists: _treeAndroidUri null for {path}");
                return false;
            }
            if (!TryParseUri(path, out var docUri))
            {
                Android.Util.Log.Warn("EmueraMaui", $"DirectoryExists: TryParseUri failed for {path}");
                return false;
            }
            var docId = ResolveDocId(docUri);
            var children = DocumentsContract.BuildChildDocumentsUriUsingTree(_treeAndroidUri, docId);
            using var cursor = _context.ContentResolver!.Query(children, null, null, null, null);
            var result = cursor != null;
            Android.Util.Log.Info("EmueraMaui", $"DirectoryExists: {path} → {result} (docId={docId})");
            return result;
        }
        catch (Exception ex)
        {
            Android.Util.Log.Warn("EmueraMaui", $"DirectoryExists exception: {path} → {ex.Message}");
            return false;
        }
    }

    public string[] GetDirectories(string path)
    {
        return EnumerateChildren(path, isDir: true, pattern: null, recursive: false);
    }

    public string[] GetDirectories(string path, string searchPattern, SearchOption searchOption)
    {
        return EnumerateChildren(path, isDir: true, pattern: searchPattern, recursive: searchOption == SearchOption.AllDirectories);
    }

    public string[] GetFiles(string path)
    {
        return EnumerateChildren(path, isDir: false, pattern: null, recursive: false);
    }

    public string[] GetFiles(string path, string searchPattern, SearchOption searchOption)
    {
        return EnumerateChildren(path, isDir: false, pattern: searchPattern, recursive: searchOption == SearchOption.AllDirectories);
    }

    public string ResolveSubPath(string basePath, string subDir)
    {
        if (_treeAndroidUri == null)
        {
            Android.Util.Log.Warn("EmueraMaui", $"ResolveSubPath: _treeAndroidUri null, base={basePath}, sub={subDir}");
            return basePath;
        }
        if (!TryParseUri(basePath, out var docUri))
        {
            Android.Util.Log.Warn("EmueraMaui", $"ResolveSubPath: TryParseUri failed for {basePath}");
            return basePath;
        }
        var docId = ResolveDocId(docUri);
        var childrenUri = DocumentsContract.BuildChildDocumentsUriUsingTree(_treeAndroidUri, docId);

        using var cursor = _context.ContentResolver!.Query(
            childrenUri,
            new[] { DocumentsContract.Document.ColumnDocumentId, DocumentsContract.Document.ColumnDisplayName },
            null, null, null);
        if (cursor == null)
        {
            Android.Util.Log.Warn("EmueraMaui", $"ResolveSubPath: query returned null cursor for {childrenUri}");
            return basePath;
        }

        while (cursor.MoveToNext())
        {
            var name = cursor.GetString(1);
            if (string.Equals(name, subDir, StringComparison.OrdinalIgnoreCase))
            {
                var childDocId = cursor.GetString(0);
                var result = DocumentsContract.BuildDocumentUriUsingTree(_treeAndroidUri!, childDocId!).ToString()!;
                Android.Util.Log.Info("EmueraMaui", $"ResolveSubPath: found {subDir} → {result}");
                return result;
            }
        }
        Android.Util.Log.Warn("EmueraMaui", $"ResolveSubPath: {subDir} NOT found under {basePath} (docId={docId})");
        return basePath;
    }

    // ── 文件读取 ──────────────────────────────────

    public bool FileExists(string path)
    {
        try
        {
            if (_treeAndroidUri == null || !TryParseUri(path, out var docUri)) return false;
            using var stream = OpenRead(path);
            return stream != null;
        }
        catch { return false; }
    }

    public string ReadAllText(string path)
    {
        using var stream = OpenRead(path);
        if (stream == null) throw new FileNotFoundException($"SAF file not found: {path}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public byte[]? ReadAllBytes(string path)
    {
        using var stream = OpenRead(path);
        if (stream == null) return null;
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    public Stream? OpenRead(string path)
    {
        try
        {
            if (_treeAndroidUri == null)
            {
                Android.Util.Log.Warn("EmueraMaui", $"OpenRead: _treeAndroidUri null for {path}");
                return null;
            }
            if (!TryParseUri(path, out var docUri))
            {
                Android.Util.Log.Warn("EmueraMaui", $"OpenRead: TryParseUri failed for {path}");
                return null;
            }
            var mime = GetMimeType(docUri);
            if (mime == null || mime == DocumentsContract.Document.MimeTypeDir)
            {
                Android.Util.Log.Info("EmueraMaui", $"OpenRead: not a file: {path} (mime={mime ?? "null"})");
                return null;
            }
            var stream = _context.ContentResolver!.OpenInputStream(docUri);
            Android.Util.Log.Info("EmueraMaui", $"OpenRead OK: {path} (mime={mime})");
            return stream;
        }
        catch (Exception ex)
        {
            Android.Util.Log.Warn("EmueraMaui", $"OpenRead exception: {path} → {ex.Message}");
            return null;
        }
    }

    // ── 路径操作 ──────────────────────────────────

    public string CombinePath(string basePath, string filename)
    {
        // SAF content URI 不能用简单的字符串拼接——必须用 BuildDocumentUriUsingTree
        // 否则 content://.../dir/file.csv 会被 Android 解析为 content://.../dir（目录），
        // /file.csv 被忽略，OpenInputStream 读到目录本身 → EISDIR
        // documentId 含 %2F 时必须用 ResolveDocId，不能用 GetDocumentId（只返回第一段）。
        if (basePath.StartsWith("content://", StringComparison.Ordinal) && _treeAndroidUri != null)
        {
            try
            {
                var baseUri = Android.Net.Uri.Parse(basePath);
                if (baseUri != null)
                {
                    var docId = ResolveDocId(baseUri);
                    var childUri = DocumentsContract.BuildDocumentUriUsingTree(_treeAndroidUri!, $"{docId}/{filename}");
                    return childUri.ToString()!;
                }
            }
            catch
            {
                // 回退到字符串拼接（可能失败，但至少不抛）
            }
        }
        var trimmed = basePath.TrimEnd('/');
        return $"{trimmed}/{filename}";
    }

    public string GetFileName(string path)
    {
        // document 段内路径分隔符是 %2F：不能取 URI 最后一个 / 之后整段。
        // 与 Core SafPath 对齐，得到逻辑短名（如 xxx.ERB）。
        try
        {
            return MinorShift.Emuera.Runtime.Utils.SafPath.GetLogicalFileName(path);
        }
        catch { return path; }
    }

    // ── 内部实现 ──────────────────────────────────

    private string[] EnumerateChildren(string path, bool isDir, string? pattern, bool recursive)
    {
        var result = new List<string>();
        if (_treeAndroidUri == null || !TryParseUri(path, out var docUri)) return [];

        var docId = ResolveDocId(docUri);
        var childrenUri = DocumentsContract.BuildChildDocumentsUriUsingTree(_treeAndroidUri, docId);
        EnumerateUri(childrenUri, result, isDir, pattern, recursive);
        return result.ToArray();
    }

    private static bool TryParseUri(string path, [NotNullWhen(true)] out Android.Net.Uri? uri)
    {
        if (string.IsNullOrEmpty(path)) { uri = null; return false; }
        try { uri = Android.Net.Uri.Parse(path)!; return true; }
        catch { uri = null; return false; }
    }

    private string? GetMimeType(Android.Net.Uri uri)
    {
        try
        {
            using var cursor = _context.ContentResolver!.Query(
                uri, new[] { DocumentsContract.Document.ColumnMimeType }, null, null, null);
            return cursor?.MoveToFirst() == true ? cursor.GetString(0) : null;
        }
        catch { return null; }
    }

    private void EnumerateUri(Android.Net.Uri uri, List<string> result, bool isDir, string? pattern, bool recursive)
    {
        using var cursor = _context.ContentResolver!.Query(
            uri,
            new[]
            {
                DocumentsContract.Document.ColumnDocumentId,
                DocumentsContract.Document.ColumnDisplayName,
                DocumentsContract.Document.ColumnMimeType
            },
            null, null, null);
        if (cursor == null) return;

        while (cursor.MoveToNext())
        {
            var docId = cursor.GetString(0);
            var name = cursor.GetString(1);
            var mime = cursor.GetString(2);
            var childIsDir = mime == DocumentsContract.Document.MimeTypeDir;

            // 通配优先 match displayName；若 displayName 无扩展名，回退用 documentId 最后一段（部分 Provider 只给短名）
            var nameForMatch = name;
            if (pattern != null && !string.IsNullOrEmpty(docId) &&
                (name == null || (pattern.Contains('.') && name.IndexOf('.') < 0)))
            {
                var lastSlash = docId.LastIndexOf('/');
                nameForMatch = lastSlash >= 0 ? docId[(lastSlash + 1)..] : docId;
            }
            if (childIsDir == isDir && (pattern == null || MatchWildcard(nameForMatch, pattern)))
            {
                var childDocUri = DocumentsContract.BuildDocumentUriUsingTree(_treeAndroidUri!, docId!);
                result.Add(childDocUri.ToString()!);
            }

            if (recursive && childIsDir)
            {
                var childUri = DocumentsContract.BuildChildDocumentsUriUsingTree(_treeAndroidUri!, docId!);
                EnumerateUri(childUri, result, isDir, pattern, true);
            }
        }
    }

    /// <summary>简单通配符匹配（支持 * 和 ?）。</summary>
    private static bool MatchWildcard(string? input, string pattern)
    {
        if (input == null) return false;
        // 委托给 System.Text.RegularExpressions 转义后匹配
        var regex = new System.Text.RegularExpressions.Regex(
            "^" + System.Text.RegularExpressions.Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return regex.IsMatch(input);
    }
}
#endif
