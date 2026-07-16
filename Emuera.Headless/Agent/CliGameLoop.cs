using System;
using System.Threading;
using MinorShift.Emuera.Terminal;

namespace MinorShift.Emuera.GameView;

internal enum RunState
{
    Running,
    GameExited,
}

internal sealed class CliGameLoop
{
    private const int PollIntervalMs = 50;

    private readonly IConsoleStateView _execState;
    private readonly IInputTimer _inputTimer;
    private readonly IWindowSize _windowSize;
    private readonly ScrollController _scroll;
    private readonly ScrollStatusBarRenderer? _scrollStatusBar;
    private readonly CliRedrawCoordinator _redraw;
    private readonly VtInputHandler _vtInput;
    private readonly ICountdownRenderer _countdown;
    private readonly Func<DateTime> _now;

    private DateTime? _waitInputEnteredAt;
    private int _lastWindowWidth = -1;
    private int _lastWindowHeight = -1;

    internal CliGameLoop(
        IConsoleStateView execState,
        IInputTimer inputTimer,
        IWindowSize windowSize,
        ScrollController scroll,
        ScrollStatusBarRenderer? scrollStatusBar,
        CliRedrawCoordinator redraw,
        VtInputHandler vtInput,
        ICountdownRenderer countdown,
        int initialWindowWidth = -1,
        int initialWindowHeight = -1,
        Func<DateTime>? nowProvider = null)
    {
        _execState = execState;
        _inputTimer = inputTimer;
        _windowSize = windowSize;
        _scroll = scroll;
        _scrollStatusBar = scrollStatusBar;
        _redraw = redraw;
        _vtInput = vtInput;
        _countdown = countdown;
        _now = nowProvider ?? (() => DateTime.UtcNow);
        _lastWindowWidth = initialWindowWidth;
        _lastWindowHeight = initialWindowHeight;
    }

    internal void RunLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            if (RunOneFrame(token) == RunState.GameExited)
                break;
        }
    }

    internal RunState RunOneFrame(CancellationToken token)
    {
        if (_execState.ConsumeNeedFullRefresh())
        {
            ResetScrollIfActive();
            _waitInputEnteredAt = null;
            _redraw.Redraw(RedrawKind.FullRefresh, true);
            return RunState.Running;
        }

        if (!_inputTimer.IsWaitingInput)
        {
            _waitInputEnteredAt = null;
        }
        else if (!_waitInputEnteredAt.HasValue && _inputTimer.InputTimelimit > 0)
        {
            _waitInputEnteredAt = _now();
        }

        if (HandleTimeout())
            return RunState.Running;

        if (_vtInput.HasInputAvailable())
        {
            int b = _vtInput.ReadByte();
            if (b >= 0)
            {
                _vtInput.Feed((byte)b);
            }
        }
        else
        {
            long elapsedMs = _waitInputEnteredAt.HasValue
                ? (long)(_now() - _waitInputEnteredAt.Value).TotalMilliseconds
                : 0;
            _countdown.Update(elapsedMs);
            token.WaitHandle.WaitOne(PollIntervalMs);
        }

        if (CheckResize())
        {
            _scroll.UpdateVisibleLines(Math.Max(1, _windowSize.WindowHeight - 2));
            int oldOffset = _scroll.ScrollOffset;
            _scroll.Clamp(_execState.DisplayLineCount);
            _waitInputEnteredAt = null;
            if (oldOffset == _scroll.ScrollOffset)
            {
                _redraw.Redraw(RedrawKind.FullRefresh, true);
            }
        }

        _redraw.Redraw(RedrawKind.Flush, false);

        return _execState.IsGameExited ? RunState.GameExited : RunState.Running;
    }

    private bool HandleTimeout()
    {
        if (!_waitInputEnteredAt.HasValue) return false;
        long timelimit = _inputTimer.InputTimelimit;
        if (timelimit <= 0) return false;
        var elapsed = (long)(_now() - _waitInputEnteredAt.Value).TotalMilliseconds;
        if (elapsed < timelimit) return false;

        _countdown.Overwrite(_inputTimer.TimeUpMessage ?? "");
        _countdown.Reset();
        _inputTimer.ClearInputBuffer();
        _inputTimer.SubmitTimeout();
        _waitInputEnteredAt = null;
        _redraw.Redraw(RedrawKind.Flush, false);
        return true;
    }

    private void ResetScrollIfActive()
    {
        if (_scroll.ScrollOffset > 0)
        {
            _scrollStatusBar?.ClearStatusBar();
            _scroll.Reset();
        }
    }

    private bool CheckResize()
    {
        int w = _windowSize.WindowWidth;
        int h = _windowSize.WindowHeight;
        if (w == _lastWindowWidth && h == _lastWindowHeight) return false;
        _lastWindowWidth = w;
        _lastWindowHeight = h;
        return true;
    }
}
