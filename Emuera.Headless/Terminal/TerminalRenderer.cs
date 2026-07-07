using System;
using System.Collections.Generic;
using MinorShift.Emuera.UI.Game;

namespace MinorShift.Emuera.GameView
{
    /// <summary>
    /// 终端输出渲染：刷新屏幕、擦除行、刷新缓冲区。
    /// ADR-0005：VT-only 后原 <c>_ansiEnabled</c> 字段已删除，调用方假设 ANSI 可用。
    /// ADR-0005 Issue 4：删除 <c>_screen == null</c> 降级分支与 <c>_cursor</c> 字段，
    /// 调用方保证 <see cref="AgentCliVtScreen"/> 在 VT 主循环内必非 null。
    /// 从 AgentCliProtocol 拆分以隔离终端输出逻辑。
    /// </summary>
    internal sealed class TerminalRenderer
    {
        private readonly EmueraConsole _console;
        private readonly Func<AgentCliVtScreen?> _getScreen;

        private int _lastRenderedLineNo = -1;
        private ConsoleDisplayLine? _lastRenderedLastLine;
        private string? _currentBgHex;

        public TerminalRenderer(
            EmueraConsole console,
            Func<AgentCliVtScreen?> getScreen)
        {
            _console = console;
            _getScreen = getScreen;
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
                        _getScreen()!.ClearScreen();
                        _lastRenderedLineNo = -1;
                        _lastRenderedLastLine = null;
                        cleared = true;
                        break;
                    case SetBgOp bg:
                        _currentBgHex = bg.color;
                        WriteBgEscape(bg.color);
                        break;
                    case ClearLineOp:
                        // CLEARLINE N 后 displayLineList 末尾行被删除、LineNo 回退，
                        // 后续可能重印同 LineNo 的新行。重置 delta tracking 迫使走 FullRefresh，
                        // 避免增量分支因 LineNo 相等/回环漏掉重绘导致按钮区域与终端显示错位。
                        _lastRenderedLineNo = -1;
                        _lastRenderedLastLine = null;
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

            // 上一轮 FlushBuffer 末行 IsLineEnd=false（PRINTN/PRINTC 等）时，
            // CLI 协议在回合间会写输入提示/回显，光标已不在该行末尾。
            // 增量擦除依赖光标位置会擦错行，改走 FullRefresh（绝对定位）最安全。
            if (_lastRenderedLastLine != null && !_lastRenderedLastLine.IsLineEnd)
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
                // 删行场景：LineNo 回退，增量擦除涉及 IsLineEnd 光标位置复杂性。
                // ClearLineOp 已在 drain 时重置 tracking 走 FullRefresh，此分支仅处理
                // LineNo 回环等罕见边界，直接全量重绘最安全。
                FullRefresh();
                return;
            }
            else
            {
                // LineNo 不变但行对象变更：擦除末行后重写。
                // IsLineEnd=false 的情况已在上方由 FullRefresh 处理，
                // 到这里 _lastRenderedLastLine.IsLineEnd 必为 true。
                if (!ReferenceEquals(_lastRenderedLastLine, lastLine))
                {
                    EraseTerminalRows(1);
                    WriteDisplayLine(lastLine);
                }
            }

            _lastRenderedLineNo = currentLineNo;
            _lastRenderedLastLine = lastLine;
        }

        /// <summary>全量重绘可见行。ADR-0005 Issue 4：删除非 VT 降级分支，仅保留 VT 备用屏绝对定位路径。</summary>
        internal void FullRefresh()
        {
            var lines = _console.DisplayLineList;
            var screen = _getScreen()!;

            screen.ClearScreen();

            if (_currentBgHex != null)
                WriteBgEscape(_currentBgHex);

            if (lines.Count == 0)
            {
                _lastRenderedLineNo = -1;
                _lastRenderedLastLine = null;
                return;
            }

            int consoleHeight = screen.WindowHeight;
            int visibleLines = Math.Max(consoleHeight - 1, 1);
            int startLine = Math.Max(0, lines.Count - visibleLines);

            for (int i = 0; i < visibleLines && (startLine + i) < lines.Count; i++)
            {
                int lineIndex = startLine + i;
                int viewportRow = i;
                string formatted = TerminalLineFormatter.FormatLineForTerminal(
                    lines[lineIndex], _console.SelectingButton, _console.CharWidthConfig, ansiEnabled: true);
                screen.WriteLineAt(viewportRow, formatted.Length > 0 ? formatted : "");
            }

            int drawnRows = Math.Min(visibleLines, lines.Count - startLine);
            screen.SetCursor(drawnRows, 0);

            _lastRenderedLineNo = lines[^1].LineNo;
            _lastRenderedLastLine = lines[^1];
        }

        /// <summary>擦除指定行数。ADR-0005 Issue 4：删除非 VT 空格覆盖分支，仅保留 VT ESC[2K 路径。</summary>
        internal void EraseTerminalRows(int rows)
        {
            if (rows <= 0) return;

            var screen = _getScreen()!;
            int currentRow = screen.GetCurrentRow();
            for (int i = 0; i < rows; i++)
            {
                int targetRow = currentRow - 1 - i;
                if (targetRow < 0) break;
                screen.ClearLine(targetRow);
            }
            screen.SetCursor(0, Math.Max(currentRow - rows, 0));
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
            string text = TerminalLineFormatter.FormatLineForTerminal(
                line, _console.SelectingButton, _console.CharWidthConfig, ansiEnabled: true);
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

            // IsLineEnd=false 的上一行已在 FlushBuffer 入口由 FullRefresh 处理，
            // 到此处的 lastRenderedLastLine.IsLineEnd 必为 true，光标在下一行行首。
            for (int i = startIdx; i < lines.Count; i++)
                WriteDisplayLine(lines[i]);
        }
    }
}
