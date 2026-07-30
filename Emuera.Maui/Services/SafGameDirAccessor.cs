#if ANDROID
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Text;
using Android.Content;
using Android.Provider;
using AndroidX.Activity.Result;

namespace MinorShift.Emuera;

/// <summary>
/// <see cref="IGameDirAccessor"/> 的 Android SAF 实现。
/// 使用 <c>ACTION_OPEN_DOCUMENT_TREE</c> 选目录，
/// <see cref="DocumentsContract"/> + <see cref="ContentResolver"/> 列举/读/写文件。
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
    private const string DeferredWriteLimitPrefKey = "saf_deferred_write_limit_bytes";
    private const string WriteProbeFilePrefix = "_emuera_write_probe_";
    private const string OctetStreamMime = "application/octet-stream";
    private const long DefaultDeferredWriteLimitBytes = 64L * 1024 * 1024;

    /// <summary>
    /// Deferred SAF writes are bounded before allocating unbounded memory.
    /// Advanced deployments may override this with the app preference key above;
    /// non-positive values fall back to the safe default.
    /// </summary>
    internal static long DeferredWriteLimitBytes
    {
        get
        {
            var configured = Microsoft.Maui.Storage.Preferences.Get(
                DeferredWriteLimitPrefKey, DefaultDeferredWriteLimitBytes);
            return configured > 0 ? configured : DefaultDeferredWriteLimitBytes;
        }
    }

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
            // 方案 B：存档写回游戏树，优先持久化读+写权限。
            // 某些 DocumentsProvider 只返回读授权；保留 URI 并继续走探针，
            // 让上层给出“请重新选择目录”的可诊断提示，而不是在这里吞掉选择结果。
            var flags = ActivityFlags.GrantReadUriPermission | ActivityFlags.GrantWriteUriPermission;
            try
            {
                _context.ContentResolver!.TakePersistableUriPermission(uri, flags);
            }
            catch (Exception ex)
            {
                Android.Util.Log.Warn("EmueraMaui",
                    $"PickDirectory: persist read/write permission failed: {ex.Message}");
                try
                {
                    _context.ContentResolver!.TakePersistableUriPermission(
                        uri, ActivityFlags.GrantReadUriPermission);
                }
                catch (Exception readEx)
                {
                    Android.Util.Log.Warn("EmueraMaui",
                        $"PickDirectory: persist read-only permission failed: {readEx.Message}");
                }
            }
            _treeUri = uri.ToString();
            _treeAndroidUri = uri;
            Microsoft.Maui.Storage.Preferences.Set(PrefKey, _treeUri);
            Android.Util.Log.Info("EmueraMaui",
                $"PickDirectory: took r/w persistable uri, hasWrite={HasWriteAccess()}, uri={_treeUri}");
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
            // 不调用 EnsureRealDirectoryUri（会 Create）；只做存在性查询
            if (IsTreeRootUri(docUri)) return true;
            if (TryQueryDocument(docUri, out var mime) && mime == DocumentsContract.Document.MimeTypeDir)
                return true;
            if (TryGetParentAndName(path, out var parentUri, out var name) && parentUri != null && name != null)
            {
                Android.Net.Uri? realParent = null;
                if (IsTreeRootUri(parentUri))
                {
                    realParent = DocumentsContract.BuildDocumentUriUsingTree(
                        _treeAndroidUri, DocumentsContract.GetTreeDocumentId(_treeAndroidUri));
                }
                else if (TryQueryDocument(parentUri, out var pm) && pm == DocumentsContract.Document.MimeTypeDir)
                {
                    realParent = parentUri;
                }
                else
                {
                    // 父亦为理论路径时：仅支持「树根下一级」查找（不创建）
                    realParent = null;
                    if (TryGetParentAndName(parentUri.ToString()!, out var grand, out var parentName)
                        && grand != null && parentName != null && IsTreeRootUri(grand))
                    {
                        var grandReal = DocumentsContract.BuildDocumentUriUsingTree(
                            _treeAndroidUri, DocumentsContract.GetTreeDocumentId(_treeAndroidUri));
                        if (grandReal != null)
                            realParent = FindChildDocument(grandReal, parentName, wantDir: true);
                    }
                }
                if (realParent != null && FindChildDocument(realParent, name, wantDir: true) != null)
                    return true;
            }
            return false;
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
        if (cursor != null)
        {
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
        }

        // 子目录尚不存在时返回理论 document URI，供 CreateDirectory / OpenWrite 按父+名创建
        // （旧实现返回 basePath 会导致 sav/ 永远建在错误层级）
        var theoretical = DocumentsContract.BuildDocumentUriUsingTree(_treeAndroidUri, $"{docId}/{subDir}")!.ToString()!;
        Android.Util.Log.Info("EmueraMaui", $"ResolveSubPath: {subDir} not found, theoretical → {theoretical}");
        return theoretical;
    }

    // ── 文件读取 ──────────────────────────────────

    public bool FileExists(string path)
    {
        try
        {
            return ResolveExistingFileUri(path) != null;
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

            var docUri = ResolveExistingFileUri(path);
            if (docUri == null)
            {
                Android.Util.Log.Info("EmueraMaui", $"OpenRead: file not found: {path}");
                return null;
            }

            var mime = GetMimeType(docUri);
            if (mime == null || mime == DocumentsContract.Document.MimeTypeDir)
            {
                Android.Util.Log.Info("EmueraMaui", $"OpenRead: not a file: {path} (mime={mime ?? "null"})");
                return null;
            }

            // 与写路径对称：一次 CopyTo 进 MemoryStream。
            // 原因：
            // 1) SAF InputStream 常不可 Seek / Length 不可靠 → EraBinaryDataReader.CreateReader
            //    的 `fs.Length < 16` 会误判为坏档或走文本路径；
            // 2) BinaryReader 对数十万字节存档逐字段读 ContentResolver 极慢（与写假死同因）。
            var sw = System.Diagnostics.Stopwatch.StartNew();
            using var input = _context.ContentResolver!.OpenInputStream(docUri);
            if (input == null)
            {
                Android.Util.Log.Warn("EmueraMaui", $"OpenRead: OpenInputStream null for {path}");
                return null;
            }
            var ms = new MemoryStream();
            input.CopyTo(ms);
            ms.Position = 0;
            Android.Util.Log.Info("EmueraMaui",
                $"OpenRead buffered OK path={path} bytes={ms.Length} ms={sw.ElapsedMilliseconds} mime={mime}");
            return ms;
        }
        catch (Exception ex)
        {
            Android.Util.Log.Warn("EmueraMaui", $"OpenRead exception: {path} → {ex.Message}");
            return null;
        }
    }

    // ── 文件/目录写入 ─────────────────────────────

    public bool HasWriteAccess()
    {
        if (_treeAndroidUri == null) return false;
        try
        {
            var treeStr = _treeAndroidUri.ToString();
            foreach (var perm in _context.ContentResolver!.PersistedUriPermissions)
            {
                if (perm?.Uri == null || !perm.IsWritePermission) continue;
                var permStr = perm.Uri.ToString();
                // 持久化的是 tree URI；比较时兼容 document 形态
                if (string.Equals(permStr, treeStr, StringComparison.Ordinal)
                    || (treeStr != null && permStr != null && treeStr.StartsWith(permStr, StringComparison.Ordinal))
                    || (treeStr != null && permStr != null && permStr.StartsWith(treeStr, StringComparison.Ordinal)))
                {
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            Android.Util.Log.Warn("EmueraMaui", $"HasWriteAccess exception: {ex.Message}");
        }
        return false;
    }

    public void CreateDirectory(string path)
    {
        if (_treeAndroidUri == null)
            throw new InvalidOperationException("SAF tree URI not set");
        if (!TryParseUri(path, out var dirUri))
            throw new IOException($"CreateDirectory: invalid path {path}");

        var real = EnsureRealDirectoryUri(dirUri)
            ?? throw new IOException($"CreateDirectory failed: {path}");
        Android.Util.Log.Info("EmueraMaui", $"CreateDirectory OK: {path} → {real}");
    }

    public Stream OpenWrite(string path)
    {
        if (_treeAndroidUri == null)
            throw new InvalidOperationException("SAF tree URI not set");
        if (!HasWriteAccess())
        {
            Android.Util.Log.Error("EmueraMaui",
                "OpenWrite: no persisted write permission — re-pick game directory to grant write access");
            throw new UnauthorizedAccessException(
                "SAF write permission not granted. Please re-select the game directory to allow saving.");
        }

        var displayName = GetFileName(path);
        if (string.IsNullOrEmpty(displayName))
            throw new IOException($"OpenWrite: empty display name for {path}");

        // 先解析/创建 document，但**不**立刻挂 OpenOutputStream。
        // TK 存档可达数十 MB；经 ContentResolver 逐块写极慢甚至表现为卡死。
        // 改为有上限的可 Seek 内存缓冲，Dispose 时一次 CopyTo 写出。
        // 旧文档只有在输出流成功打开后才会被 provider 截断；打开失败时旧内容保持不变。
        var existing = ResolveExistingFileUri(path);
        Android.Net.Uri docUri;
        var createdDocument = false;
        if (existing != null)
        {
            docUri = existing;
            Android.Util.Log.Info("EmueraMaui", $"OpenWrite: defer buffer → existing {path}");
        }
        else
        {
            if (!TryGetParentAndName(path, out var parentUri, out var name))
                throw new IOException($"OpenWrite: cannot resolve parent for {path}");

            parentUri = EnsureRealDirectoryUri(parentUri)
                ?? throw new IOException($"OpenWrite: parent directory missing or unwritable for {path}");

            var created = DocumentsContract.CreateDocument(
                _context.ContentResolver!,
                parentUri,
                GuessMime(displayName),
                name);
            if (created == null)
                throw new IOException($"OpenWrite: CreateDocument returned null for {path}");
            docUri = created;
            createdDocument = true;
            Android.Util.Log.Info("EmueraMaui", $"OpenWrite: defer buffer → created {path} → {created}");
        }

        return new DeferredSafWriteStream(_context, docUri, path, createdDocument);
    }

    public void Delete(string path)
    {
        try
        {
            var docUri = ResolveExistingFileUri(path);
            if (docUri == null)
            {
                Android.Util.Log.Info("EmueraMaui", $"Delete: not found {path}");
                return;
            }
            var ok = DocumentsContract.DeleteDocument(_context.ContentResolver!, docUri);
            Android.Util.Log.Info("EmueraMaui", $"Delete: {path} → {ok}");
        }
        catch (Exception ex)
        {
            Android.Util.Log.Warn("EmueraMaui", $"Delete exception: {path} → {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// B1 真机闸门：在树根写/读/删探针文件。成功返回 true 并写 log。
    /// </summary>
    internal bool TryWriteProbe(out string detail)
    {
        detail = "";
        try
        {
            if (_treeAndroidUri == null)
            {
                detail = "tree URI null";
                return false;
            }
            if (!HasWriteAccess())
            {
                detail = "no persisted write permission — re-pick directory";
                Android.Util.Log.Warn("EmueraMaui", $"WriteProbe FAIL: {detail}");
                return false;
            }

            var rootPath = TreeRootDocumentPath();
            var probeName = $"{WriteProbeFilePrefix}{Guid.NewGuid():N}.tmp";
            var probePath = CombinePath(rootPath, probeName);
            var payload = Encoding.UTF8.GetBytes("emuera-write-probe-ok");

            try
            {
                using (var ws = OpenWrite(probePath))
                    ws.Write(payload, 0, payload.Length);

                using (var rs = OpenRead(probePath)
                    ?? throw new IOException("probe OpenRead returned null"))
                {
                    using var ms = new MemoryStream();
                    rs.CopyTo(ms);
                    var read = ms.ToArray();
                    if (read.Length != payload.Length || !read.AsSpan().SequenceEqual(payload))
                    {
                        detail = $"readback mismatch len={read.Length}";
                        Android.Util.Log.Error("EmueraMaui", $"WriteProbe FAIL: {detail}");
                        return false;
                    }
                }

                detail = $"ok path={probePath}";
                Android.Util.Log.Info("EmueraMaui", $"WriteProbe OK: {detail}");
                return true;
            }
            finally
            {
                try
                {
                    Delete(probePath);
                }
                catch (Exception cleanupEx)
                {
                    Android.Util.Log.Warn("EmueraMaui",
                        $"WriteProbe cleanup failed path={probePath} ex={cleanupEx.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            detail = ex.ToString();
            Android.Util.Log.Error("EmueraMaui", $"WriteProbe FAIL: {detail}");
            return false;
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

    private string TreeRootDocumentPath()
    {
        if (_treeAndroidUri == null) return _treeUri ?? "";
        // 树 URI 本身可作为 base；CombinePath/ResolveDocId 能处理
        var docId = DocumentsContract.GetTreeDocumentId(_treeAndroidUri);
        var docUri = DocumentsContract.BuildDocumentUriUsingTree(_treeAndroidUri, docId);
        return docUri?.ToString() ?? _treeUri ?? "";
    }

    private bool IsTreeRootUri(Android.Net.Uri uri)
    {
        if (_treeAndroidUri == null) return false;
        try
        {
            var treeId = DocumentsContract.GetTreeDocumentId(_treeAndroidUri);
            var docId = ResolveDocId(uri);
            return string.Equals(treeId, docId, StringComparison.Ordinal);
        }
        catch { return false; }
    }

    private bool DirectoryDocumentExists(string path)
    {
        try
        {
            if (_treeAndroidUri == null || !TryParseUri(path, out var docUri)) return false;
            return EnsureRealDirectoryUri(docUri) != null;
        }
        catch { return false; }
    }

    /// <summary>
    /// 将可能为「理论」的目录 URI 解析为 provider 中真实存在的目录 URI；
    /// 若不存在则在真实父下 CreateDocument(DIR)。
    /// </summary>
    private Android.Net.Uri? EnsureRealDirectoryUri(Android.Net.Uri dirUri)
    {
        if (_treeAndroidUri == null) return null;
        if (IsTreeRootUri(dirUri))
            return DocumentsContract.BuildDocumentUriUsingTree(
                _treeAndroidUri, DocumentsContract.GetTreeDocumentId(_treeAndroidUri));

        if (TryQueryDocument(dirUri, out var mime) && mime == DocumentsContract.Document.MimeTypeDir)
            return dirUri;

        var path = dirUri.ToString()!;
        if (!TryGetParentAndName(path, out var parentUri, out var name) || name == null || parentUri == null)
            return null;

        // 父目录：树根 / 已存在 / 再对父做一级 Ensure（仅支持「根下一级」，如 sav/dat）
        Android.Net.Uri? effectiveParent;
        if (IsTreeRootUri(parentUri))
        {
            effectiveParent = DocumentsContract.BuildDocumentUriUsingTree(
                _treeAndroidUri, DocumentsContract.GetTreeDocumentId(_treeAndroidUri));
        }
        else if (TryQueryDocument(parentUri, out var parentMime)
                 && parentMime == DocumentsContract.Document.MimeTypeDir)
        {
            effectiveParent = parentUri;
        }
        else
        {
            // 父仍是理论路径：若父的父是树根，则在树根下创建/查找父名
            if (!TryGetParentAndName(parentUri.ToString()!, out var grand, out var parentName)
                || grand == null || parentName == null)
                return null;
            if (!IsTreeRootUri(grand)
                && !(TryQueryDocument(grand, out var gm) && gm == DocumentsContract.Document.MimeTypeDir))
            {
                Android.Util.Log.Warn("EmueraMaui",
                    $"EnsureRealDirectoryUri: parent depth >1 not supported yet: {path}");
                return null;
            }
            var grandReal = IsTreeRootUri(grand)
                ? DocumentsContract.BuildDocumentUriUsingTree(
                    _treeAndroidUri, DocumentsContract.GetTreeDocumentId(_treeAndroidUri))
                : grand;
            if (grandReal == null) return null;
            effectiveParent = FindChildDocument(grandReal, parentName, wantDir: true);
            if (effectiveParent == null)
            {
                effectiveParent = DocumentsContract.CreateDocument(
                    _context.ContentResolver!,
                    grandReal,
                    DocumentsContract.Document.MimeTypeDir,
                    parentName);
            }
        }

        if (effectiveParent == null) return null;

        var existing = FindChildDocument(effectiveParent, name, wantDir: true);
        if (existing != null) return existing;

        var created = DocumentsContract.CreateDocument(
            _context.ContentResolver!,
            effectiveParent,
            DocumentsContract.Document.MimeTypeDir,
            name);
        if (created != null)
            Android.Util.Log.Info("EmueraMaui", $"EnsureRealDirectoryUri created {name} → {created}");
        return created;
    }

    private bool TryQueryDocument(Android.Net.Uri docUri, out string? mime)
    {
        mime = null;
        try
        {
            using var cursor = _context.ContentResolver!.Query(
                docUri,
                new[] { DocumentsContract.Document.ColumnMimeType },
                null, null, null);
            if (cursor?.MoveToFirst() != true) return false;
            mime = cursor.GetString(0);
            return true;
        }
        catch { return false; }
    }

    /// <summary>解析已存在文件的真实 document URI；不存在返回 null。</summary>
    private Android.Net.Uri? ResolveExistingFileUri(string path)
    {
        if (_treeAndroidUri == null || !TryParseUri(path, out var docUri)) return null;

        if (TryQueryDocument(docUri, out var mime)
            && mime != null
            && mime != DocumentsContract.Document.MimeTypeDir)
            return docUri;

        if (!TryGetParentAndName(path, out var parent, out var name)) return null;
        return FindChildDocument(parent, name, wantDir: false);
    }

    private Android.Net.Uri? FindChildDocument(Android.Net.Uri parentUri, string displayName, bool wantDir)
    {
        if (_treeAndroidUri == null) return null;
        try
        {
            var parentId = ResolveDocId(parentUri);
            var childrenUri = DocumentsContract.BuildChildDocumentsUriUsingTree(_treeAndroidUri, parentId);
            using var cursor = _context.ContentResolver!.Query(
                childrenUri,
                new[]
                {
                    DocumentsContract.Document.ColumnDocumentId,
                    DocumentsContract.Document.ColumnDisplayName,
                    DocumentsContract.Document.ColumnMimeType
                },
                null, null, null);
            if (cursor == null) return null;

            while (cursor.MoveToNext())
            {
                var name = cursor.GetString(1);
                var mime = cursor.GetString(2);
                var isDir = mime == DocumentsContract.Document.MimeTypeDir;
                if (isDir != wantDir) continue;
                if (!string.Equals(name, displayName, StringComparison.OrdinalIgnoreCase))
                {
                    // displayName 无扩展时回退 documentId 末段
                    var docId = cursor.GetString(0);
                    if (docId == null) continue;
                    var last = docId.LastIndexOf('/');
                    var tail = last >= 0 ? docId[(last + 1)..] : docId;
                    if (!string.Equals(tail, displayName, StringComparison.OrdinalIgnoreCase))
                        continue;
                }
                var childId = cursor.GetString(0);
                return DocumentsContract.BuildDocumentUriUsingTree(_treeAndroidUri, childId!);
            }
        }
        catch (Exception ex)
        {
            Android.Util.Log.Warn("EmueraMaui", $"FindChildDocument({displayName}) → {ex.Message}");
        }
        return null;
    }

    /// <summary>
    /// 从 document/tree 路径拆出父目录 URI 与逻辑短名。
    /// documentId 形如 primary:Download/game/sav/global.sav → parent=.../sav, name=global.sav
    /// </summary>
    private bool TryGetParentAndName(string path, [NotNullWhen(true)] out Android.Net.Uri? parentUri, [NotNullWhen(true)] out string? name)
    {
        parentUri = null;
        name = null;
        if (_treeAndroidUri == null || !TryParseUri(path, out var docUri)) return false;

        var docId = ResolveDocId(docUri);
        var lastSlash = docId.LastIndexOf('/');
        if (lastSlash < 0)
        {
            // 单段 documentId：父为树根
            name = docId;
            var treeId = DocumentsContract.GetTreeDocumentId(_treeAndroidUri);
            parentUri = DocumentsContract.BuildDocumentUriUsingTree(_treeAndroidUri, treeId);
            return parentUri != null && !string.IsNullOrEmpty(name);
        }

        var parentId = docId[..lastSlash];
        name = docId[(lastSlash + 1)..];
        if (string.IsNullOrEmpty(name)) return false;
        parentUri = DocumentsContract.BuildDocumentUriUsingTree(_treeAndroidUri, parentId);
        return parentUri != null;
    }

    private Stream? TryOpenOutputStream(Android.Net.Uri docUri, string mode)
    {
        try
        {
            return _context.ContentResolver!.OpenOutputStream(docUri, mode);
        }
        catch (Exception ex)
        {
            Android.Util.Log.Warn("EmueraMaui", $"OpenOutputStream({mode}) failed: {ex.Message}");
            return null;
        }
    }

    private static string GuessMime(string displayName)
    {
        var ext = Path.GetExtension(displayName);
        if (ext.Equals(".txt", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".csv", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".erb", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".erh", StringComparison.OrdinalIgnoreCase))
            return "text/plain";
        return OctetStreamMime;
    }

    /// <summary>
    /// SAF 写缓冲流：引擎侧写内存（可 Seek），Dispose 时一次 OpenOutputStream + CopyTo。
    /// 避免 BinaryWriter 对数十 MB 存档逐字段打 ContentResolver 导致假死。
    /// </summary>
    private sealed class DeferredSafWriteStream : Stream
    {
        private readonly Context _context;
        private readonly Android.Net.Uri _docUri;
        private readonly string _pathForLog;
        private readonly bool _createdDocument;
        private readonly MemoryStream _buffer = new();
        private bool _disposed;
        private bool _writeFailed;

        public DeferredSafWriteStream(
            Context context, Android.Net.Uri docUri, string pathForLog, bool createdDocument)
        {
            _context = context;
            _docUri = docUri;
            _pathForLog = pathForLog;
            _createdDocument = createdDocument;
        }

        public override bool CanRead => false;
        public override bool CanSeek => true;
        public override bool CanWrite => true;
        public override long Length => _buffer.Length;
        public override long Position
        {
            get => _buffer.Position;
            set => _buffer.Position = value;
        }

        public override void Flush() => _buffer.Flush();

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException("DeferredSafWriteStream is write-only");

        public override long Seek(long offset, SeekOrigin origin) => _buffer.Seek(offset, origin);

        public override void SetLength(long value)
        {
            try
            {
                EnsureWithinLimit(value);
                _buffer.SetLength(value);
            }
            catch
            {
                _writeFailed = true;
                throw;
            }
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            try
            {
                EnsureWriteWithinLimit(count);
                _buffer.Write(buffer, offset, count);
            }
            catch
            {
                _writeFailed = true;
                throw;
            }
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            try
            {
                EnsureWriteWithinLimit(buffer.Length);
                _buffer.Write(buffer);
            }
            catch
            {
                _writeFailed = true;
                throw;
            }
        }

        public override void WriteByte(byte value)
        {
            try
            {
                EnsureWriteWithinLimit(1);
                _buffer.WriteByte(value);
            }
            catch
            {
                _writeFailed = true;
                throw;
            }
        }

        private void EnsureWriteWithinLimit(int count)
        {
            var limit = DeferredWriteLimitBytes;
            if (count < 0 || _buffer.Position > limit - count)
                throw new IOException(
                    $"DeferredSafWriteStream limit exceeded for {_pathForLog}; limit={limit} bytes");
        }

        private void EnsureWithinLimit(long value)
        {
            var limit = DeferredWriteLimitBytes;
            if (value < 0 || value > limit)
                throw new IOException(
                    $"DeferredSafWriteStream limit exceeded for {_pathForLog}; limit={limit} bytes");
        }

        protected override void Dispose(bool disposing)
        {
            if (_disposed) return;
            _disposed = true;
            if (disposing)
            {
                try
                {
                    if (_writeFailed)
                    {
                        DeleteCreatedDocument();
                        base.Dispose(disposing);
                        return;
                    }
                    var bytes = _buffer.Length;
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    _buffer.Position = 0;
                    using var output = _context.ContentResolver!.OpenOutputStream(_docUri, "wt")
                        ?? _context.ContentResolver.OpenOutputStream(_docUri, "w")
                        ?? throw new IOException($"DeferredSafWriteStream: OpenOutputStream null for {_docUri}");
                    _buffer.CopyTo(output);
                    output.Flush();
                    Android.Util.Log.Info("EmueraMaui",
                        $"OpenWrite flush OK path={_pathForLog} bytes={bytes} ms={sw.ElapsedMilliseconds}");
                }
                catch (Exception ex)
                {
                    Android.Util.Log.Error("EmueraMaui",
                        $"OpenWrite flush FAIL path={_pathForLog} ex={ex}");
                    DeleteCreatedDocument();
                    throw;
                }
                finally
                {
                    _buffer.Dispose();
                }
            }
            base.Dispose(disposing);
        }

        private void DeleteCreatedDocument()
        {
            if (!_createdDocument) return;
            try
            {
                DocumentsContract.DeleteDocument(_context.ContentResolver!, _docUri);
                Android.Util.Log.Info("EmueraMaui",
                    $"OpenWrite cleanup removed incomplete document path={_pathForLog}");
            }
            catch (Exception cleanupEx)
            {
                Android.Util.Log.Warn("EmueraMaui",
                    $"OpenWrite cleanup failed path={_pathForLog} ex={cleanupEx.Message}");
            }
        }
    }
}
#endif
