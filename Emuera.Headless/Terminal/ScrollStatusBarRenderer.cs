using System;

namespace MinorShift.Emuera.GameView
{
    /// <summary>
    /// Scroll Mode 状态栏渲染（ADR-0006）。
    /// offset>0 时在视口底部（consoleHeight-2 行，0-indexed）写入
    /// `[scroll -N] End=resume PgUp/PgDn`，并隐藏光标（ESC[?25l）。
    /// offset=0 时清空该行并恢复光标（ESC[?25h）。
    /// 独立于 TerminalRenderer，由 AgentCliProtocol 在 offset 变化时显式调用。
    /// </summary>
    internal sealed class ScrollStatusBarRenderer
    {
        private readonly Func<AgentCliVtScreen?> _getScreen;
        private bool _cursorHidden;

        public ScrollStatusBarRenderer(Func<AgentCliVtScreen?> getScreen)
        {
            _getScreen = getScreen;
        }

        /// <summary>按当前 offset 渲染状态栏。offset>0 显示状态栏并隐藏光标；offset=0 清行并恢复光标。</summary>
        internal void Render(int offset)
        {
            var screen = _getScreen();
            if (screen == null) return;

            int consoleHeight = screen.WindowHeight;
            // 状态栏行：consoleHeight-2（0-indexed）。内容占 rows[0..visibleLines-1]，
            // visibleLines=consoleHeight-2，状态栏紧贴内容下方；最后一行（consoleHeight-1）为光标位置。
            int statusBarRow = Math.Max(0, consoleHeight - 2);

            if (offset > 0)
            {
                screen.WriteLineAt(statusBarRow, $"[scroll -{offset}] End=resume PgUp/PgDn");
                if (!_cursorHidden)
                {
                    TerminalCursor.TryWrite("\x1b[?25l");
                    _cursorHidden = true;
                }
            }
            else
            {
                screen.ClearLine(statusBarRow);
                if (_cursorHidden)
                {
                    TerminalCursor.TryWrite("\x1b[?25h");
                    _cursorHidden = false;
                }
            }
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
