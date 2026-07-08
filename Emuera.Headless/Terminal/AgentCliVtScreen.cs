using System;

namespace MinorShift.Emuera.GameView
{
    /// <summary>
    /// VT 渲染后端：备用屏幕缓冲区生命周期 + 绝对定位渲染。
    /// 仅在 DA1 探测通过后使用，封装所有 VT 渲染序列。
    /// 坐标全部为 viewport 坐标（0-based），备用屏下 WindowTop 恒为 0。
    /// ADR-0006：持有应用层视口偏移（ScrollOffset），由滚轮/热键改变，
    /// TerminalRenderer.FullRefresh 按偏移渲染更早切片。
    /// </summary>
    internal sealed class AgentCliVtScreen : IDisposable
    {
        private bool _inAltScreen;
        private bool _disposed;

        /// <summary>
        /// 应用层视口偏移（ADR-0006）。0 = 跟随底部（正常模式）；
        /// >0 = 回看历史（Scroll Mode，底部留状态栏）。
        /// </summary>
        internal int ScrollOffset { get; private set; }

        /// <summary>相对偏移滚动 delta 行；正=向上回看历史，负=向下回底。钳到 [0, maxOffset]。返回新 offset。</summary>
        internal int ScrollBy(int delta, int lineCount)
            => SetScrollOffset(ScrollOffset + delta, lineCount);

        /// <summary>设置绝对 offset；钳到 [0, maxOffset]。返回新 offset。</summary>
        internal int ScrollTo(int target, int lineCount)
            => SetScrollOffset(target, lineCount);

        /// <summary>将当前 offset 钳到新 max（resize 后调用）。返回新 offset。</summary>
        internal int ClampScroll(int lineCount)
        {
            if (ScrollOffset <= 0) { ScrollOffset = 0; return 0; }
            int visibleLines = GetScrollVisibleLines();
            int maxOffset = Math.Max(0, lineCount - visibleLines);
            ScrollOffset = Math.Min(ScrollOffset, maxOffset);
            return ScrollOffset;
        }

        /// <summary>归零 offset（auto-follow / ClearOp / ConsumeNeedFullRefresh 调用）。</summary>
        internal void ResetScroll() => ScrollOffset = 0;

        /// <summary>当前 offset 下的可见行数。offset=0 用 consoleHeight-1（无状态栏）；offset>0 用 consoleHeight-2（底部留状态栏）。</summary>
        internal int GetVisibleLines()
            => ScrollOffset > 0 ? GetScrollVisibleLines() : Math.Max(1, WindowHeight - 1);

        /// <summary>Scroll Mode 下的可见行数（offset>0 时使用）。</summary>
        private int GetScrollVisibleLines() => Math.Max(1, WindowHeight - 2);

        private int SetScrollOffset(int target, int lineCount)
        {
            if (target <= 0) { ScrollOffset = 0; return 0; }
            int visibleLines = GetScrollVisibleLines();
            int maxOffset = Math.Max(0, lineCount - visibleLines);
            ScrollOffset = Math.Min(target, maxOffset);
            return ScrollOffset;
        }

        /// <summary>进入备用屏幕缓冲区（ESC[?1049h）。主屏内容暂存，退出后恢复。</summary>
        internal void EnterAlternateScreen()
        {
            TerminalCursor.TryWrite("\x1b[?1049h");
            _inAltScreen = true;
        }

        /// <summary>退出备用屏幕缓冲区（ESC[?1049l）。主屏恢复进入前内容。</summary>
        internal void LeaveAlternateScreen()
        {
            if (!_inAltScreen) return;
            TerminalCursor.TryWrite("\x1b[?1049l");
            _inAltScreen = false;
        }

        /// <summary>全屏清屏并归位光标。</summary>
        internal void ClearScreen()
        {
            TerminalCursor.TryWrite("\x1b[2J\x1b[H");
        }

        /// <summary>绝对定位光标到指定 viewport 坐标。</summary>
        internal void SetCursor(int row, int col)
        {
            TerminalCursor.TryWrite($"\x1b[{row + 1};{col + 1}H");
        }

        /// <summary>清除指定行的全部内容并归位到行首。</summary>
        internal void ClearLine(int row)
        {
            TerminalCursor.TryWrite($"\x1b[{row + 1};1H\x1b[2K");
        }

        /// <summary>从光标当前位置清除到行尾。</summary>
        internal void ClearLineToEnd()
        {
            TerminalCursor.TryWrite("\x1b[K");
        }

        /// <summary>在指定坐标写入文本（不清行尾）。</summary>
        internal void WriteAt(int row, int col, string text)
        {
            SetCursor(row, col);
            TerminalCursor.TryWrite(text);
        }

        /// <summary>在指定行行首写入文本并清除行尾（= SetCursor(row,0) + Write + ClearLineToEnd）。</summary>
        internal void WriteLineAt(int row, string text)
        {
            SetCursor(row, 0);
            TerminalCursor.TryWrite(text);
            TerminalCursor.TryWrite("\x1b[K");
        }

        /// <summary>终端可见列数。</summary>
        internal int WindowWidth => TerminalCursor.TryGetWindowWidth();

        /// <summary>终端可见行数。</summary>
        internal int WindowHeight => TerminalCursor.TryGetWindowHeight();

        /// <summary>获取当前光标所在 viewport 行号。</summary>
        internal int GetCurrentRow()
        {
            try { return Console.CursorTop; }
            catch (Exception) { /* redirected console，默认 0 */ return 0; }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            LeaveAlternateScreen();
        }
    }
}
