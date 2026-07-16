using System;

namespace MinorShift.Emuera.GameView
{
    internal enum RedrawKind
    {
        FullRefresh,
        Flush,
        /// <summary>ChromeOnly：内容区已由调用方（如 TerminalRenderer 的 auto-follow FullRefresh）
        /// 绘制完毕，协调器只同步状态栏/倒计时/按钮等 chrome。不触发 renderer 重绘、不重置倒计时。</summary>
        ChromeOnly,
    }

    internal sealed class CliRedrawCoordinator
    {
        private readonly IRedrawRenderer _renderer;
        private readonly ScrollController _scroll;
        private readonly Func<IScrollStatusBar?> _getStatusBar;
        private readonly ICountdownRenderer _countdown;
        private readonly IButtonSelection _buttons;
        private readonly Func<VtInputHandler?> _getVtInput;
        private bool _pendingScrollRedraw;

        internal CliRedrawCoordinator(
            IRedrawRenderer renderer,
            ScrollController scroll,
            Func<IScrollStatusBar?> getStatusBar,
            ICountdownRenderer countdown,
            IButtonSelection buttons,
            Func<VtInputHandler?> getVtInput)
        {
            _renderer = renderer;
            _scroll = scroll;
            _getStatusBar = getStatusBar;
            _countdown = countdown;
            _buttons = buttons;
            _getVtInput = getVtInput;

            _scroll.ScrollChanged += (_) => _pendingScrollRedraw = true;
        }

        /// <summary>帧末 debounce：消费一帧内累积的滚动变更，仅做一次整屏重绘。
        /// 由 CliGameLoop 在 Flush 之前调用，返回 true 表示已重绘（可跳过本次 Flush）。</summary>
        internal bool PumpPendingScrollRedraw()
        {
            if (!_pendingScrollRedraw) return false;
            _pendingScrollRedraw = false;
            RedrawScrollChanged();
            return true;
        }

        internal void Redraw(RedrawKind kind, bool force)
        {
            switch (kind)
            {
                case RedrawKind.FullRefresh:
                    _renderer.FullRefresh("coordinator");
                    SyncChrome(force, resetCountdown: true);
                    break;
                case RedrawKind.Flush:
                    // no-op 帧（FlushBuffer 返回 false）：内容未变，跳过整轮 chrome 同步。
                    if (!_renderer.FlushBuffer()) return;
                    SyncChrome(force, resetCountdown: false);
                    break;
                case RedrawKind.ChromeOnly:
                    // 内容区已由调用方绘制，此处仅同步 chrome（见 enum 注释）。
                    SyncChrome(force, resetCountdown: false);
                    break;
            }
        }

        /// <summary>滚动 offset 变化后的整屏重绘，由 <see cref="CliGameLoop"/> 在帧末
        /// 对一帧内累积的多次 ScrollBy/ScrollTo 做单次 debounce 调用（替代原先每档一次 FullRefresh）。</summary>
        internal void RedrawScrollChanged()
        {
            _renderer.FullRefresh("scroll");
            SyncChrome(force: true, resetCountdown: true);
        }

        private void SyncChrome(bool force, bool resetCountdown)
        {
            _getStatusBar()?.Render(_scroll.ScrollOffset);
            if (resetCountdown) _countdown.Reset();
            _buttons.SyncButtonState();
            _buttons.RefreshButtonRegionsFromSnapshot(_getVtInput()!, force);
        }
    }
}
