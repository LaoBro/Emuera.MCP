using System;
using System.Collections.Generic;
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
        private readonly bool _ansiEnabled;

        private int _lastRenderedLineNo = -1;
        private ConsoleDisplayLine? _lastRenderedLastLine;
        private string? _currentBgHex;

        public TerminalRenderer(
            EmueraConsole console,
            Func<AgentCliVtScreen?> getScreen,
            TerminalCursor cursor,
            bool ansiEnabled)
        {
            _console = console;
            _getScreen = getScreen;
            _cursor = cursor;
            _ansiEnabled = ansiEnabled;
        }

        /// <summary>Flush pending ops and render displayLineList delta to terminal.</summary>
        internal void FlushBuffer()
        {
            bool cleared = false;
            _console.DrainPendingOpsForCli(op =>
            {
                switch (op)
                {
                    case ClearOp:
                        {
                            var s = _getScreen();
                            if (s != null)
                                s.ClearScreen();
                            else
                                _cursor.ClearScreen();
                        }
                        _lastRenderedLineNo = -1;
                        _lastRenderedLastLine = null;
                        cleared = true;
                        break;
                    case SetBgOp bg:
                        _currentBgHex = bg.color;
                        if (_getScreen() != null && _ansiEnabled)
                            WriteBgEscape(bg.color);
                        break;
                }
            });
            if (cleared) return;

            var lines = _console.DisplayLineList;
            if (lines.Count == 0)
            {
                _lastRenderedLineNo = -1;
                _lastRenderedLastLine = null;
                return;
            }

            var lastLine = lines[^1];
            int currentLineNo = lastLine.LineNo;

            if (_lastRenderedLineNo < 0)
            {
                FullRefresh();
                return;
            }

            if (currentLineNo > _lastRenderedLineNo)
            {
                WriteNewLinesSince(lines, _lastRenderedLineNo, _lastRenderedLastLine);
            }
            else if (currentLineNo < _lastRenderedLineNo)
            {
                int delta = _lastRenderedLineNo - currentLineNo;
                EraseTerminalRows(delta);
                if (!ReferenceEquals(_lastRenderedLastLine, lastLine))
                {
                    EraseTerminalRows(1);
                    WriteDisplayLine(lastLine);
                }
            }
            else
            {
                if (!ReferenceEquals(_lastRenderedLastLine, lastLine))
                {
                    EraseTerminalRows(1);
                    WriteDisplayLine(lastLine);
                }
            }

            _lastRenderedLineNo = currentLineNo;
            _lastRenderedLastLine = lastLine;
        }

        /// <summary>全量重绘可见行。VT 模式用备用屏绝对定位，非 VT 模式用自然滚动。</summary>
        internal void FullRefresh()
        {
            var lines = _console.DisplayLineList;
            var screen = _getScreen();

            if (screen != null)
            {
                screen.ClearScreen();

                if (_currentBgHex != null && _ansiEnabled)
                    WriteBgEscape(_currentBgHex);

                if (lines.Count == 0) goto SyncState;

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

                int drawnRows = Math.Min(visibleLines, lines.Count - startLine);
                screen.SetCursor(drawnRows, 0);
            }
            else
            {
                _cursor.ClearScreen();
                if (lines.Count == 0) goto SyncState;

                int height = TerminalCursor.TryGetWindowHeight();
                int visLines = Math.Max(height - 1, 1);
                int startLn = Math.Max(0, lines.Count - visLines);

                for (int i = startLn; i < lines.Count; i++)
                {
                    string formatted = _console.FormatLineForTerminal(lines[i]);
                    Console.WriteLine(formatted.Length > 0 ? formatted : "");
                }
            }

        SyncState:
            if (lines.Count > 0)
            {
                _lastRenderedLineNo = lines[^1].LineNo;
                _lastRenderedLastLine = lines[^1];
            }
            else
            {
                _lastRenderedLineNo = -1;
                _lastRenderedLastLine = null;
            }
        }

        /// <summary>擦除指定行数。VT 模式用 ESC[2K，非 VT 模式用空格覆盖。</summary>
        internal void EraseTerminalRows(int rows)
        {
            if (rows <= 0) return;

            var screen = _getScreen();
            if (screen != null)
            {
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

        private static void WriteBgEscape(string? hexColor)
        {
            if (hexColor == null) return;
            string hex = hexColor.TrimStart('#');
            int r = Convert.ToInt32(hex.Substring(0, 2), 16);
            int g = Convert.ToInt32(hex.Substring(2, 2), 16);
            int b = Convert.ToInt32(hex.Substring(4, 2), 16);
            Console.Write($"\x1b[48;2;{r};{g};{b}m");
        }

        private void WriteDisplayLine(ConsoleDisplayLine line)
        {
            string text = _console.FormatLineForTerminal(line);
            if (line.IsLineEnd)
                Console.WriteLine(text);
            else
                Console.Write(text);
        }

        private void WriteNewLinesSince(
            List<ConsoleDisplayLine> lines,
            int lastRenderedLineNo,
            ConsoleDisplayLine? lastRenderedLastLine)
        {
            int startIdx = -1;
            for (int i = lines.Count - 1; i >= 0; i--)
            {
                if (lines[i].LineNo <= lastRenderedLineNo)
                {
                    startIdx = i + 1;
                    break;
                }
            }
            if (startIdx < 0) startIdx = 0;

            if (lastRenderedLastLine != null
                && !lastRenderedLastLine.IsLineEnd
                && startIdx > 0)
            {
                EraseTerminalRows(1);
                startIdx--;
            }

            for (int i = startIdx; i < lines.Count; i++)
                WriteDisplayLine(lines[i]);
        }
    }
}
