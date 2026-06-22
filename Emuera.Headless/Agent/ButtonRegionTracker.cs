using System.Collections.Generic;
using MinorShift.Emuera.UI.Game;

namespace MinorShift.Emuera.GameView
{
    internal sealed class ButtonRegionTracker
    {
        private readonly List<Region> _regions = new();

        public int RegionCount => _regions.Count;

        public void Clear() => _regions.Clear();

        public void RecordLineRegions(string formattedLine, int bufferRow, ConsoleButtonString[]? buttons, long currentGeneration)
        {
            if (buttons == null || buttons.Length == 0 || string.IsNullOrEmpty(formattedLine)) return;

            int column = LeadingDisplayWidth(formattedLine);

            foreach (var btn in buttons)
            {
                if (btn == null) continue;

                string btnText = btn.ToString() ?? "";
                int segmentWidth = TerminalDisplayWidth.GetDisplayWidth(btnText);

                if (btn.IsButton && btn.Generation == currentGeneration && segmentWidth > 0)
                {
                    _regions.Add(new Region(bufferRow, column, column + segmentWidth - 1, btn, currentGeneration));
                    AgentLog.Instance.Write($"[region] row={bufferRow} col={column}-{column + segmentWidth - 1} input={btn.Inputs} label={btnText}");
                }

                column += segmentWidth;
            }
        }

        public ConsoleButtonString? HitTest(int row, int col)
        {
            for (int i = _regions.Count - 1; i >= 0; i--)
            {
                var r = _regions[i];
                if (r.Row == row && col >= r.Left && col <= r.Right)
                    return r.Button;
            }
            return null;
        }

        private static int LeadingDisplayWidth(string s)
        {
            int width = 0;
            foreach (char c in s)
            {
                if (c == ' ') { width += 1; continue; }
                if (c == '\u3000') { width += 2; continue; }
                break;
            }
            return width;
        }

        internal readonly record struct Region(
            int Row,
            int Left,
            int Right,
            ConsoleButtonString Button,
            long Generation);
    }
}