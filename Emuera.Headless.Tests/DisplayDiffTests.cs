using System;
using System.Collections.Generic;
using System.Linq;
using MinorShift.Emuera;
using MinorShift.Emuera.GameView;
using MinorShift.Emuera.Primitives;
using MinorShift.Emuera.Runtime;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Terminal.Platform;
using MinorShift.Emuera.UI.Game;
using Xunit;

namespace Emuera.Headless.Tests;

/// <summary>
/// Phase 2-4：DisplayDiff 增量模型单测。
/// 验证 ComputeDiff 在两个连续 DisplaySnapshot 之间产出正确的 LineOp 序列：
/// - 纯追加 → AppendLinesOp
/// - 纯截尾（CLEARLINE） → ClearLineDiffOp（权威 n，非 keepCount）
/// - CLEAR/全重置 → ClearScreenOp（+ AppendLinesOp 重印）
/// - 末行原地编辑 → ClearLineDiffOp + Append
/// - 背景色变更 → diff.bgColor 非 null
/// - no-op 回合 → null（ReferenceEquals 短路）
///
/// 复用 DisplayStateChangeDetectionTests 的构造模式（GlobalStatic.OpenScope + EmueraConsole + DisplayState）。
/// </summary>
public class DisplayDiffTests : IDisposable
{
    private readonly IDisposable _scope;
    private readonly EmueraConsole _console;
    private readonly DisplayState _displayState;

    public DisplayDiffTests()
    {
        _scope = GlobalStatic.OpenScope(new ConfigData());
        var ui = new HeadlessConsole();
        _console = new EmueraConsole(ui, new NullTerminalSetup());
        _displayState = new DisplayState(_console, "MS Gothic");
    }

    public void Dispose() => _scope.Dispose();

    private void AddPendingOp(TurnOp op) => _console._state._pendingOps.Enqueue(op);

    private static ConsoleDisplayLine Line(string text) =>
        new(new[] { TestButtonFactory.CreateNonButton(text) },
            isLogical: true, temporary: false);

    private void PrintLine(string text)
    {
        _console.DisplayLineList.Add(Line(text));
        AddPendingOp(new PrintOp(
            new List<PrintSegment> { new(text, null, null, null, null) },
            button: null));
    }

    /// <summary>模拟 BuildTurn 的调用序列：ComputeDiff（plan C v5 原子化 drain+分类清空信号）。</summary>
    private DisplayDiff? BuildDiff() => _displayState.ComputeDiff();

    // ---------- 首次回合：无 diff ----------

    [Fact]
    public void First_turn_compute_diff_returns_null()
    {
        PrintLine("hello");
        var diff = BuildDiff();
        Assert.Null(diff);
    }

    // ---------- 纯追加 ----------

    [Fact]
    public void Pure_append_produces_append_lines_op()
    {
        PrintLine("line1");
        BuildDiff(); // 首次 → null，_previous 设为当前快照

        PrintLine("line2");
        var diff = BuildDiff();

        Assert.NotNull(diff);
        var op = Assert.Single(diff!.lineOps);
        var append = Assert.IsType<AppendLinesOp>(op);
        Assert.Single(append.newLines);
        Assert.Equal("line2", append.newLines[0].entries[0].segments[0].text);
        Assert.Null(diff.bgColor);
    }

    // ---------- 纯截尾（CLEARLINE）----------

    [Fact]
    public void Pure_truncate_produces_clear_line_diff_op()
    {
        PrintLine("line1");
        PrintLine("line2");
        PrintLine("line3");
        BuildDiff(); // 首次

        // CLEARLINE 2：删除末 2 行
        _console.DisplayLineList.RemoveRange(_console.DisplayLineList.Count - 2, 2);
        AddPendingOp(new ClearLineOp(2));

        var diff = BuildDiff();

        Assert.NotNull(diff);
        var op = Assert.Single(diff!.lineOps);
        var clearLine = Assert.IsType<ClearLineDiffOp>(op);
        Assert.Equal(2, clearLine.clearCount); // 清 2 行（权威 n，非 keepCount）
        Assert.Null(diff.bgColor);
    }

    // ---------- CLEAR / 全重置（ClearScreenOp）----------

    [Fact]
    public void Clear_all_produces_clear_screen_op_then_append()
    {
        PrintLine("line1");
        PrintLine("line2");
        BuildDiff();

        // CLEAR：清空所有行后写全新内容
        _console.DisplayLineList.Clear();
        _console.DisplayLineList.Add(Line("new1"));
        _console.DisplayLineList.Add(Line("new2"));
        AddPendingOp(new ClearOp());
        AddPendingOp(new PrintOp(
            new List<PrintSegment> { new("new1", null, null, null, null) },
            button: null));
        AddPendingOp(new PrintOp(
            new List<PrintSegment> { new("new2", null, null, null, null) },
            button: null));

        var diff = BuildDiff();

        Assert.NotNull(diff);
        Assert.Equal(2, diff!.lineOps.Count);
        Assert.IsType<ClearScreenOp>(diff.lineOps[0]);
        var append = Assert.IsType<AppendLinesOp>(diff.lineOps[1]);
        Assert.Equal(2, append.newLines.Count);
        Assert.Equal("new1", append.newLines[0].entries[0].segments[0].text);
        Assert.Equal("new2", append.newLines[1].entries[0].segments[0].text);
    }

    // ---------- 末行原地编辑（ClearLineDiffOp + Append）----------

    [Fact]
    public void Last_line_edit_produces_clear_line_then_append()
    {
        PrintLine("line1");
        PrintLine("line2");
        BuildDiff();

        // 末行原地编辑：删除最后一行 + 写新内容
        _console.DisplayLineList.RemoveAt(_console.DisplayLineList.Count - 1);
        PrintLine("edited");

        var diff = BuildDiff();

        Assert.NotNull(diff);
        Assert.Equal(2, diff!.lineOps.Count);
        var clearLine = Assert.IsType<ClearLineDiffOp>(diff.lineOps[0]);
        Assert.Equal(1, clearLine.clearCount);
        var append = Assert.IsType<AppendLinesOp>(diff.lineOps[1]);
        var appendedLine = Assert.Single(append.newLines);
        Assert.Equal("edited", appendedLine.entries[0].segments[0].text);
    }

    // ---------- 背景色变更 ----------

    [Fact]
    public void BgColor_change_produces_non_null_bgColor_in_diff()
    {
        PrintLine("line1");
        BuildDiff();

        // 改变背景色（不改变行内容）
        _console.bgColor = EmuColor.FromArgb(255, 0, 0);
        AddPendingOp(new SetBgOp("#FF0000"));

        var diff = BuildDiff();

        Assert.NotNull(diff);
        Assert.Equal("#FF0000", diff!.bgColor);
        // 行内容未变 → 空 lineOps
        Assert.Empty(diff.lineOps);
    }

    // ---------- 背景色变更 + 行追加 ----------

    [Fact]
    public void BgColor_change_and_append_both_in_diff()
    {
        PrintLine("line1");
        BuildDiff();

        _console.bgColor = EmuColor.FromArgb(0, 128, 0);
        AddPendingOp(new SetBgOp("#008000"));
        PrintLine("line2");

        var diff = BuildDiff();

        Assert.NotNull(diff);
        Assert.Equal("#008000", diff!.bgColor);
        var op = Assert.Single(diff.lineOps);
        Assert.IsType<AppendLinesOp>(op);
    }

    // ---------- no-op 回合（ReferenceEquals → null）----------

    [Fact]
    public void Noop_turn_produces_null_diff()
    {
        PrintLine("line1");
        BuildDiff();

        // 无新 op → TryUpdate 返回 false，_current 引用不变
        var diff = BuildDiff();

        Assert.Null(diff);
    }

    // ---------- 完全重印相同内容（权威清空信号仍上报清行）----------

    [Fact]
    public void Reprint_same_content_still_signals_clear_line()
    {
        PrintLine("line1");
        PrintLine("line2");
        BuildDiff();

        // CLEARLINE 2 + 重印相同内容：plan C（权威溯源）即便内容值相等也如实上报清行，
        // 因为引擎确实执行了 CLEARLINE 2 + 重印——diff 携带 ClearLineDiffOp(2) + AppendLinesOp(2)。
        _console.DisplayLineList.Clear();
        PrintLine("line1");
        PrintLine("line2");
        // ClearLineOp 不影响 displayLineList，只触发 pendingOps；这里手动清空+重印
        AddPendingOp(new ClearLineOp(2));

        var diff = BuildDiff();

        // diff 非 null；权威清空信号 → ClearLineDiffOp(2) + AppendLinesOp(2 行相同内容)
        Assert.NotNull(diff);
        Assert.Equal(2, diff!.lineOps.Count);
        var clearLine = Assert.IsType<ClearLineDiffOp>(diff.lineOps[0]);
        Assert.Equal(2, clearLine.clearCount);
        var append = Assert.IsType<AppendLinesOp>(diff.lineOps[1]);
        Assert.Equal(2, append.newLines.Count);
        Assert.Equal("line1", append.newLines[0].entries[0].segments[0].text);
        Assert.Equal("line2", append.newLines[1].entries[0].segments[0].text);
        Assert.Null(diff.bgColor);
    }

    // ---------- 空 → 有内容 → 空（CLEAR）----------

    [Fact]
    public void Empty_to_content_to_empty_cycle()
    {
        // 首次：空屏
        BuildDiff();

        // 追加 2 行
        PrintLine("a");
        PrintLine("b");
        var diff1 = BuildDiff();
        Assert.NotNull(diff1);
        var append1 = Assert.IsType<AppendLinesOp>(Assert.Single(diff1!.lineOps));
        Assert.Equal(2, append1.newLines.Count);

        // CLEAR：清空
        _console.DisplayLineList.Clear();
        AddPendingOp(new ClearOp());
        var diff2 = BuildDiff();
        Assert.NotNull(diff2);
        Assert.IsType<ClearScreenOp>(Assert.Single(diff2!.lineOps));
    }

    // ---------- 按钮几何在 diff 中保留 ----------

    [Fact]
    public void Diff_append_preserves_button_geometry()
    {
        PrintLine("plain");
        BuildDiff();

        // 追加一行带按钮
        var buttons = new[] { TestButtonFactory.CreateButton("[OK]", input: 1) };
        var line = new ConsoleDisplayLine(buttons, isLogical: true, temporary: false);
        _console.DisplayLineList.Add(line);
        AddPendingOp(new PrintOp(
            new List<PrintSegment> { new("[OK]", null, null, null, null) },
            button: TestSnapshots.Button(1, true, 0, 0, 4)));

        var diff = BuildDiff();

        Assert.NotNull(diff);
        var append = Assert.IsType<AppendLinesOp>(Assert.Single(diff!.lineOps));
        var appendedLine = Assert.Single(append.newLines);
        var entry = Assert.Single(appendedLine.entries);
        Assert.NotNull(entry.button);
        Assert.Equal(0, entry.button!.col);
        Assert.Equal(4, entry.button!.width);
    }

    // ---------- plan C：权威清空信号语义（ClearOp/ClearLineOp 溯源）----------

    [Fact]
    public void ClearOp_dominates_over_clearline_in_same_turn()
    {
        PrintLine("line1");
        PrintLine("line2");
        PrintLine("line3");
        BuildDiff();

        // 同回合先 CLEARLINE 2 再 CLEAR：ClearOp 主导 → 整个回合 ClearScreenOp
        _console.DisplayLineList.Clear();
        _console.DisplayLineList.Add(Line("fresh"));
        AddPendingOp(new ClearLineOp(2));
        AddPendingOp(new ClearOp());
        AddPendingOp(new PrintOp(
            new List<PrintSegment> { new("fresh", null, null, null, null) },
            button: null));

        var diff = BuildDiff();

        Assert.NotNull(diff);
        Assert.Equal(2, diff!.lineOps.Count);
        Assert.IsType<ClearScreenOp>(diff.lineOps[0]);
        var append = Assert.IsType<AppendLinesOp>(diff.lineOps[1]);
        Assert.Single(append.newLines);
        Assert.Equal("fresh", append.newLines[0].entries[0].segments[0].text);
    }

    [Fact]
    public void Multiple_clearline_ops_accumulate_count()
    {
        PrintLine("a");
        PrintLine("b");
        PrintLine("c");
        PrintLine("d");
        BuildDiff();

        // 同回合 CLEARLINE 1 + CLEARLINE 2 → 累加 clearCount = 3
        _console.DisplayLineList.RemoveRange(_console.DisplayLineList.Count - 3, 3); // 剩 "a"
        AddPendingOp(new ClearLineOp(1));
        AddPendingOp(new ClearLineOp(2));

        var diff = BuildDiff();

        Assert.NotNull(diff);
        var op = Assert.Single(diff!.lineOps);
        var clearLine = Assert.IsType<ClearLineDiffOp>(op);
        Assert.Equal(3, clearLine.clearCount);
    }

    [Fact]
    public void Clearline_then_reprint_fewer_lines_produces_clear_plus_append()
    {
        PrintLine("l1");
        PrintLine("l2");
        PrintLine("l3");
        BuildDiff();

        // CLEARLINE 2 后只重印 1 行（reprint 不足 N 行）
        _console.DisplayLineList.RemoveRange(_console.DisplayLineList.Count - 2, 2); // 剩 "l1"
        _console.DisplayLineList.Add(Line("r1"));
        AddPendingOp(new ClearLineOp(2));
        AddPendingOp(new PrintOp(
            new List<PrintSegment> { new("r1", null, null, null, null) },
            button: null));

        var diff = BuildDiff();

        Assert.NotNull(diff);
        Assert.Equal(2, diff!.lineOps.Count);
        var clearLine = Assert.IsType<ClearLineDiffOp>(diff.lineOps[0]);
        Assert.Equal(2, clearLine.clearCount);
        var append = Assert.IsType<AppendLinesOp>(diff.lineOps[1]);
        Assert.Single(append.newLines);
        Assert.Equal("r1", append.newLines[0].entries[0].segments[0].text);
    }

    private sealed class NullTerminalSetup : ITerminalSetup
    {
        public bool IsAnsiEnabled => false;
        public bool TryEnableAnsi() => false;
        public bool TrySetConsoleSize(int cols, int rows) => false;
        public string? DetectFont() => null;
        public bool TryPrepareVtInput() => false;
    }
}
