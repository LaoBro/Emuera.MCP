using System;
using MinorShift.Emuera.UI.Game;

namespace MinorShift.Emuera.GameView
{
    /// <summary>
    /// 终端输出渲染：刷新屏幕、擦除行、刷新缓冲区。
    /// 支持 VT 备用屏绝对定位与非 VT 自然滚动两种模式。
    /// 从 AgentCliProtocol 拆分以隔离终端输出逻辑。
    /// </summary>
    internal sealed class TerminalRenderer
    {
        private readonly EmueraConsole _console;
        private readonly Func<AgentCliVtScreen?> _getScreen;
        private readonly TerminalCursor _cursor;

        public TerminalRenderer(
            EmueraConsole console,
            Func<AgentCliVtScreen?> getScreen,
            TerminalCursor cursor)
        {
            _console = console;
            _getScreen = getScreen;
            _cursor = cursor;
        }

        /// <summary>擦除待清理行并输出 agent 缓冲区内容。</summary>
        internal void FlushBuffer()
        {
            EraseTerminalRows();
            string text = _console.TakeAgentBuffer();
            if (text.Length > 0)
                Console.Write(text);
        }

        /// <summary>全量重绘可见行。VT 模式用备用屏绝对定位，非 VT 模式用自然滚动。</summary>
        internal void FullRefresh()
        {
            var lines = _console.DisplayLineList;
            var screen = _getScreen();

            if (screen != null)
            {
                // VT 模式：备用屏绝对定位重绘，不依赖自然滚动
                screen.ClearScreen();
                if (lines.Count == 0) return;

                int consoleHeight = screen.WindowHeight;
                int visibleLines = Math.Max(consoleHeight - 1, 1);
                int startLine = Math.Max(0, lines.Count - visibleLines);

                for (int i = 0; i < visibleLines && (startLine + i) < lines.Count; i++)
                {
                    int lineIndex = startLine + i;
                    int viewportRow = i;
                    string formatted = _console.FormatLineForTerminal(lines[lineIndex]);
                    screen.WriteLineAt(viewportRow, formatted.Length > 0 ? formatted : "");
                }

                // 光标定位到实际内容末尾的下一行行首，作为输入回显行
                int drawnRows = Math.Min(visibleLines, lines.Count - startLine);
                screen.SetCursor(drawnRows, 0);
                return;
            }

            // 非 VT 模式：Console.WriteLine 自然滚动
            _cursor.ClearScreen();
            if (lines.Count == 0) return;

            int height = TerminalCursor.TryGetWindowHeight();
            int visLines = Math.Max(height - 1, 1);
            int startLn = Math.Max(0, lines.Count - visLines);

            for (int i = startLn; i < lines.Count; i++)
            {
                string formatted = _console.FormatLineForTerminal(lines[i]);
                Console.WriteLine(formatted.Length > 0 ? formatted : "");
            }
        }

        /// <summary>擦除 console 标记的待清理行。VT 模式用 ESC[2K，非 VT 模式用空格覆盖。</summary>
        internal void EraseTerminalRows()
        {
            int rows = _console._pendingEraseRows;
            if (rows <= 0) return;
            _console._pendingEraseRows = 0;

            var screen = _getScreen();
            if (screen != null)
            {
                // VT 模式：绝对定位 + ESC[2K 清行
                int currentRow = screen.GetCurrentRow();
                for (int i = 0; i < rows; i++)
                {
                    int targetRow = currentRow - 1 - i;
                    if (targetRow < 0) break;
                    screen.ClearLine(targetRow);
                }
                screen.SetCursor(0, Math.Max(currentRow - rows, 0));
                return;
            }

            // 非 VT 模式：写空格覆盖
            _cursor.Save(out int savedLeft, out int savedTop);
            int consoleWidth = TerminalCursor.TryGetWindowWidth();

            for (int i = 0; i < rows; i++)
            {
                int targetTop = savedTop - 1 - i;
                if (targetTop < 0) break;
                _cursor.Set(0, targetTop);
                TerminalCursor.TryWrite(new string(' ', consoleWidth));
            }

            _cursor.Set(0, Math.Max(savedTop - rows, 0));
        }
    }
}
