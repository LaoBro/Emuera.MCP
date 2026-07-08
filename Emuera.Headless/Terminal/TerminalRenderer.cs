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

        /// <summary>
        /// auto-follow 回调：FlushBuffer 检测到新行且 offset>0 时，归零 offset + FullRefresh 后调用，
        /// 让 AgentCliProtocol 同步状态栏/倒计时/按钮区域。ADR-0006。
        /// </summary>
        internal Action? OnScrollAutoFollow { get; set; }

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
                        // ADR-0006：ClearOp 触发 auto-follow，归零 Scroll Offset。
                        if (_getScreen()!.ScrollOffset > 0)
                        {
                            _getScreen()!.ResetScroll();
                            OnScrollAutoFollow?.Invoke();
                        }
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
                // ADR-0006：新输出到达时 auto-follow 归零 offset。增量分支假设光标在底部，
                // offset>0 时光标在状态栏行，必须走 FullRefresh（绝对定位）。
                var screen = _getScreen();
                if (screen != null && screen.ScrollOffset > 0)
                {
                    screen.ResetScroll();
                    FullRefresh();
                    OnScrollAutoFollow?.Invoke();
                }
                // 内容超出视口时必须走 FullRefresh：WriteNewLinesSince 依赖 Console.WriteLine
                // 的自然光标推进，但光标在底部行时 \n 会触发终端滚动，ConPTY 不会把滚出
                // 视口的字节重新发送给 master，导致后续行永远渲染不出来。
                // FullRefresh 用绝对定位（WriteLineAt）重绘整个可见区，不依赖滚动。
                else if (screen != null && lines.Count > screen.WindowHeight - 1)
                {
                    FullRefresh();
                }
                else
                {
                    WriteNewLinesSince(lines, _lastRenderedLineNo, _lastRenderedLastLine);
                }
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

        /// <summary>全量重绘可见行。ADR-0005 Issue 4：删除非 VT 降级分支，仅保留 VT 备用屏绝对定位路径。
        /// ADR-0006：读 _screen.ScrollOffset 计算 startLine + visibleLines，offset>0 时渲染更早切片并缩小可见区（底部留状态栏）。</summary>
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

            int offset = screen.ScrollOffset;
            // 复用 AgentCliVtScreen.GetVisibleLines()：offset=0 → consoleHeight-1，offset>0 → consoleHeight-2。
            int visibleLines = screen.GetVisibleLines();
            // offset>0 时回看历史：startLine 向前移动 offset 行。
            int startLine = offset > 0
                ? Math.Max(0, lines.Count - visibleLines - offset)
                : Math.Max(0, lines.Count - visibleLines);

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
