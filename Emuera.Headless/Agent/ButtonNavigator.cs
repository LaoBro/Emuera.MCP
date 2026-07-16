using System;
using System.Collections.Generic;
using MinorShift.Emuera.UI.Game;

namespace MinorShift.Emuera.GameView;

internal readonly record struct ButtonPos(int Row, int Left, int Right, ConsoleButtonString Button, string InputKey);

internal static class ButtonNavigator
{
    internal enum Direction
    {
        Up,
        Down,
        Left,
        Right
    }

    public static int FindNext(IReadOnlyList<ButtonPos> buttons, ButtonPos? current, Direction direction)
    {
        if (buttons.Count == 0) return -1;
        if (current == null) return 0;

        var cur = current.Value;
        int currentCenter = (cur.Left + cur.Right) / 2;

        return direction switch
        {
            Direction.Up => FindNextCore(buttons, cur,
                p => p.Row < cur.Row,
                (a, best) => a.Row > best.Row
                    || (a.Row == best.Row && CenterDist(a, currentCenter) < CenterDist(best, currentCenter))),
            Direction.Down => FindNextCore(buttons, cur,
                p => p.Row > cur.Row,
                (a, best) => a.Row < best.Row
                    || (a.Row == best.Row && CenterDist(a, currentCenter) < CenterDist(best, currentCenter))),
            Direction.Left => FindNextCore(buttons, cur,
                p => p.Row == cur.Row && p.Right < cur.Left,
                (a, best) => a.Right > best.Right),
            Direction.Right => FindNextCore(buttons, cur,
                p => p.Row == cur.Row && p.Left > cur.Right,
                (a, best) => a.Left < best.Left),
            _ => -1,
        };
    }

    private static int FindNextCore(IReadOnlyList<ButtonPos> buttons, ButtonPos current,
        Func<ButtonPos, bool> isCandidate, Func<ButtonPos, ButtonPos, bool> isBetter)
    {
        int newIdx = -1;
        for (int i = 0; i < buttons.Count; i++)
        {
            var p = buttons[i];
            if (!isCandidate(p)) continue;
            if (newIdx < 0 || isBetter(p, buttons[newIdx]))
                newIdx = i;
        }
        return newIdx;
    }

    private static int CenterDist(ButtonPos p, int center)
        => Math.Abs((p.Left + p.Right) / 2 - center);
}
