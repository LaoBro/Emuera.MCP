using System.Collections.Generic;
using MinorShift.Emuera.UI.Game;

namespace MinorShift.Emuera.GameView
{
    /// <summary>
    /// 按钮命中区追踪器（Phase 3-3 / ADR-0014）。
    /// Phase 3：Region 改为 value-based（Value/IsInteger），移除 Generation 客户端过滤——
    /// 服务端 ConsoleInputHandler 按 button.Generation 校验兜底（ConsoleInputHandler.cs:210-267）。
    /// 新增 UpdateFromSnapshot 从 DisplaySnapshot 构建命中区（value-based，供 Phase 4 CLI 消费快照）。
    /// 旧 RecordLineRegions 路径保留，Phase 4 切换调用点后删旧方法。
    /// </summary>
    internal sealed class ButtonRegionTracker
    {
        private readonly List<Region> _regions = new();

        public int RegionCount => _regions.Count;

        public void Clear() => _regions.Clear();

        /// <summary>
        /// 旧路径：从 ConsoleButtonString[] 记录命中区（引用式入口，Phase 4 切换后删）。
        /// Phase 3：Region 改为 value-based，从此处提取 Value/IsInteger；
        /// Generation 过滤移除——服务端 ConsoleInputHandler 校验兜底。
        /// currentGeneration 参数保留以维持签名兼容，但不再用于过滤。
        /// </summary>
        public void RecordLineRegions(string formattedLine, int bufferRow, ConsoleButtonString[]? buttons, long currentGeneration)
        {
            if (buttons == null || buttons.Length == 0 || string.IsNullOrEmpty(formattedLine)) return;

            int column = TerminalDisplayWidth.LeadingDisplayWidth(formattedLine);

            foreach (var btn in buttons)
            {
                if (btn == null) continue;

                string btnText = btn.ToString() ?? "";
                int segmentWidth = TerminalDisplayWidth.GetDisplayWidth(btnText);

                if (btn.IsButton && segmentWidth > 0)
                {
                    object value = btn.IsInteger ? (object)btn.Input : (object)btn.Inputs;
                    _regions.Add(new Region(bufferRow, column, column + segmentWidth - 1, value, btn.IsInteger));
                    AgentLog.Instance.Write($"[region] row={bufferRow} col={column}-{column + segmentWidth - 1} input={btnText}");
                }

                column += segmentWidth;
            }
        }

        /// <summary>
        /// 新路径：从 DisplaySnapshot 构建命中区（Phase 3-3 / Q13）。
        /// value-based——从快照 entries[j].button 取 col/width/value/isInteger。
        /// scrollOffset/viewportHeight 决定可见行切片，Row=i 是视口内行号。
        /// </summary>
        public void UpdateFromSnapshot(DisplaySnapshot snapshot, int scrollOffset, int viewportHeight)
        {
            _regions.Clear();

            int startLine = scrollOffset > 0
                ? System.Math.Max(0, snapshot.lines.Count - viewportHeight - scrollOffset)
                : System.Math.Max(0, snapshot.lines.Count - viewportHeight);

            for (int i = 0; i < viewportHeight && (startLine + i) < snapshot.lines.Count; i++)
            {
                var line = snapshot.lines[startLine + i];
                foreach (var entry in line.entries)
                {
                    if (entry.button is { } btn && btn.col is { } c && btn.width is { } w)
                    {
                        _regions.Add(new Region(
                            Row: i,
                            Left: c,
                            Right: c + w - 1,
                            Value: btn.value,
                            IsInteger: btn.isInteger));
                    }
                }
            }
        }

        /// <summary>命中测试，返回匹配的 Region（value-based）。</summary>
        public Region? HitTest(int row, int col)
        {
            for (int i = _regions.Count - 1; i >= 0; i--)
            {
                var r = _regions[i];
                if (r.Row == row && col >= r.Left && col <= r.Right)
                    return r;
            }
            return null;
        }

        /// <summary>
        /// 命中区（value-based）。Value 是按钮提交值（int/long 或 string），
        /// IsInteger 标记整数按钮。Phase 3 前持有 ConsoleButtonString 引用 + Generation，
        /// Phase 3 改为 value-based 以支持从 DisplaySnapshot 构建。
        /// </summary>
        internal readonly record struct Region(
            int Row,
            int Left,
            int Right,
            object Value,
            bool IsInteger);
    }
}
