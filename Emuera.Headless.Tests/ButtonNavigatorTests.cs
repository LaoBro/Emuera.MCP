using System.Collections.Generic;
using MinorShift.Emuera.GameView;
using Dir = MinorShift.Emuera.GameView.ButtonNavigator.Direction;
using Xunit;

namespace Emuera.Headless.Tests;

public class ButtonNavigatorTests
{
    // Helper: Button field is null! — FindNext never reads Button or InputKey.
    private static ButtonPos P(int row, int left, int right) => new(row, left, right, null!, "");

    // L-shape: A at top-left, B below A, C to the right of B.
    //   A(0,  0-4)  center=2
    //   B(1,  0-4)  center=2
    //   C(1,  6-10) center=8
    private static readonly ButtonPos A = P(0, 0, 4);
    private static readonly ButtonPos B = P(1, 0, 4);
    private static readonly ButtonPos C = P(1, 6, 10);
    private static readonly IReadOnlyList<ButtonPos> LShape = [A, B, C];

    // Same-column: two buttons stacked.
    private static readonly ButtonPos Col0 = P(0, 0, 4);
    private static readonly ButtonPos Col1 = P(1, 0, 4);
    private static readonly IReadOnlyList<ButtonPos> SameColumn = [Col0, Col1];

    // Same-row: two buttons side by side.
    private static readonly ButtonPos RowL = P(0, 0, 4);
    private static readonly ButtonPos RowR = P(0, 6, 10);
    private static readonly IReadOnlyList<ButtonPos> SameRow = [RowL, RowR];

    // Three on same row, center-2 and center-8 below — for center tiebreak.
    private static readonly ButtonPos T0 = P(0, 0, 4);   // center 2
    private static readonly ButtonPos T1 = P(0, 6, 10);  // center 8
    private static readonly ButtonPos T2 = P(1, 2, 6);   // center 4, dist to T0:2 vs T1:4
    private static readonly IReadOnlyList<ButtonPos> CenterTiebreak = [T0, T1, T2];

    // ---------- L-shape ----------

    [Fact] void L_Up_from_A() => Assert.Equal(-1, ButtonNavigator.FindNext(LShape, A, Dir.Up));
    [Fact] void L_Down_from_A() => Assert.Equal(1, ButtonNavigator.FindNext(LShape, A, Dir.Down));
    [Fact] void L_Left_from_A() => Assert.Equal(-1, ButtonNavigator.FindNext(LShape, A, Dir.Left));
    [Fact] void L_Right_from_A() => Assert.Equal(-1, ButtonNavigator.FindNext(LShape, A, Dir.Right));
    [Fact] void L_Up_from_B() => Assert.Equal(0, ButtonNavigator.FindNext(LShape, B, Dir.Up));
    [Fact] void L_Down_from_B() => Assert.Equal(-1, ButtonNavigator.FindNext(LShape, B, Dir.Down));
    [Fact] void L_Left_from_B() => Assert.Equal(-1, ButtonNavigator.FindNext(LShape, B, Dir.Left));
    [Fact] void L_Right_from_B() => Assert.Equal(2, ButtonNavigator.FindNext(LShape, B, Dir.Right));
    [Fact] void L_Up_from_C() => Assert.Equal(0, ButtonNavigator.FindNext(LShape, C, Dir.Up));
    [Fact] void L_Down_from_C() => Assert.Equal(-1, ButtonNavigator.FindNext(LShape, C, Dir.Down));
    [Fact] void L_Left_from_C() => Assert.Equal(1, ButtonNavigator.FindNext(LShape, C, Dir.Left));
    [Fact] void L_Right_from_C() => Assert.Equal(-1, ButtonNavigator.FindNext(LShape, C, Dir.Right));

    // ---------- Same-column ----------

    [Fact] void SameCol_Down_from_0() => Assert.Equal(1, ButtonNavigator.FindNext(SameColumn, Col0, Dir.Down));
    [Fact] void SameCol_Up_from_0() => Assert.Equal(-1, ButtonNavigator.FindNext(SameColumn, Col0, Dir.Up));
    [Fact] void SameCol_Up_from_1() => Assert.Equal(0, ButtonNavigator.FindNext(SameColumn, Col1, Dir.Up));
    [Fact] void SameCol_Down_from_1() => Assert.Equal(-1, ButtonNavigator.FindNext(SameColumn, Col1, Dir.Down));

    // ---------- Same-row ----------

    [Fact] void SameRow_Right_from_L() => Assert.Equal(1, ButtonNavigator.FindNext(SameRow, RowL, Dir.Right));
    [Fact] void SameRow_Left_from_L() => Assert.Equal(-1, ButtonNavigator.FindNext(SameRow, RowL, Dir.Left));
    [Fact] void SameRow_Left_from_R() => Assert.Equal(0, ButtonNavigator.FindNext(SameRow, RowR, Dir.Left));
    [Fact] void SameRow_Right_from_R() => Assert.Equal(-1, ButtonNavigator.FindNext(SameRow, RowR, Dir.Right));

    // ---------- Single button ----------

    [Fact] void Single_Up() => Assert.Equal(-1, ButtonNavigator.FindNext([A], A, Dir.Up));
    [Fact] void Single_Down() => Assert.Equal(-1, ButtonNavigator.FindNext([A], A, Dir.Down));
    [Fact] void Single_Left() => Assert.Equal(-1, ButtonNavigator.FindNext([A], A, Dir.Left));
    [Fact] void Single_Right() => Assert.Equal(-1, ButtonNavigator.FindNext([A], A, Dir.Right));

    // ---------- Empty list ----------

    [Fact] void Empty_any() => Assert.Equal(-1, ButtonNavigator.FindNext([], A, Dir.Up));

    // ---------- current == null ----------

    [Fact] void CurrentNull_returns_0() => Assert.Equal(0, ButtonNavigator.FindNext(LShape, null, Dir.Down));

    // ---------- Same-row, 3 buttons: pick closest edge among multiple candidates ----------
    //   X(0,  0-4)  Y(0,  6-8)   Z(0,  10-14)
    private static readonly ButtonPos X = P(0, 0, 4);
    private static readonly ButtonPos Y = P(0, 6, 8);
    private static readonly ButtonPos Z = P(0, 10, 14);
    private static readonly IReadOnlyList<ButtonPos> SameRow3 = [X, Y, Z];

    [Fact] void SameRow3_Right_from_X_picks_Y() => Assert.Equal(1, ButtonNavigator.FindNext(SameRow3, X, Dir.Right));
    [Fact] void SameRow3_Left_from_Z_picks_Y() => Assert.Equal(1, ButtonNavigator.FindNext(SameRow3, Z, Dir.Left));

    // ---------- Center-dist tiebreak ----------

    [Fact] void CenterTiebreak_Down_from_T0()
    {
        // T0 center=2, T2 center=4 (dist 2), T1 center=8 (dist 6). T2 wins.
        Assert.Equal(2, ButtonNavigator.FindNext(CenterTiebreak, T0, Dir.Down));
    }

    [Fact] void CenterTiebreak_Up_from_T2()
    {
        // T2 center=4, T0 center=2 (dist 2), T1 center=8 (dist 4). T0 wins.
        Assert.Equal(0, ButtonNavigator.FindNext(CenterTiebreak, T2, Dir.Up));
    }
}
