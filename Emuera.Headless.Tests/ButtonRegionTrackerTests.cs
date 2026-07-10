using System;
using MinorShift.Emuera.GameView;
using MinorShift.Emuera.UI.Game;
using Xunit;

namespace Emuera.Headless.Tests;

/// <summary>
/// ButtonRegionTracker 单元测试（ADR-0010 C2/C3）。
/// 用 TestButtonFactory 创建测试按钮（null console → Generation=0），
/// 调 RecordLineRegions 记录区域，调 HitTest 断言命中/未命中。
/// 不依赖真实 EmueraConsole / IConsoleUI / Config。
/// </summary>
public class ButtonRegionTrackerTests
{
    // ---------- C2：HitTest 按钮命中 ----------

    [Fact]
    public void C2_hit_test_within_button_region_hits()
    {
        var tracker = new ButtonRegionTracker();
        var buttons = new[] { TestButtonFactory.CreateButton("[OK]", input: 1) };

        // formattedLine="" → LeadingDisplayWidth=0，按钮从 col=0 开始
        // "[OK]" 显示宽度=4，区域 col=[0,3]
        tracker.RecordLineRegions("X", bufferRow: 10, buttons, currentGeneration: 0);

        var hit = tracker.HitTest(row: 10, col: 0);
        Assert.NotNull(hit);
        Assert.Equal("[OK]", hit!.ToString());
    }

    [Fact]
    public void C2_hit_test_at_right_edge_hits()
    {
        var tracker = new ButtonRegionTracker();
        var buttons = new[] { TestButtonFactory.CreateButton("[OK]", input: 1) };

        tracker.RecordLineRegions("X", bufferRow: 10, buttons, currentGeneration: 0);

        // col=3 是 "[OK]" 的最后一列（区域 [0,3]）
        var hit = tracker.HitTest(row: 10, col: 3);
        Assert.NotNull(hit);
    }

    [Fact]
    public void C2_hit_test_past_right_edge_misses()
    {
        var tracker = new ButtonRegionTracker();
        var buttons = new[] { TestButtonFactory.CreateButton("[OK]", input: 1) };

        tracker.RecordLineRegions("X", bufferRow: 10, buttons, currentGeneration: 0);

        // col=4 在区域 [0,3] 之外
        var hit = tracker.HitTest(row: 10, col: 4);
        Assert.Null(hit);
    }

    [Fact]
    public void C2_hit_test_wrong_row_misses()
    {
        var tracker = new ButtonRegionTracker();
        var buttons = new[] { TestButtonFactory.CreateButton("[OK]", input: 1) };

        tracker.RecordLineRegions("X", bufferRow: 10, buttons, currentGeneration: 0);

        var hit = tracker.HitTest(row: 9, col: 0);
        Assert.Null(hit);
    }

    [Fact]
    public void C2_leading_display_width_offsets_button_column()
    {
        var tracker = new ButtonRegionTracker();
        var buttons = new[] { TestButtonFactory.CreateButton("[OK]", input: 1) };

        // formattedLine="  " → LeadingDisplayWidth=2，按钮从 col=2 开始
        // 区域 col=[2,5]
        tracker.RecordLineRegions("  ", bufferRow: 0, buttons, currentGeneration: 0);

        Assert.Null(tracker.HitTest(row: 0, col: 1)); // col=1 在区域之前
        Assert.NotNull(tracker.HitTest(row: 0, col: 2)); // col=2 是起始
        Assert.NotNull(tracker.HitTest(row: 0, col: 5)); // col=5 是末列
        Assert.Null(tracker.HitTest(row: 0, col: 6)); // col=6 在区域之后
    }

    [Fact]
    public void C2_multiple_buttons_on_same_line()
    {
        var tracker = new ButtonRegionTracker();
        var buttons = TestButtonFactory.CreateButtons(("[OK]", 1), ("[Cancel]", 2));

        // "[OK]" 宽度=4 → col=[0,3]；"[Cancel]" 宽度=8 → col=[4,11]
        tracker.RecordLineRegions("X", bufferRow: 5, buttons, currentGeneration: 0);

        var hitOk = tracker.HitTest(row: 5, col: 1);
        Assert.NotNull(hitOk);
        Assert.Equal("[OK]", hitOk!.ToString());

        var hitCancel = tracker.HitTest(row: 5, col: 7);
        Assert.NotNull(hitCancel);
        Assert.Equal("[Cancel]", hitCancel!.ToString());
    }

    [Fact]
    public void C2_overlapping_buttons_last_recorded_wins()
    {
        var tracker = new ButtonRegionTracker();
        var btn1 = TestButtonFactory.CreateButton("[OK]", input: 1);
        var btn2 = TestButtonFactory.CreateButton("[NO]", input: 2);

        // 同一行同一位置先记录 btn1，再记录 btn2
        tracker.RecordLineRegions("X", bufferRow: 0, new[] { btn1 }, currentGeneration: 0);
        tracker.RecordLineRegions("X", bufferRow: 0, new[] { btn2 }, currentGeneration: 0);

        // HitTest 从后往前匹配——后记录的 btn2 优先
        var hit = tracker.HitTest(row: 0, col: 0);
        Assert.NotNull(hit);
        Assert.Equal("[NO]", hit!.ToString());
    }

    [Fact]
    public void C2_empty_buttons_array_no_regions_recorded()
    {
        var tracker = new ButtonRegionTracker();

        tracker.RecordLineRegions("X", bufferRow: 0, Array.Empty<ConsoleButtonString>(), currentGeneration: 0);

        Assert.Equal(0, tracker.RegionCount);
    }

    [Fact]
    public void C2_null_buttons_array_no_regions_recorded()
    {
        var tracker = new ButtonRegionTracker();

        tracker.RecordLineRegions("X", bufferRow: 0, null, currentGeneration: 0);

        Assert.Equal(0, tracker.RegionCount);
    }

    [Fact]
    public void C2_clear_removes_all_regions()
    {
        var tracker = new ButtonRegionTracker();
        var buttons = new[] { TestButtonFactory.CreateButton("[OK]", input: 1) };

        tracker.RecordLineRegions("X", bufferRow: 0, buttons, currentGeneration: 0);
        Assert.Equal(1, tracker.RegionCount);

        tracker.Clear();
        Assert.Equal(0, tracker.RegionCount);
        Assert.Null(tracker.HitTest(row: 0, col: 0));
    }

    // ---------- C3：Generation 过期 ----------

    [Fact]
    public void C3_button_with_matching_generation_registered()
    {
        var tracker = new ButtonRegionTracker();
        var buttons = new[] { TestButtonFactory.CreateButton("[OK]", input: 1) };

        // Generation=0（null console），currentGeneration=0 → 匹配
        tracker.RecordLineRegions("X", bufferRow: 0, buttons, currentGeneration: 0);

        Assert.Equal(1, tracker.RegionCount);
        Assert.NotNull(tracker.HitTest(row: 0, col: 0));
    }

    [Fact]
    public void C3_button_with_mismatched_generation_not_registered()
    {
        var tracker = new ButtonRegionTracker();
        var buttons = new[] { TestButtonFactory.CreateButton("[OK]", input: 1) };

        // Generation=0（null console），currentGeneration=1 → 不匹配
        tracker.RecordLineRegions("X", bufferRow: 0, buttons, currentGeneration: 1);

        Assert.Equal(0, tracker.RegionCount);
        Assert.Null(tracker.HitTest(row: 0, col: 0));
    }

    [Fact]
    public void C3_generation_change_clears_old_regions_via_clear()
    {
        var tracker = new ButtonRegionTracker();
        var buttons = new[] { TestButtonFactory.CreateButton("[OK]", input: 1) };

        // Generation 0：记录按钮
        tracker.RecordLineRegions("X", bufferRow: 5, buttons, currentGeneration: 0);
        Assert.NotNull(tracker.HitTest(row: 5, col: 0));

        // 新 Generation：先 Clear，再 RecordLineRegions（模拟 RefreshButtonRegions 流程）
        tracker.Clear();
        tracker.RecordLineRegions("X", bufferRow: 5, buttons, currentGeneration: 1);

        // Generation 0 的按钮在 currentGeneration=1 时不被记录
        Assert.Equal(0, tracker.RegionCount);
        Assert.Null(tracker.HitTest(row: 5, col: 0));
    }

    [Fact]
    public void C3_non_button_not_registered()
    {
        var tracker = new ButtonRegionTracker();
        var nonButton = TestButtonFactory.CreateNonButton("[Text]");

        tracker.RecordLineRegions("X", bufferRow: 0, new[] { nonButton }, currentGeneration: 0);

        Assert.Equal(0, tracker.RegionCount);
    }
}
