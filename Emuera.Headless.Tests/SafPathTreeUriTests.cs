using MinorShift.Emuera.Runtime.Utils;
using Xunit;

namespace Emuera.Headless.Tests;

/// <summary>
/// 03 — SafPath tree URI 支持回归测试。MAUI 安卓经 SAF 目录选择器拿到的游戏根是
/// tree URI（.../tree/...，无 /document/ 段）；TryGetDocumentId / IsUnderRoot 必须
/// 对 tree URI 根正确工作（AssetPathValidator content 分支的前置）。
/// </summary>
public class SafPathTreeUriTests
{
    private const string TreeRoot = "content://com.android.externalstorage.documents/tree/primary%3AEmuera";

    [Fact]
    public void TryGetDocumentId_extracts_tree_uri_document_id()
    {
        // DocumentsContract.GetTreeDocumentId 的纯 C# 等价：/tree/ 之后整段 Unescape
        Assert.Equal("primary:Emuera", SafPath.TryGetDocumentId(TreeRoot));
    }

    [Fact]
    public void TryGetDocumentId_prefers_document_segment_over_tree()
    {
        // document URI 含 /tree/ 段但必须按 /document/ 提取
        const string docUri = "content://com.android.externalstorage.documents/tree/primary%3AEmuera/document/primary%3AEmuera/img%2Ftest.png";
        Assert.Equal("primary:Emuera/img/test.png", SafPath.TryGetDocumentId(docUri));
    }

    [Fact]
    public void TryGetDocumentId_returns_null_for_non_content_path()
    {
        Assert.Null(SafPath.TryGetDocumentId("C:/game/img/x.png"));
    }

    [Fact]
    public void IsUnderRoot_accepts_child_below_tree_root()
    {
        var resolved = TreeRoot + "/img/test.png";
        Assert.True(SafPath.IsUnderRoot(TreeRoot, resolved));
    }

    [Fact]
    public void IsUnderRoot_rejects_path_outside_tree_root()
    {
        const string outside = "content://com.android.externalstorage.documents/tree/primary%3AOther/img/x.png";
        Assert.False(SafPath.IsUnderRoot(TreeRoot, outside));
    }

    [Fact]
    public void IsUnderRoot_rejects_non_content_child_for_content_root()
    {
        // content 根 vs 普通路径：不走 documentId 比较，按文件名规则应当拒绝
        Assert.False(SafPath.IsUnderRoot(TreeRoot, "C:/game/img/x.png"));
    }
}
