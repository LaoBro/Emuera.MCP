using System;
using MinorShift.Emuera.Runtime;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.UI.Game;

namespace MinorShift.Emuera.GameView;

/// <summary>
/// Refresh and render control: RefreshStrings, VerticalScrollBarUpdate, SetRedraw.
/// In headless mode, OnPaint is a no-op; RefreshStrings handles state checks and
/// delegates to _uiAdapter.Refresh().
/// </summary>
internal sealed class ConsoleRefreshHandler
{
    private readonly ConsoleStateData _state;
    private readonly EmueraConsole _console;
    private readonly IConsoleUI _ui;

    internal ConsoleRefreshHandler(ConsoleStateData state, EmueraConsole console, IConsoleUI ui)
    {
        _state = state;
        _console = console;
        _ui = ui;
    }

    internal void RefreshStrings(bool force_Paint)
    {
        bool isBackLog = _ui.ScrollBar.Value != _ui.ScrollBar.Maximum;
        if ((_state.redraw == ConsoleRedraw.None) && (!force_Paint) && (!isBackLog))
            return;

        if (_state.selectingButton != null)
        {
            if (_state.State != ConsoleState.Error && _state.State != ConsoleState.WaitInput)
                _state.selectingButton = null;
            else if ((_state.State == ConsoleState.WaitInput) && !_state.inputReq!.NeedValue)
                _state.selectingButton = null;
            else if (_state.selectingButton.Generation != _state.lastButtonGeneration)
                _state.selectingButton = null;
        }
        if (!force_Paint)
        {
            if ((!isBackLog) && (_state.lastDrawnLineNo == _state.lineNo) && (_state.lastSelectingButton == _state.selectingButton))
                return;
            if (_state._frameDeltaTimer.ElapsedMilliseconds < _state.msPerFrame && (_state.State == ConsoleState.Running || _state.State == ConsoleState.Initializing))
                return;
        }
        if (_state.forceTextBoxColor)
        {
            _state.WaitFrameSyncIfEnabled(_ui);
            _ui.TextBox.BackColor = _state.bgColor;
            // WaitFrameSyncIfEnabled 已保证 _drawStopwatch 非 null（内部 StartNew 兜底）
            _state._drawStopwatch!.Restart();
        }

        // HEADLESS: handle need_settimer transition
        if (_state.need_settimer)
        {
            _state.need_settimer = false;
            _console._timer.SetTimer();
        }

        _ui.Invoke(() =>
        {
            VerticalScrollBarUpdate();
            _ui.Refresh();
        });

        if (isBackLog)
            _state.lastDrawnLineNo = -1;
        else
            _state.lastDrawnLineNo = _state.lineNo;
        _state.lastSelectingButton = _state.selectingButton;
        _state.forceTextBoxColor = false;
    }

    internal void SetRedraw(long i)
    {
        if ((i & 1) == 0)
            _state.redraw = ConsoleRedraw.None;
        else
            _state.redraw = ConsoleRedraw.Normal;
        if ((i & 2) != 0)
            RefreshStrings(true);
    }

    internal void VerticalScrollBarUpdate()
    {
        int max = _state.displayLineList.Count;
        int move = max - _ui.ScrollBar.Maximum;
        if (move == 0)
            return;
        _ui.TextBoxIgnoreScrollBarChanges = true;
        if (move > 0)
        {
            _ui.ScrollBar.Maximum = max;
            _ui.ScrollBar.Value += move;
        }
        else
        {
            if (max > _ui.ScrollBar.Value)
                _ui.ScrollBar.Value = max;
            _ui.ScrollBar.Maximum = max;
        }
        _ui.ScrollBar.Enabled = max > 0;
        _ui.TextBoxIgnoreScrollBarChanges = false;
    }
}
