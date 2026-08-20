using System.IO;
using MinorShift.Emuera.Assets;
using Xunit;

namespace Emuera.Headless.Tests;

/// <summary>
/// 03 — /assets 资源通道的路径消毒与白名单校验（spec 测试 seam ②，Q5 安全决策）。
/// AssetPathValidator 是 C# 侧单点实现：Web 端点（03）与安卓 PathHandler（05）复用同一函数。
/// 输入契约：URL 已解码的相对路径（Kestrel 路由参数 / WebView URL 均解码后传入）。
/// </summary>
public class AssetPathValidatorTests
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "emuera-assets-test-" + Path.GetRandomFileName());

    public AssetPathValidatorTests()
    {
        Directory.CreateDirectory(_root);
    }

    // ---------- TryResolve：路径消毒 ----------

    [Fact]
    public void Resolve_valid_relative_path_stays_in_root()
    {
        Assert.True(AssetPathValidator.TryResolve(_root, "img/test.png", out var full));
        Assert.StartsWith(_root, full);
        Assert.EndsWith("test.png", full);
    }

    [Fact]
    public void Resolve_nested_subdir_is_allowed()
    {
        Assert.True(AssetPathValidator.TryResolve(_root, "resources/bg/forest.png", out var full));
        Assert.EndsWith("forest.png", full);
    }

    [Fact]
    public void Resolve_rejects_parent_traversal()
    {
        Assert.False(AssetPathValidator.TryResolve(_root, "../outside.png", out _));
    }

    [Fact]
    public void Resolve_rejects_encoded_parent_traversal()
    {
        // URL 编码绕过：%2e%2e 解码后必须被拒
        Assert.False(AssetPathValidator.TryResolve(_root, "%2e%2e/outside.png", out _));
        Assert.False(AssetPathValidator.TryResolve(_root, "img/%2e%2e/%2e%2e/outside.png", out _));
    }

    [Fact]
    public void Resolve_rejects_absolute_path()
    {
        // 平台无关的绝对路径构造（C:/... 硬编码在非 Windows 上不是 rooted）
        var abs = Path.Combine(Path.GetTempPath(), "evil.png");
        Assert.False(AssetPathValidator.TryResolve(_root, abs, out _));
    }

    [Fact]
    public void Resolve_rejects_encoded_parent_traversal_double_encoded()
    {
        // 双重编码：%252e 在 content 分支的 documentId 前缀比较里还会 Unescape 一次，
        // 若外层段校验只看一层就会重组出 `..` 绕过。循环解码后必须仍被拒。
        Assert.False(AssetPathValidator.TryResolve(_root, "%252e%252e/outside.png", out _));
        const string root = "content://com.android.externalstorage.documents/tree/primary%3AEmuera";
        Assert.False(AssetPathValidator.TryResolve(root, "%252e%252e/outside.png", out _));
    }

    [Fact]
    public void Resolve_rejects_leading_slash()
    {
        Assert.False(AssetPathValidator.TryResolve(_root, "/img/test.png", out _));
    }

    [Fact]
    public void Resolve_rejects_backslash_traversal()
    {
        // Windows 分隔符变体：..\ 必须被拒
        Assert.False(AssetPathValidator.TryResolve(_root, "img\\..\\..\\evil.png", out _));
    }

    [Fact]
    public void Resolve_rejects_double_slash_and_dot_segment()
    {
        Assert.False(AssetPathValidator.TryResolve(_root, "img//test.png", out _));
        Assert.False(AssetPathValidator.TryResolve(_root, "img/./test.png", out _));
    }

    [Fact]
    public void Resolve_rejects_path_escaping_root_after_normalization()
    {
        // 规范化后仍必须落根内（段组合绕过）
        Assert.False(AssetPathValidator.TryResolve(_root, "img/../..//outside.png", out _));
        Assert.False(AssetPathValidator.TryResolve(_root, "a/b/../../../c.png", out _));
    }

    [Fact]
    public void Resolve_empty_input_rejected()
    {
        Assert.False(AssetPathValidator.TryResolve(_root, "", out _));
        Assert.False(AssetPathValidator.TryResolve(_root, "  ", out _));
        Assert.False(AssetPathValidator.TryResolve("", "img/x.png", out _));
    }

    [Fact]
    public void Resolve_content_uri_root_saf_semantics()
    {
        // SAF content:// 根：不经 Path 规范化（content URI 不是文件系统路径），documentId 前缀比较
        const string root = "content://com.android.externalstorage.documents/tree/primary%3AEmuera";
        Assert.True(AssetPathValidator.TryResolve(root, "img/test.png", out var full));
        Assert.StartsWith(root, full);

        Assert.False(AssetPathValidator.TryResolve(root, "../outside.png", out _));
        Assert.False(AssetPathValidator.TryResolve(root, "/img/test.png", out _));
    }

    // ---------- IsAllowedImage：扩展名白名单 ----------

    [Theory]
    [InlineData("img/a.png")]
    [InlineData("img/a.jpg")]
    [InlineData("img/a.jpeg")]
    [InlineData("img/a.gif")]
    [InlineData("img/a.webp")]
    [InlineData("img/a.bmp")]
    [InlineData("img/A.PNG")] // 大小写不敏感
    public void IsAllowedImage_accepts_whitelist_extensions(string path)
    {
        Assert.True(AssetPathValidator.IsAllowedImage(Path.Combine(_root, path)));
    }

    [Theory]
    [InlineData("csv/GameBase.csv")]
    [InlineData("erb/TEST.ERB")]
    [InlineData("emuera.config")]
    [InlineData("debug/agent.log")]
    [InlineData("img/noext")]
    public void IsAllowedImage_rejects_non_image_extensions(string path)
    {
        Assert.False(AssetPathValidator.IsAllowedImage(Path.Combine(_root, path)));
    }

    // ---------- GetMimeType ----------

    [Theory]
    [InlineData("a.png", "image/png")]
    [InlineData("a.jpg", "image/jpeg")]
    [InlineData("a.jpeg", "image/jpeg")]
    [InlineData("a.gif", "image/gif")]
    [InlineData("a.webp", "image/webp")]
    [InlineData("a.bmp", "image/bmp")]
    [InlineData("a.csv", "application/octet-stream")]
    public void GetMimeType_maps_extensions(string name, string expected)
    {
        Assert.Equal(expected, AssetPathValidator.GetMimeType(Path.Combine(_root, name)));
    }
}
