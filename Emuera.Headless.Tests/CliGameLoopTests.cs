using System;
using System.Threading;
using MinorShift.Emuera.GameView;
using MinorShift.Emuera.Terminal;
using MinorShift.Emuera.Terminal.Platform;
using Xunit;

namespace Emuera.Headless.Tests;

public class CliGameLoopTests
{
    private sealed class FakeConsoleStateView : IConsoleStateView
    {
        public bool NeedFullRefresh;
        public bool IsGameExitedValue;
        public int DisplayLineCountValue;
        bool IConsoleStateView.IsGameExited => IsGameExitedValue;
        int IConsoleStateView.DisplayLineCount => DisplayLineCountValue;
        public bool ConsumeNeedFullRefresh()
        {
            bool v = NeedFullRefresh;
            NeedFullRefresh = false;
            return v;
        }
    }

    private sealed class FakeInputTimer : IInputTimer
    {
        public long InputTimelimitValue;
        public bool IsWaitingInputValue;
        public string? TimeUpMessageValue;
        public int SubmitTimeoutCalls;
        public int ClearInputBufferCalls;
        long IInputTimer.InputTimelimit => InputTimelimitValue;
        bool IInputTimer.IsWaitingInput => IsWaitingInputValue;
        string? IInputTimer.TimeUpMessage => TimeUpMessageValue;
        public void SubmitTimeout() => SubmitTimeoutCalls++;
        public void ClearInputBuffer() => ClearInputBufferCalls++;
    }

    private sealed class FakeWindowSize : IWindowSize
    {
        public int Width = 80;
        public int Height = 25;
        int IWindowSize.WindowWidth => Width;
        int IWindowSize.WindowHeight => Height;
    }

    private sealed class FakeRedrawRenderer : IRedrawRenderer
    {
        public int FullRefreshCalls;
        public string? LastReason;
        public int FlushBufferCalls;
        public void FullRefresh(string reason = "?") { FullRefreshCalls++; LastReason = reason; }
        public void FlushBuffer() => FlushBufferCalls++;
    }

    private sealed class FakeCountdownRenderer : ICountdownRenderer
    {
        public int UpdateCalls;
        public long LastElapsedMs;
        public int OverwriteCalls;
        public string? LastOverwriteText;
        public int ResetCalls;
        public void Update(long elapsedMs) { UpdateCalls++; LastElapsedMs = elapsedMs; }
        public void Overwrite(string text) { OverwriteCalls++; LastOverwriteText = text; }
        public void Reset() => ResetCalls++;
    }

    private sealed class FakeButtonSelection : IButtonSelection
    {
        public int SyncCalls;
        public int RefreshCalls;
        public void SyncButtonState() => SyncCalls++;
        public void RefreshButtonRegionsFromSnapshot(VtInputHandler vtInput, bool force = false) => RefreshCalls++;
    }

    private sealed class FakeTerminalInput : ITerminalInput
    {
        public int ReadByteResult = -1;
        public bool HasInput;
        public bool HasInputAvailable() => HasInput;
        public int ReadByte() => ReadByteResult;
        public void EnableSgrMouse() { }
        public void DisableSgrMouse() { }
        public void Dispose() { }
    }

    private sealed class FakeVtHost : IVtHost
    {
        public void RequestExit() { }
        public bool IsWaitingPrimitive => false;
        public int ScrollOffset => 0;
        public void DispatchWheel(int delta) { }
        public void DispatchScroll(ScrollAction action) { }
        public void ProcessKeyFromVt(ConsoleKeyInfo key) { }
        public void DispatchMouseClick(object value, bool isInteger) { }
        public void DispatchMouseMiss() { }
        public void PressPrimitiveKey(int keycode, int keydata, int keymod) { }
        public void InputMouseKey(int type, int result1, int result2, int result3, int result4, long result5) { }
    }

    private sealed class FakeClock
    {
        public DateTime Base = DateTime.UtcNow;
        private int _callCount;
        public DateTime Provider()
        {
            _callCount++;
            return _callCount == 1 ? Base : Base.AddMilliseconds(200);
        }
    }

    private sealed class Harness
    {
        public readonly FakeConsoleStateView ExecState = new();
        public readonly FakeInputTimer InputTimer = new();
        public readonly FakeWindowSize WindowSize = new();
        public readonly FakeRedrawRenderer Renderer = new();
        public readonly FakeCountdownRenderer Countdown = new();
        public readonly FakeButtonSelection Buttons = new();
        public readonly FakeTerminalInput TerminalInput = new();
        public readonly FakeVtHost VtHost = new();
        public readonly FakeClock Clock = new();
        public readonly ScrollController Scroll = new(1);
        public readonly VtInputHandler VtInput;
        public readonly CliRedrawCoordinator RedrawCoordinator;
        public readonly CliGameLoop GameLoop;
        public readonly CancellationTokenSource Cts = new();

        public Harness()
        {
            VtInput = new VtInputHandler(VtHost, TerminalInput);
            RedrawCoordinator = new CliRedrawCoordinator(
                Renderer, Scroll, () => null, Countdown, Buttons, () => VtInput);
            GameLoop = new CliGameLoop(
                ExecState, InputTimer, WindowSize, Scroll, null,
                RedrawCoordinator, VtInput, Countdown,
                initialWindowWidth: 80, initialWindowHeight: 25,
                nowProvider: Clock.Provider);
        }
    }

    [Fact]
    public void FullRefresh_resets_scroll_and_triggers_FullRefresh_redraw()
    {
        var h = new Harness();
        h.ExecState.NeedFullRefresh = true;

        var state = h.GameLoop.RunOneFrame(h.Cts.Token);

        Assert.Equal(RunState.Running, state);
        Assert.Equal(1, h.Renderer.FullRefreshCalls);
        Assert.Equal("coordinator", h.Renderer.LastReason);
        Assert.Equal(0, h.Renderer.FlushBufferCalls);
    }

    [Fact]
    public void FullRefresh_resets_scroll_when_active()
    {
        var h = new Harness();
        h.Scroll.ScrollBy(5, 100);
        h.ExecState.NeedFullRefresh = true;

        h.GameLoop.RunOneFrame(h.Cts.Token);

        Assert.Equal(0, h.Scroll.ScrollOffset);
    }

    [Fact]
    public void FullRefresh_clears_waitInputEnteredAt()
    {
        var h = new Harness();
        h.InputTimer.IsWaitingInputValue = true;
        h.InputTimer.InputTimelimitValue = 5000;
        h.ExecState.NeedFullRefresh = true;

        h.GameLoop.RunOneFrame(h.Cts.Token);

        Assert.Equal(0, h.Countdown.OverwriteCalls);
        Assert.Equal(0, h.InputTimer.SubmitTimeoutCalls);
    }

    [Fact]
    public void FullRefresh_skips_input_and_countdown()
    {
        var h = new Harness();
        h.TerminalInput.HasInput = true;
        h.ExecState.NeedFullRefresh = true;

        h.GameLoop.RunOneFrame(h.Cts.Token);

        Assert.Equal(0, h.Countdown.UpdateCalls);
    }

    [Fact]
    public void Timeout_submits_timeup_message_and_clears_buffer()
    {
        var h = new Harness();
        h.InputTimer.IsWaitingInputValue = true;
        h.InputTimer.InputTimelimitValue = 100;
        h.InputTimer.TimeUpMessageValue = "time up!";

        h.GameLoop.RunOneFrame(h.Cts.Token);

        Assert.Equal(1, h.Countdown.OverwriteCalls);
        Assert.Equal("time up!", h.Countdown.LastOverwriteText);
        Assert.Equal(1, h.Countdown.ResetCalls);
        Assert.Equal(1, h.InputTimer.ClearInputBufferCalls);
        Assert.Equal(1, h.InputTimer.SubmitTimeoutCalls);
    }

    [Fact]
    public void Timeout_redraws_on_flush()
    {
        var h = new Harness();
        h.InputTimer.IsWaitingInputValue = true;
        h.InputTimer.InputTimelimitValue = 100;

        h.GameLoop.RunOneFrame(h.Cts.Token);

        Assert.Equal(1, h.Renderer.FlushBufferCalls);
    }

    [Fact]
    public void Time_not_yet_expired_does_not_trigger_timeout()
    {
        var h = new Harness();
        h.InputTimer.IsWaitingInputValue = true;
        h.InputTimer.InputTimelimitValue = 1_000_000;

        h.GameLoop.RunOneFrame(h.Cts.Token);

        Assert.Equal(0, h.Countdown.OverwriteCalls);
        Assert.Equal(0, h.InputTimer.SubmitTimeoutCalls);
    }

    [Fact]
    public void Not_waiting_input_clears_waitInputEnteredAt()
    {
        var h = new Harness();
        h.InputTimer.IsWaitingInputValue = false;

        h.GameLoop.RunOneFrame(h.Cts.Token);

        Assert.Equal(1, h.Countdown.UpdateCalls);
        Assert.Equal(0, h.Countdown.OverwriteCalls);
    }

    [Fact]
    public void Input_available_reads_byte_and_feeds()
    {
        var h = new Harness();
        h.TerminalInput.HasInput = true;
        h.TerminalInput.ReadByteResult = 0x61;

        h.GameLoop.RunOneFrame(h.Cts.Token);

        Assert.Equal(0, h.Countdown.UpdateCalls);
    }

    [Fact]
    public void No_input_updates_countdown()
    {
        var h = new Harness();
        h.InputTimer.IsWaitingInputValue = true;
        h.InputTimer.InputTimelimitValue = 5000;
        h.TerminalInput.HasInput = false;

        h.GameLoop.RunOneFrame(h.Cts.Token);

        Assert.Equal(1, h.Countdown.UpdateCalls);
    }

    [Fact]
    public void Resize_detected_triggers_FullRefresh_when_offset_unchanged()
    {
        var h = new Harness();
        h.WindowSize.Width = 120;

        h.GameLoop.RunOneFrame(h.Cts.Token);

        Assert.Equal(1, h.Renderer.FullRefreshCalls);
        Assert.Equal("coordinator", h.Renderer.LastReason);
    }

    [Fact]
    public void Resize_updates_visible_lines_and_clamps_offset()
    {
        var h = new Harness();
        h.Scroll.ScrollBy(60, 100);
        h.WindowSize.Height = 40;
        h.ExecState.DisplayLineCountValue = 100;

        h.GameLoop.RunOneFrame(h.Cts.Token);

        Assert.Equal(60, h.Scroll.ScrollOffset);
    }

    [Fact]
    public void Resize_does_not_extra_redraw_when_offset_clamped()
    {
        var h = new Harness();
        h.Scroll.ScrollBy(60, 100);
        h.WindowSize.Height = 40;
        h.ExecState.DisplayLineCountValue = 10;

        h.GameLoop.RunOneFrame(h.Cts.Token);

        Assert.Equal(0, h.Scroll.ScrollOffset);
    }

    [Fact]
    public void Resize_detected_clears_waitInputEnteredAt()
    {
        var h = new Harness();
        h.InputTimer.IsWaitingInputValue = true;
        h.InputTimer.InputTimelimitValue = 1_000_000;
        h.WindowSize.Width = 120;

        h.GameLoop.RunOneFrame(h.Cts.Token);

        Assert.Equal(0, h.InputTimer.SubmitTimeoutCalls);
    }

    [Fact]
    public void GameExited_returns_GameExited()
    {
        var h = new Harness();
        h.ExecState.IsGameExitedValue = true;

        var state = h.GameLoop.RunOneFrame(h.Cts.Token);

        Assert.Equal(RunState.GameExited, state);
    }

    [Fact]
    public void RunLoop_returns_when_GameExited()
    {
        var h = new Harness();
        h.ExecState.IsGameExitedValue = true;

        h.GameLoop.RunLoop(h.Cts.Token);

        Assert.Equal(1, h.Renderer.FlushBufferCalls);
    }

    [Fact]
    public void RunLoop_stops_on_cancellation()
    {
        var h = new Harness();
        h.Cts.Cancel();

        h.GameLoop.RunLoop(h.Cts.Token);

        Assert.Equal(0, h.Renderer.FlushBufferCalls);
    }
}
