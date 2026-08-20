using Android.Content;
using Android.Provider;
using AndroidX.Activity.Result;
using Debug = System.Diagnostics.Debug;

namespace SafVerification.Services;

/// <summary>
/// SAF 操作封装——目录选择、URI持久化、文件读取。
/// 单例由 MainActivity.OnCreate 初始化。
/// </summary>
public sealed class SafService
{
    public static SafService? Current { get; private set; }
    public static string? InitError { get; set; }

    private readonly Context _context;
    private readonly ActivityResultLauncher _launcher;
    private TaskCompletionSource<Android.Net.Uri?>? _tcs;

    private const string PrefKey = "saf_tree_uri";

    public SafService(Context context, ActivityResultLauncher launcher)
    {
        _context = context;
        _launcher = launcher;
        Current = this;
    }

    // ── 目录选择 ──────────────────────────────────

    /// <summary>
    /// 启动 SAF 目录选择器。用户选完 / 取消后 Task 完成。
    /// 5 分钟超时。
    /// </summary>
    public Task<Android.Net.Uri?> PickDirectoryAsync(CancellationToken ct = default)
    {
        _tcs?.TrySetCanceled();
        _tcs = new TaskCompletionSource<Android.Net.Uri?>();

        ct.Register(() => _tcs.TrySetCanceled(ct), useSynchronizationContext: false);

        _launcher!.Launch(null);
        return _tcs.Task;
    }

    /// <summary>
    /// ActivityResultCallback 入口——由 MainActivity 调用。
    /// </summary>
    public void OnTreeResult(Android.Net.Uri? uri)
    {
        if (_tcs == null) return;

        try
        {
            if (uri != null)
            {
                // 获取持久化读权限
                var flags = ActivityFlags.GrantReadUriPermission;
                _context.ContentResolver!.TakePersistableUriPermission(uri, flags);
                SaveUri(uri.ToString()!);
                Debug.WriteLine($"[SafVerification] OnTreeResult: uri={uri}, persistable permission granted");
            }
            else
            {
                Debug.WriteLine("[SafVerification] OnTreeResult: user cancelled");
            }

            _tcs.TrySetResult(uri);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SafVerification] OnTreeResult error: {ex}");
            _tcs.TrySetException(ex);
        }
    }

    // ── 持久化 ────────────────────────────────────

    public void SaveUri(string uri)
    {
        Microsoft.Maui.Storage.Preferences.Set(PrefKey, uri);
    }

    public string? LoadUri()
    {
        var uri = Microsoft.Maui.Storage.Preferences.Get(PrefKey, null);
        return string.IsNullOrEmpty(uri) ? null : uri;
    }

    /// <summary>
    /// 试探已存 URI 是否仍然有效。
    /// </summary>
    public bool IsUriValid(string? uriString = null)
    {
        uriString ??= LoadUri();
        if (string.IsNullOrEmpty(uriString)) return false;

        try
        {
            var uri = Android.Net.Uri.Parse(uriString);

            // 尝试查询根目录——任何异常都说明 URI 失效
            var rootDocId = DocumentsContract.GetTreeDocumentId(uri);
            var childrenUri = DocumentsContract.BuildChildDocumentsUriUsingTree(uri, rootDocId);

            using var cursor = _context.ContentResolver!.Query(
                childrenUri,
                new[] { DocumentsContract.Document.ColumnDisplayName },
                null, null, null);

            // cursor 为 null 或查询抛异常 = URI 失效
            return cursor != null;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SafVerification] IsUriValid failed: {ex.Message}");
            return false;
        }
    }

    // ── 文件操作 ──────────────────────────────────

    /// <summary>
    /// 列出 URI 根目录下所有条目名。
    /// </summary>
    public List<string> ListRootFiles()
    {
        var result = new List<string>();
        var uriString = LoadUri();
        if (string.IsNullOrEmpty(uriString)) return result;

        var treeUri = Android.Net.Uri.Parse(uriString);
        var rootDocId = DocumentsContract.GetTreeDocumentId(treeUri);
        var childrenUri = DocumentsContract.BuildChildDocumentsUriUsingTree(treeUri, rootDocId);

        using var cursor = _context.ContentResolver!.Query(
            childrenUri,
            new[]
            {
                DocumentsContract.Document.ColumnDisplayName,
                DocumentsContract.Document.ColumnMimeType,
                DocumentsContract.Document.ColumnSize
            },
            null, null, null);

        if (cursor == null) return result;

        while (cursor.MoveToNext())
        {
            var name = cursor.GetString(0) ?? "(null)";
            var mime = cursor.GetString(1);
            var isDir = mime == DocumentsContract.Document.MimeTypeDir;
            var suffix = isDir ? "/" : $" ({cursor.GetLong(2)} B)";
            result.Add($"{name}{suffix}");
        }

        return result;
    }

    /// <summary>
    /// 从 URI 根目录读取指定文件名的文本内容。
    /// </summary>
    public string? ReadFileContent(string fileName)
    {
        var uriString = LoadUri();
        if (string.IsNullOrEmpty(uriString)) return null;

        var treeUri = Android.Net.Uri.Parse(uriString);

        // 通过 SAF tree 查找指定文件
        var fileUri = FindChildUri(treeUri, fileName);
        if (fileUri == null) return null;

        using var stream = _context.ContentResolver!.OpenInputStream(fileUri);
        if (stream == null) return null;

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// 在 treeUri 的根目录下查找 fileName，返回其 document URI。
    /// </summary>
    private Android.Net.Uri? FindChildUri(Android.Net.Uri treeUri, string fileName)
    {
        var rootDocId = DocumentsContract.GetTreeDocumentId(treeUri);
        var childrenUri = DocumentsContract.BuildChildDocumentsUriUsingTree(treeUri, rootDocId);

        using var cursor = _context.ContentResolver!.Query(
            childrenUri,
            new[]
            {
                DocumentsContract.Document.ColumnDocumentId,
                DocumentsContract.Document.ColumnDisplayName
            },
            null, null, null);

        if (cursor == null) return null;

        while (cursor.MoveToNext())
        {
            var docId = cursor.GetString(0);
            var name = cursor.GetString(1);
            if (name == fileName)
            {
                return DocumentsContract.BuildDocumentUriUsingTree(treeUri, docId);
            }
        }

        return null;
    }
}
