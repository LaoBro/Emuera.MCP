using System;
using System.Collections.Generic;
using MinorShift.Emuera.GameView;
using MinorShift.Emuera.Terminal.Platform;
using Xunit;

namespace Emuera.Headless.Tests;

/// <summary>
/// CliRedrawCoordinator 编排单元测试（架构审视 B / plan step 5）。
/// 协调器经窄接口 <see cref="IRedrawRenderer"/> / <see cref="ICountdownRenderer"/> /
/// <see cref="IScrollStatusBar"/> / <see cref="IButtonSelection"/> 触达四个重绘协作者，
/// 本测试用 recording fake 替换，断言：
///   - 不同 <see cref="RedrawKind"/> 路由到不同的渲染调用（FullRefresh / FlushBuffer / 无）
///   - statusbar.Render 每轮都调用
///   - countdown.Reset 仅 FullRefresh 调用
///   - buttons.SyncButtonState + RefreshButtonRegionsFromSnapshot 每轮都调用，force 随 kind
///   - ScrollController.ScrollChanged 自订阅 → 触发 FullRefresh + force=true
///   - statusbar 为 null 时安全跳过（?. 守卫）
/// 不依赖真实 VT 屏幕 / EmueraConsole。
/// </summary>
public class CliRedrawCoordinatorTests
{
    private sealed class FakeRenderer : IRedrawRenderer
    {
        private readonly Action<string> _log;
        public int FullRefreshCalls;
        public string? LastReason;
        public int FlushCalls;
        public FakeRenderer(Action<string> log) => _log = log;
        public void FullRefresh(string reason = "?")
        {
            FullRefreshCalls++;
            LastReason = reason;
            _log("renderer.FullRefresh");
        }
        public void FlushBuffer()
        {
            FlushCalls++;
            _log("renderer.FlushBuffer");
        }
    }

    private sealed class FakeCountdown : ICountdownRenderer
    {
        private readonly Action<string> _log;
        public int ResetCalls;
        public FakeCountdown(Action<string> log) => _log = log;
        public void Reset()
        {
            ResetCalls++;
            _log("countdown.Reset");
        }
    }

    private sealed class FakeStatusBar : IScrollStatusBar
    {
        private readonly Action<string> _log;
        public int RenderCalls;
        public int LastOffset;
        public FakeStatusBar(Action<string> log) => _log = log;
        public void Render(int offset)
        {
            RenderCalls++;
            LastOffset = offset;
            _log("statusBar.Render");
        }
    }

    private sealed class FakeButtonSelection : IButtonSelection
    {
        private readonly Action<string> _log;
        public int SyncCalls;
        public int RefreshCalls;
        public bool LastForce;
        public VtInputHandler? LastVtInput;
        public FakeButtonSelection(Action<string> log) => _log = log;
        public void SyncButtonState()
        {
            SyncCalls++;
            _log("buttons.Sync");
        }
        public void RefreshButtonRegionsFromSnapshot(VtInputHandler vtInput, bool force = false)
        {
            RefreshCalls++;
            LastForce = force;
            LastVtInput = vtInput;
            _log("buttons.Refresh");
        }
    }

    private sealed class Harness
    {
        public List<string> Log { get; } = new();
        public FakeRenderer Renderer;
        public FakeCountdown Countdown;
        public FakeStatusBar StatusBar;
        public FakeButtonSelection Buttons;
        public ScrollController Scroll;
        public CliRedrawCoordinator Coordinator;

        public Harness(Func<IScrollStatusBar?>? statusBarFactory = null, Func<VtInputHandler?>? vtInputFactory = null)
        {
            Renderer = new FakeRenderer(Log.Add);
            Countdown = new FakeCountdown(Log.Add);
            StatusBar = new FakeStatusBar(Log.Add);
            Buttons = new FakeButtonSelection(Log.Add);
            Scroll = new ScrollController(1);
            Coordinator = new CliRedrawCoordinator(
                Renderer,
                Scroll,
                statusBarFactory ?? (() => StatusBar),
                Countdown,
                Buttons,
                vtInputFactory ?? (() => null));
        }
    }

    [Fact]
    public void FullRefresh_routes_content_render_and_resets_countdown_and_forces_buttons()
    {
        var h = new Harness();

        h.Coordinator.Redraw(RedrawKind.FullRefresh, force: true);

        Assert.Equal(1, h.Renderer.FullRefreshCalls);
        Assert.Equal("coordinator", h.Renderer.LastReason);
        Assert.Equal(0, h.Renderer.FlushCalls);
        Assert.Equal(1, h.StatusBar.RenderCalls);
        Assert.Equal(1, h.Countdown.ResetCalls);
        Assert.Equal(1, h.Buttons.SyncCalls);
        Assert.Equal(1, h.Buttons.RefreshCalls);
        Assert.True(h.Buttons.LastForce);
    }

    [Fact]
    public void FullRefresh_orchestration_order_is_content_then_chrome_then_countdown_then_buttons()
    {
        var h = new Harness();

        h.Coordinator.Redraw(RedrawKind.FullRefresh, force: true);

        Assert.Equal(new[]
        {
            "renderer.FullRefresh",
            "statusBar.Render",
            "countdown.Reset",
            "buttons.Sync",
            "buttons.Refresh",
        }, h.Log);
    }

    [Fact]
    public void Flush_routes_flushbuffer_and_does_not_reset_countdown_and_passes_force_false()
    {
        var h = new Harness();

        h.Coordinator.Redraw(RedrawKind.Flush, force: false);

        Assert.Equal(1, h.Renderer.FlushCalls);
        Assert.Equal(0, h.Renderer.FullRefreshCalls);
        Assert.Equal(1, h.StatusBar.RenderCalls);
        Assert.Equal(0, h.Countdown.ResetCalls);
        Assert.Equal(1, h.Buttons.SyncCalls);
        Assert.Equal(1, h.Buttons.RefreshCalls);
        Assert.False(h.Buttons.LastForce);
    }

    [Fact]
    public void ChromeOnly_renders_no_content_and_does_not_reset_countdown()
    {
        var h = new Harness();

        h.Coordinator.Redraw(RedrawKind.ChromeOnly, force: false);

        Assert.Equal(0, h.Renderer.FullRefreshCalls);
        Assert.Equal(0, h.Renderer.FlushCalls);
        Assert.Equal(1, h.StatusBar.RenderCalls);
        Assert.Equal(0, h.Countdown.ResetCalls);
        Assert.Equal(1, h.Buttons.SyncCalls);
        Assert.Equal(1, h.Buttons.RefreshCalls);
    }

    [Fact]
    public void ScrollChanged_event_triggers_FullRefresh_with_force_true()
    {
        var h = new Harness();

        // offset 变化 raise ScrollChanged → 协调器自订阅触发 Redraw(FullRefresh, true)
        h.Scroll.ScrollBy(1, lineCount: 10);

        Assert.Equal(1, h.Scroll.ScrollOffset);
        Assert.Equal(1, h.Renderer.FullRefreshCalls);
        Assert.Equal(1, h.Countdown.ResetCalls);
        Assert.Equal(1, h.Buttons.RefreshCalls);
        Assert.True(h.Buttons.LastForce);
    }

    [Fact]
    public void Null_status_bar_is_safely_skipped()
    {
        var h = new Harness(statusBarFactory: () => null);

        // 不应抛 NRE；其余协作者仍按 FullRefresh 编排执行
        h.Coordinator.Redraw(RedrawKind.FullRefresh, force: true);

        Assert.Equal(0, h.StatusBar.RenderCalls); // 工厂返回 null，本实例未被调用
        Assert.Equal(1, h.Renderer.FullRefreshCalls);
        Assert.Equal(1, h.Countdown.ResetCalls);
        Assert.Equal(1, h.Buttons.SyncCalls);
    }
}
