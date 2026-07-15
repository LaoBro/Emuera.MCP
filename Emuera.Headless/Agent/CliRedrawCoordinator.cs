using System;

namespace MinorShift.Emuera.GameView
{
    internal enum RedrawKind
    {
        FullRefresh,
        Flush,
        ChromeOnly,
    }

    internal sealed class CliRedrawCoordinator
    {
        private readonly TerminalRenderer _renderer;
        private readonly ScrollController _scroll;
        private readonly Func<ScrollStatusBarRenderer?> _getStatusBar;
        private readonly CountdownRenderer _countdown;
        private readonly ButtonSelectionMode _buttons;
        private readonly Func<VtInputHandler?> _getVtInput;

        internal CliRedrawCoordinator(
            TerminalRenderer renderer,
            ScrollController scroll,
            Func<ScrollStatusBarRenderer?> getStatusBar,
            CountdownRenderer countdown,
            ButtonSelectionMode buttons,
            Func<VtInputHandler?> getVtInput)
        {
            _renderer = renderer;
            _scroll = scroll;
            _getStatusBar = getStatusBar;
            _countdown = countdown;
            _buttons = buttons;
            _getVtInput = getVtInput;

            _scroll.ScrollChanged += (_) => Redraw(RedrawKind.FullRefresh, true);
        }

        internal void Redraw(RedrawKind kind, bool force)
        {
            switch (kind)
            {
                case RedrawKind.FullRefresh:
                    _renderer.FullRefresh("coordinator");
                    break;
                case RedrawKind.Flush:
                    _renderer.FlushBuffer();
                    break;
                case RedrawKind.ChromeOnly:
                    break;
            }

            _getStatusBar()?.Render(_scroll.ScrollOffset);

            if (kind == RedrawKind.FullRefresh)
                _countdown.Reset();

            _buttons.SyncButtonState();
            _buttons.RefreshButtonRegionsFromSnapshot(_getVtInput()!, force);
        }
    }
}
