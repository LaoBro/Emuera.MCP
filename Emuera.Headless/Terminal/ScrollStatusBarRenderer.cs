using System;

namespace MinorShift.Emuera.GameView
{
    /// <summary>
    /// Scroll Mode 状态栏渲染（ADR-0006）。
    /// offset>0 时在视口底部（consoleHeight-2 行，0-indexed）写入
    /// `[scroll -N] End=resume PgUp/PgDn`，并隐藏光标（ESC[?25l）。
    /// offset=0 时仅恢复光标（ESC[?25h），不清行——offset=0 布局下
    /// row consoleHeight-2 是最后一行内容所在行，ClearLine 会擦掉内容。
    /// 旧状态栏清理由 <see cref="ClearStatusBar"/> 在 offset 归零前显式调用。
    /// 独立于 TerminalRenderer，由 AgentCliProtocol 在 offset 变化时显式调用。
    /// </summary>
    internal sealed class ScrollStatusBarRenderer : IScrollStatusBar
    {
        private readonly Func<AgentCliVtScreen?> _getScreen;
        private bool _cursorHidden;

        public ScrollStatusBarRenderer(Func<AgentCliVtScreen?> getScreen)
        {
            _getScreen = getScreen;
        }

        /// <summary>按当前 offset 渲染状态栏。offset>0 显示状态栏并隐藏光标；offset=0 仅恢复光标（不清行）。</summary>
        public void Render(int offset)
        {
            var screen = _getScreen();
            if (screen == null) return;

            if (offset > 0)
            {
                // 状态栏行：consoleHeight-2（0-indexed）。内容占 rows[0..visibleLines-1]，
                // visibleLines=consoleHeight-2，状态栏紧贴内容下方；最后一行（consoleHeight-1）为光标位置。
                int statusBarRow = Math.Max(0, screen.WindowHeight - 2);
                screen.WriteLineAt(statusBarRow, $"[scroll -{offset}] End=resume PgUp/PgDn");
                if (!_cursorHidden)
                {
                    TerminalCursor.TryWrite("\x1b[?25l");
                    _cursorHidden = true;
                }
            }
            else
            {
                // offset=0 布局下 visibleLines=consoleHeight-1，row consoleHeight-2 是最后一行内容，
                // 不能清行。仅恢复光标（若之前处于 Scroll Mode 隐藏过）。
                RestoreCursor();
            }
        }

        /// <summary>清旧状态栏行并恢复光标。仅在屏幕仍处于 offset>0 旧布局、offset 归零前调用。</summary>
        internal void ClearStatusBar()
        {
            var screen = _getScreen();
            if (screen == null) return;
            int statusBarRow = Math.Max(0, screen.WindowHeight - 2);
            screen.ClearLine(statusBarRow);
            RestoreCursor();
        }

        /// <summary>强制恢复光标（VT cleanup 时调用，确保退出备用屏前光标可见）。</summary>
        internal void RestoreCursor()
        {
            if (_cursorHidden)
            {
                TerminalCursor.TryWrite("\x1b[?25h");
                _cursorHidden = false;
            }
        }
    }
}
