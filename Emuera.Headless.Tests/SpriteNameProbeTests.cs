using System;
using System.IO;
using MinorShift.Emuera;
using MinorShift.Emuera.UI.Game.Image;
using Xunit;

namespace Emuera.Headless.Tests;

/// <summary>
/// 探针 sprite 名回退（issue：era 游戏 <c>&lt;img src='Face_1'&gt;</c> 的 src 是 sprite 名，
/// 映射在 resources/*.csv）。<c>TryGetImageSize</c> 先按根相对路径试（兼容 test_game 的
/// <c>img/test.png</c>），失败后经 <see cref="ImageNameTable"/> 查 sprite 名 → 文件再探针。
/// GamePaths 敏感（静态 Current）→ 放串行隔离集合。
/// </summary>
[Collection("GamePathsIsolated")]
public class SpriteNameProbeTests : IDisposable
{
    private readonly string _root;
    private readonly GamePaths _paths;

    public SpriteNameProbeTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "emuera_spriteprobe_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "resources"));
        Directory.CreateDirectory(Path.Combine(_root, "img"));
        // sprite 表：Face_1 → resources/1_Face.png（8×4 迷你 PNG）
        File.WriteAllText(Path.Combine(_root, "resources", "Face.csv"), "FACE_1,1_Face.png,0,0,180,180\n");
        File.WriteAllBytes(Path.Combine(_root, "resources", "1_Face.png"), MiniPng());
        // 直接相对路径（test_game 形态）
        File.WriteAllBytes(Path.Combine(_root, "img", "test.png"), MiniPng());
        _paths = GamePaths.Resolve(_root, new FileSystemGameDirAccessor());
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    [Fact]
    public void Sprite_name_returns_probed_size()
    {
        Assert.True(AppContents.TryGetImageSize("Face_1", out var w, out var h));
        Assert.Equal(8, w);
        Assert.Equal(4, h);
    }

    [Fact]
    public void Direct_relative_path_still_works()
    {
        // 兼容 test_game：相对路径优先，不经 sprite 表
        Assert.True(AppContents.TryGetImageSize("img/test.png", out var w, out var h));
        Assert.Equal(8, w);
        Assert.Equal(4, h);
    }

    [Fact]
    public void Unknown_name_returns_false()
    {
        Assert.False(AppContents.TryGetImageSize("nope", out _, out _));
    }

    /// <summary>8×4 RGBA PNG 头（signature + IHDR + IEND）——探针只需前 24 字节。</summary>
    private static byte[] MiniPng()
    {
        var bytes = new byte[33];
        byte[] sig = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        sig.CopyTo(bytes, 0);
        // IHDR chunk: len(4) tag(4) width(4) height(4) bitdepth(1) colortype(1) ...
        byte[] ihdr = [0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
                       0x00, 0x00, 0x00, 0x08, 0x00, 0x00, 0x00, 0x04,
                       0x08, 0x06, 0x00, 0x00, 0x00];
        ihdr.CopyTo(bytes, 8);
        // IEND 占位（探针不校验 CRC）
        bytes[29] = 0x49; bytes[30] = 0x45; bytes[31] = 0x4E; bytes[32] = 0x44;
        return bytes;
    }

    // ---------- B：GetSprite 无头替身（SPRITECREATED 依赖它） ----------

    [Fact]
    public void GetSprite_sprite_name_returns_created_stub_with_size()
    {
        var sprite = AppContents.GetSprite("Face_1");
        Assert.NotNull(sprite);
        Assert.True(sprite!.IsCreated); // SPRITECREATED 判定依赖
        Assert.Equal(8, sprite.DestBaseSize.Width);   // 探针整图 8×4
        Assert.Equal(4, sprite.DestBaseSize.Height);
    }

    [Fact]
    public void GetSprite_unknown_name_returns_null()
    {
        Assert.Null(AppContents.GetSprite("nope"));
    }

    [Fact]
    public void GetSprite_direct_relative_path_returns_stub()
    {
        // test_game 形态：相对路径（img/test.png）也能建替身
        var sprite = AppContents.GetSprite("img/test.png");
        Assert.NotNull(sprite);
        Assert.True(sprite!.IsCreated);
        Assert.Equal(8, sprite.DestBaseSize.Width);
    }

    [Fact]
    public void GetSprite_switches_map_on_game_root_change()
    {
        // 目录切换：新根同名 sprite → 新文件尺寸
        var root2 = Path.Combine(Path.GetTempPath(), "emuera_spriteprobe2_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root2, "resources"));
            // 16×4 PNG：IHDR width=16
            var png = MiniPng();
            png[16] = 0; png[17] = 0; png[18] = 0; png[19] = 16;
            File.WriteAllBytes(Path.Combine(root2, "resources", "1_Face.png"), png);
            File.WriteAllText(Path.Combine(root2, "resources", "Face.csv"), "FACE_1,1_Face.png,0,0,180,180\n");
            GamePaths.Resolve(root2, new FileSystemGameDirAccessor());
            try
            {
                var sprite = AppContents.GetSprite("Face_1");
                Assert.NotNull(sprite);
                Assert.Equal(16, sprite!.DestBaseSize.Width);
            }
            finally
            {
                GamePaths.Resolve(_root, new FileSystemGameDirAccessor());
            }
        }
        finally
        {
            try { Directory.Delete(root2, true); } catch { }
        }
    }
}
