using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MinorShift.Emuera.Runtime.Utils;
using Xunit;

namespace MinorShift.Emuera.Tests;

/// <summary>
/// SAF 运行时契约：content URI 的文件操作必须委托给 IGameDirAccessor，不能退回 System.IO 路径语义。
/// </summary>
[Collection("LoaderTests")]
public class SafCompatContentUriTests
{
    private const string Root =
        "content://com.android.externalstorage.documents/tree/primary%3Aemuera/document/primary%3Aemuera%2FTK";

    [Fact]
    public void Content_uri_write_read_delete_roundtrip_uses_accessor()
    {
        var accessor = new InMemoryContentDirAccessor(Root);
        _ = GamePaths.Resolve(Root, accessor);

        var savDir = SafCompat.ResolveSubPath(Root, "sav");
        SafCompat.CreateDirectory(savDir);
        var savePath = SafCompat.CombinePath(savDir, "global.sav");
        Assert.Equal(savDir, accessor.GetParentPath(savePath));

        SafCompat.WriteAllText(savePath, "global-data", Encoding.UTF8);

        Assert.True(accessor.DirectoryExists(savDir));
        Assert.True(accessor.FileExists(savePath));
        Assert.Equal("global.sav", SafPath.GetLogicalFileName(savePath));
        Assert.Equal("global-data", SafCompat.ReadAllText(savePath, Encoding.UTF8));
        Assert.Equal(1, accessor.OpenWriteCalls);
        Assert.Equal(1, accessor.OpenReadCalls);

        SafCompat.Delete(savePath);

        Assert.False(accessor.FileExists(savePath));
        Assert.Equal(1, accessor.DeleteCalls);
    }

    [Fact]
    public void Content_uri_root_directory_exists_uses_accessor_semantics()
    {
        var accessor = new InMemoryContentDirAccessor(Root);

        Assert.True(GameScanner.RootDirectoryExists(Root, accessor));
        Assert.False(GameScanner.RootDirectoryExists(
            "content://com.android.externalstorage.documents/tree/primary%3Amissing/document/primary%3Amissing",
            accessor));
    }

    [Fact]
    public void Content_uri_getfiles_preserves_logical_filenames()
    {
        var accessor = new InMemoryContentDirAccessor(Root);
        _ = GamePaths.Resolve(Root, accessor);
        var datDir = SafCompat.ResolveSubPath(Root, "dat");
        SafCompat.CreateDirectory(datDir);
        accessor.Seed(SafCompat.CombinePath(datDir, "var_01.dat"), [0x01]);
        accessor.Seed(SafCompat.CombinePath(datDir, "var_custom.dat"), [0x02]);

        var files = SafCompat.GetFiles(datDir, "var_*.dat", SearchOption.TopDirectoryOnly);

        Assert.Equal(["var_01.dat", "var_custom.dat"], files
            .Select(SafPath.GetLogicalFileName)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray());
    }

    [Fact]
    public void Content_uri_write_contract_handles_overwrite_empty_nested_and_missing_delete()
    {
        var accessor = new InMemoryContentDirAccessor(Root);
        _ = GamePaths.Resolve(Root, accessor);

        var nestedDir = SafCompat.ResolveSubPath(SafCompat.ResolveSubPath(Root, "sav"), "nested");
        SafCompat.CreateDirectory(nestedDir);
        var path = SafCompat.CombinePath(nestedDir, "slot.sav");

        SafCompat.WriteAllText(path, "first", Encoding.UTF8);
        SafCompat.WriteAllText(path, "second", Encoding.UTF8);
        Assert.Equal("second", SafCompat.ReadAllText(path, Encoding.UTF8));

        SafCompat.WriteAllText(path, string.Empty, Encoding.UTF8);
        Assert.Equal(string.Empty, SafCompat.ReadAllText(path, Encoding.UTF8));
        SafCompat.Delete(SafCompat.CombinePath(nestedDir, "missing.sav"));
        Assert.True(accessor.FileExists(path));
    }

    internal sealed class InMemoryContentDirAccessor : IGameDirAccessor
    {
        private readonly HashSet<string> _directories = new(StringComparer.Ordinal);
        private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);

        public int OpenReadCalls { get; private set; }
        public int OpenWriteCalls { get; private set; }
        public int DeleteCalls { get; private set; }

        public InMemoryContentDirAccessor(string root)
        {
            _directories.Add(root);
        }

        public Task<string?> PickDirectoryAsync(CancellationToken ct = default) => Task.FromResult<string?>(null);
        public bool DirectoryExists(string path) => _directories.Contains(path);
        public bool FileExists(string path) => _files.ContainsKey(path);
        public string[] GetDirectories(string path) => [];
        public string[] GetDirectories(string path, string searchPattern, SearchOption searchOption) => [];

        public string[] GetFiles(string path) => _files.Keys.Where(file => IsDirectChild(path, file)).ToArray();

        public string[] GetFiles(string path, string searchPattern, SearchOption searchOption) => _files.Keys
            .Where(file => IsDirectChild(path, file))
            .Where(file => WildcardMatches(SafPath.GetLogicalFileName(file), searchPattern))
            .ToArray();

        public string ReadAllText(string path) => Encoding.UTF8.GetString(_files[path]);
        public byte[]? ReadAllBytes(string path) => _files.TryGetValue(path, out var bytes) ? bytes : null;

        public Stream? OpenRead(string path)
        {
            OpenReadCalls++;
            return _files.TryGetValue(path, out var bytes) ? new MemoryStream(bytes, writable: false) : null;
        }

        public void CreateDirectory(string path) => _directories.Add(path);

        public Stream OpenWrite(string path)
        {
            OpenWriteCalls++;
            return new CommitStream(bytes => _files[path] = bytes);
        }

        public void Delete(string path)
        {
            DeleteCalls++;
            _files.Remove(path);
        }

        public bool HasWriteAccess() => true;
        public string ResolveSubPath(string basePath, string subDir) => AppendDocumentSegment(basePath, subDir);
        public string CombinePath(string basePath, string filename) => AppendDocumentSegment(basePath, filename);
        public string GetParentPath(string path)
        {
            var docId = SafPath.TryGetDocumentId(path);
            if (docId == null) return path;
            var slash = docId.LastIndexOf('/');
            if (slash < 0) return path;
            var marker = "/document/";
            var markerPos = path.LastIndexOf(marker, StringComparison.Ordinal);
            return path[..(markerPos + marker.Length)] + Uri.EscapeDataString(docId[..slash]);
        }
        public string GetFileName(string path) => SafPath.GetLogicalFileName(path);
        public void Seed(string path, byte[] bytes) => _files[path] = bytes;

        private static bool IsDirectChild(string parent, string child)
        {
            var parentId = SafPath.TryGetDocumentId(parent)!;
            var childId = SafPath.TryGetDocumentId(child)!;
            return childId.StartsWith(parentId + "/", StringComparison.Ordinal)
                && childId[(parentId.Length + 1)..].IndexOf('/') < 0;
        }

        private static string AppendDocumentSegment(string basePath, string name)
        {
            var documentMarker = "/document/";
            var pos = basePath.LastIndexOf(documentMarker, StringComparison.Ordinal);
            Assert.True(pos >= 0, "Fake SAF accessor only accepts document URIs.");
            return basePath + "%2F" + Uri.EscapeDataString(name);
        }

        private static bool WildcardMatches(string value, string pattern)
        {
            var expression = "^" + System.Text.RegularExpressions.Regex.Escape(pattern)
                .Replace("\\*", ".*")
                .Replace("\\?", ".") + "$";
            return System.Text.RegularExpressions.Regex.IsMatch(value, expression,
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        private sealed class CommitStream(Action<byte[]> commit) : MemoryStream
        {
            private bool _committed;

            protected override void Dispose(bool disposing)
            {
                if (disposing && !_committed)
                {
                    _committed = true;
                    commit(ToArray());
                }
                base.Dispose(disposing);
            }
        }
    }
}
