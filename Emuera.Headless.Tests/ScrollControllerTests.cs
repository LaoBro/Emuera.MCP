using System;
using MinorShift.Emuera.GameView;
using Xunit;

namespace Emuera.Headless.Tests;

/// <summary>
/// ScrollController 纯算术单元测试（ADR-0009 C7a）。
/// 验证 offset 状态机：ScrollBy/ScrollTo/Clamp/Reset + ScrollChanged 事件 + 钳位语义。
/// 不依赖任何 VT I/O —— _scrollVisibleLines 构造注入固定值。
/// </summary>
public class ScrollControllerTests
{
    // 测试常量：模拟 Scroll Mode 视口高度（WindowHeight - 2）。
    private const int ScrollVisibleLines = 23;

    // ---------- ScrollBy ----------

    [Fact]
    public void ScrollBy_positive_delta_increases_offset()
    {
        var sc = new ScrollController(ScrollVisibleLines);
        int raised = -1;
        sc.ScrollChanged += o => raised = o;

        int newOffset = sc.ScrollBy(delta: 3, lineCount: 100);

        Assert.Equal(3, newOffset);
        Assert.Equal(3, sc.ScrollOffset);
        Assert.True(sc.IsScrollMode);
        Assert.Equal(3, raised);
    }

    [Fact]
    public void ScrollBy_negative_delta_decreases_offset()
    {
        var sc = new ScrollController(ScrollVisibleLines);
        sc.ScrollBy(10, 100);

        int raised = -1;
        sc.ScrollChanged += o => raised = o;

        int newOffset = sc.ScrollBy(-3, 100);

        Assert.Equal(7, newOffset);
        Assert.Equal(7, raised);
    }

    [Fact]
    public void ScrollBy_clamps_to_zero_on_overshoot()
    {
        var sc = new ScrollController(ScrollVisibleLines);
        sc.ScrollBy(5, 100);

        int raised = -1;
        sc.ScrollChanged += o => raised = o;

        int newOffset = sc.ScrollBy(-100, 100);

        Assert.Equal(0, newOffset);
        Assert.False(sc.IsScrollMode);
        Assert.Equal(0, raised);
    }

    [Fact]
    public void ScrollBy_clamps_to_maxOffset_on_overshoot()
    {
        var sc = new ScrollController(ScrollVisibleLines);
        // lineCount=50, visibleLines=23 → maxOffset = 50 - 23 = 27
        int raised = -1;
        sc.ScrollChanged += o => raised = o;

        int newOffset = sc.ScrollBy(1000, 50);

        Assert.Equal(27, newOffset);
        Assert.Equal(27, raised);
    }

    [Fact]
    public void ScrollBy_no_change_does_not_raise_event()
    {
        var sc = new ScrollController(ScrollVisibleLines);
        sc.ScrollBy(5, 100); // offset=5

        int raiseCount = 0;
        sc.ScrollChanged += _ => raiseCount++;

        // delta=0 → offset 不变
        sc.ScrollBy(0, 100);

        Assert.Equal(0, raiseCount);
    }

    [Fact]
    public void ScrollBy_already_at_zero_no_change_does_not_raise()
    {
        var sc = new ScrollController(ScrollVisibleLines);
        int raiseCount = 0;
        sc.ScrollChanged += _ => raiseCount++;

        // offset=0 时 delta=-3 钳到 0 → 无变化 → 不 raise
        sc.ScrollBy(-3, 100);

        Assert.Equal(0, raiseCount);
        Assert.Equal(0, sc.ScrollOffset);
    }

    // ---------- ScrollTo ----------

    [Fact]
    public void ScrollTo_sets_absolute_offset()
    {
        var sc = new ScrollController(ScrollVisibleLines);
        sc.ScrollBy(5, 100);

        int raised = -1;
        sc.ScrollChanged += o => raised = o;

        int newOffset = sc.ScrollTo(10, 100);

        Assert.Equal(10, newOffset);
        Assert.Equal(10, raised);
    }

    [Fact]
    public void ScrollTo_int_max_clamps_to_maxOffset()
    {
        var sc = new ScrollController(ScrollVisibleLines);
        // Home 滚到顶：ScrollTo(int.MaxValue) 钳到 max
        int raised = -1;
        sc.ScrollChanged += o => raised = o;

        int newOffset = sc.ScrollTo(int.MaxValue, 50);

        Assert.Equal(27, newOffset); // 50 - 23 = 27
        Assert.Equal(27, raised);
    }

    [Fact]
    public void ScrollTo_zero_exits_scroll_mode()
    {
        var sc = new ScrollController(ScrollVisibleLines);
        sc.ScrollBy(10, 100);

        int raised = -1;
        sc.ScrollChanged += o => raised = o;

        int newOffset = sc.ScrollTo(0, 100);

        Assert.Equal(0, newOffset);
        Assert.False(sc.IsScrollMode);
        Assert.Equal(0, raised);
    }

    [Fact]
    public void ScrollTo_negative_treated_as_zero()
    {
        var sc = new ScrollController(ScrollVisibleLines);
        sc.ScrollBy(10, 100);

        int newOffset = sc.ScrollTo(-5, 100);

        Assert.Equal(0, newOffset);
        Assert.False(sc.IsScrollMode);
    }

    // ---------- Clamp ----------

    [Fact]
    public void Clamp_reduces_offset_when_lineCount_shrinks()
    {
        var sc = new ScrollController(ScrollVisibleLines);
        sc.ScrollBy(20, 100); // offset=20, maxOffset=100-23=77
        int raiseCount = 0;
        sc.ScrollChanged += _ => raiseCount++;

        // lineCount 缩到 30 → maxOffset = 30 - 23 = 7
        int newOffset = sc.Clamp(30);

        Assert.Equal(7, newOffset);
        Assert.Equal(7, sc.ScrollOffset);
        Assert.Equal(1, raiseCount);
    }

    [Fact]
    public void Clamp_no_change_does_not_raise()
    {
        var sc = new ScrollController(ScrollVisibleLines);
        sc.ScrollBy(5, 100); // offset=5, maxOffset=77
        int raiseCount = 0;
        sc.ScrollChanged += _ => raiseCount++;

        sc.Clamp(100); // maxOffset 仍 77，offset=5 不变

        Assert.Equal(0, raiseCount);
    }

    [Fact]
    public void Clamp_zero_offset_stays_zero_no_raise()
    {
        var sc = new ScrollController(ScrollVisibleLines);
        int raiseCount = 0;
        sc.ScrollChanged += _ => raiseCount++;

        sc.Clamp(10);

        Assert.Equal(0, raiseCount);
        Assert.Equal(0, sc.ScrollOffset);
    }

    // ---------- Reset ----------

    [Fact]
    public void Reset_zeroes_offset_silently_without_event()
    {
        var sc = new ScrollController(ScrollVisibleLines);
        sc.ScrollBy(10, 100);

        int raiseCount = 0;
        sc.ScrollChanged += _ => raiseCount++;

        sc.Reset();

        Assert.Equal(0, sc.ScrollOffset);
        Assert.False(sc.IsScrollMode);
        Assert.Equal(0, raiseCount); // Reset 静默
    }

    [Fact]
    public void Reset_when_already_zero_is_noop()
    {
        var sc = new ScrollController(ScrollVisibleLines);

        sc.Reset();

        Assert.Equal(0, sc.ScrollOffset);
    }

    // ---------- UpdateVisibleLines ----------

    [Fact]
    public void UpdateVisibleLines_changes_maxOffset_for_subsequent_scroll()
    {
        var sc = new ScrollController(ScrollVisibleLines); // visibleLines=23
        // lineCount=50 → maxOffset=27
        sc.ScrollBy(27, 50);
        Assert.Equal(27, sc.ScrollOffset);

        // resize：visibleLines 增到 40 → maxOffset=50-40=10
        sc.UpdateVisibleLines(40);

        // 不自动 clamp——下次 ScrollBy/Clamp 才生效
        Assert.Equal(27, sc.ScrollOffset); // 仍 27（未 clamp）

        // Clamp 触发钳位
        sc.Clamp(50);
        Assert.Equal(10, sc.ScrollOffset);
    }

    // ---------- 边界场景 ----------

    [Fact]
    public void LineCount_less_than_visibleLines_maxOffset_zero()
    {
        var sc = new ScrollController(ScrollVisibleLines);
        // lineCount=10 < visibleLines=23 → maxOffset=0
        int raised = -1;
        sc.ScrollChanged += o => raised = o;

        int newOffset = sc.ScrollBy(5, 10);

        Assert.Equal(0, newOffset); // 钳到 0
        Assert.Equal(-1, raised); // 无变化（0→0）不 raise，raised 保持初始值
        Assert.False(sc.IsScrollMode);
    }

    [Fact]
    public void LineCount_equals_visibleLines_maxOffset_zero()
    {
        var sc = new ScrollController(ScrollVisibleLines);
        // lineCount=23 == visibleLines=23 → maxOffset=0
        int newOffset = sc.ScrollBy(5, 23);

        Assert.Equal(0, newOffset);
        Assert.False(sc.IsScrollMode);
    }

    [Fact]
    public void Multiple_subscribers_all_receive_event()
    {
        var sc = new ScrollController(ScrollVisibleLines);
        int count = 0;
        sc.ScrollChanged += _ => count++;
        sc.ScrollChanged += _ => count++;
        sc.ScrollChanged += _ => count++;

        sc.ScrollBy(5, 100);

        Assert.Equal(3, count);
    }

    [Fact]
    public void Initial_state_is_zero_and_not_scroll_mode()
    {
        var sc = new ScrollController(ScrollVisibleLines);

        Assert.Equal(0, sc.ScrollOffset);
        Assert.False(sc.IsScrollMode);
    }
}
