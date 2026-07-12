using System;
using System.Collections.Generic;
using System.Reflection;
using MinorShift.Emuera.GameView;
using MinorShift.Emuera.Primitives;
using MinorShift.Emuera.Runtime;
using MinorShift.Emuera.UI.Game;
using Xunit;

namespace Emuera.Headless.Tests;

/// <summary>
/// DisplayState 全量快照序列化单测（ADR-0013 决策二）。
/// 验证 BuildSnapshot 从 displayLineList → DisplaySnapshot 的映射：
/// - 行数一致
/// - 每行 entries[] / segments[] / button（含 col/width）/ align / isLineEnd 正确
/// - bgColor / state / inputType / needValue / protocolVersion 字段正确
///
/// 复用 TestButtonFactory（null console → Generation=0），不依赖真实 EmueraConsole/Config。
/// 通过 internal static DisplayState.BuildSnapshot 直测——无需构造完整 console。
/// </summary>
public class DisplayStateTests
{
    private const int ExpectedProtocolVersion = 3;

    /// <summary>
    /// 通过反射直接设置 ConsoleDisplayLine 的私有 align 字段，绕过 SetAlignment 对 Config.DrawableWidth 的依赖。
    /// 测试仅验证 DisplayState.AlignToString 的映射，不测试 SetAlignment 的位移逻辑。
    /// </summary>
    private static void SetAlign(ConsoleDisplayLine line, DisplayLineAlignment align)
    {
        var field = typeof(ConsoleDisplayLine).GetField("align",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        field!.SetValue(line, align);
    }

    // ---------- 空 displayLineList ----------

    [Fact]
    public void Empty_displayLineList_produces_empty_lines()
    {
        var snapshot = DisplayState.BuildSnapshot(
            displayLineList: new List<ConsoleDisplayLine>(),
            bgColor: EmuColor.Black,
            state: ConsoleState.WaitInput,
            currentRequest: null, "TestFont");

        Assert.Empty(snapshot.lines);
        Assert.Equal("#000000", snapshot.bgColor);
        Assert.Equal("WaitInput", snapshot.state);
        Assert.Null(snapshot.inputType);
        Assert.False(snapshot.needValue);
        Assert.Equal(ExpectedProtocolVersion, snapshot.protocolVersion);
    }

    // ---------- 单行单按钮 ----------

    [Fact]
    public void Single_line_single_button_snapshots_correctly()
    {
        var buttons = new[] { TestButtonFactory.CreateButton("[OK]", input: 1) };
        var line = new ConsoleDisplayLine(buttons, isLogical: true, temporary: false);
        var list = new List<ConsoleDisplayLine> { line };

        var snapshot = DisplayState.BuildSnapshot(
            list, EmuColor.Black, ConsoleState.WaitInput, currentRequest: null, "TestFont");

        Assert.Single(snapshot.lines);
        var snapLine = snapshot.lines[0];
        Assert.Single(snapLine.entries);
        // ConsoleDisplayLine 默认 align=LEFT(0)，AlignToString 映射到 "left"
        Assert.Equal("left", snapLine.align);
        Assert.True(snapLine.isLineEnd);

        var entry = snapLine.entries[0];
        Assert.Single(entry.segments);
        Assert.Equal("[OK]", entry.segments[0].text);
        Assert.NotNull(entry.button);
        Assert.Equal(0, entry.button!.col);
        Assert.Equal(4, entry.button!.width);
        Assert.Equal(1L, (long)entry.button.value);
        Assert.True(entry.button.isInteger);
    }

    // ---------- 多行多按钮（几何累加） ----------

    [Fact]
    public void Multi_line_count_matches_displayLineList_count()
    {
        var line1 = new ConsoleDisplayLine(
            TestButtonFactory.CreateButtons(("[OK]", 1)), isLogical: true, temporary: false);
        var line2 = new ConsoleDisplayLine(
            TestButtonFactory.CreateButtons(("[Cancel]", 2), ("[Exit]", 3)), isLogical: true, temporary: false);
        var line3 = new ConsoleDisplayLine(
            Array.Empty<ConsoleButtonString>(), isLogical: true, temporary: false);
        var list = new List<ConsoleDisplayLine> { line1, line2, line3 };

        var snapshot = DisplayState.BuildSnapshot(
            list, EmuColor.Black, ConsoleState.WaitInput, currentRequest: null, "TestFont");

        Assert.Equal(3, snapshot.lines.Count);
        Assert.Single(snapshot.lines[0].entries);
        Assert.Equal(2, snapshot.lines[1].entries.Count);
        Assert.Empty(snapshot.lines[2].entries);
    }

    [Fact]
    public void Multi_button_geometry_accumulates_within_line()
    {
        // "[OK]"(width=4) + "[Cancel]"(width=8) + "[Exit]"(width=6)
        var buttons = TestButtonFactory.CreateButtons(("[OK]", 1), ("[Cancel]", 2), ("[Exit]", 3));
        var line = new ConsoleDisplayLine(buttons, isLogical: true, temporary: false);
        var list = new List<ConsoleDisplayLine> { line };

        var snapshot = DisplayState.BuildSnapshot(
            list, EmuColor.Black, ConsoleState.WaitInput, currentRequest: null, "TestFont");

        var entries = snapshot.lines[0].entries;
        Assert.Equal(3, entries.Count);

        Assert.Equal(0, entries[0].button!.col);
        Assert.Equal(4, entries[0].button!.width);

        Assert.Equal(4, entries[1].button!.col);
        Assert.Equal(8, entries[1].button!.width);

        Assert.Equal(12, entries[2].button!.col); // 4 + 8
        Assert.Equal(6, entries[2].button!.width);
    }

    // ---------- CJK 双宽字符按钮 ----------

    [Fact]
    public void Cjk_double_width_button_in_snapshot()
    {
        // "確定" = 2 CJK 字符，width=4
        var buttons = new[] { TestButtonFactory.CreateButton("確定", input: 1) };
        var line = new ConsoleDisplayLine(buttons, isLogical: true, temporary: false);
        var list = new List<ConsoleDisplayLine> { line };

        var snapshot = DisplayState.BuildSnapshot(
            list, EmuColor.Black, ConsoleState.WaitInput, currentRequest: null, "TestFont");

        var entry = snapshot.lines[0].entries[0];
        Assert.Equal(0, entry.button!.col);
        Assert.Equal(4, entry.button!.width);
        Assert.Equal("確定", entry.segments[0].text);
    }

    [Fact]
    public void Cjk_then_ascii_buttons_geometry_accumulates_in_snapshot()
    {
        // "確定"(width=4) + "[OK]"(width=4)
        var buttons = TestButtonFactory.CreateButtons(("確定", 1), ("[OK]", 2));
        var line = new ConsoleDisplayLine(buttons, isLogical: true, temporary: false);
        var list = new List<ConsoleDisplayLine> { line };

        var snapshot = DisplayState.BuildSnapshot(
            list, EmuColor.Black, ConsoleState.WaitInput, currentRequest: null, "TestFont");

        var entries = snapshot.lines[0].entries;
        Assert.Equal(0, entries[0].button!.col);
        Assert.Equal(4, entries[0].button!.width);
        Assert.Equal(4, entries[1].button!.col); // 累加 CJK 宽度
        Assert.Equal(4, entries[1].button!.width);
    }

    // ---------- 非按钮文本段（ButtonRef=null） ----------

    [Fact]
    public void Non_button_text_segment_has_null_button()
    {
        var nonButton = TestButtonFactory.CreateNonButton("plain text");
        var line = new ConsoleDisplayLine(new[] { nonButton }, isLogical: true, temporary: false);
        var list = new List<ConsoleDisplayLine> { line };

        var snapshot = DisplayState.BuildSnapshot(
            list, EmuColor.Black, ConsoleState.WaitInput, currentRequest: null, "TestFont");

        var entry = snapshot.lines[0].entries[0];
        Assert.Null(entry.button);
        Assert.Equal("plain text", entry.segments[0].text);
    }

    [Fact]
    public void Mixed_button_and_non_button_geometry_accumulates_through_non_button_in_snapshot()
    {
        // [OK](button, width=4) + plain(non-button, width=10) + [Cancel](button, width=8)
        var buttons = new ConsoleButtonString[]
        {
            TestButtonFactory.CreateButton("[OK]", 1),
            TestButtonFactory.CreateNonButton("plain text"),
            TestButtonFactory.CreateButton("[Cancel]", 2),
        };
        var line = new ConsoleDisplayLine(buttons, isLogical: true, temporary: false);
        var list = new List<ConsoleDisplayLine> { line };

        var snapshot = DisplayState.BuildSnapshot(
            list, EmuColor.Black, ConsoleState.WaitInput, currentRequest: null, "TestFont");

        var entries = snapshot.lines[0].entries;
        Assert.Equal(3, entries.Count);
        Assert.Equal(0, entries[0].button!.col);
        Assert.Equal(4, entries[0].button!.width);
        Assert.Null(entries[1].button);
        Assert.Equal(14, entries[2].button!.col); // 4 + 10
        Assert.Equal(8, entries[2].button!.width);
    }

    // ---------- PRINTN 不完整行（isLineEnd=false） ----------

    [Fact]
    public void Printn_incomplete_line_isLineEnd_false_preserved_in_snapshot()
    {
        // PRINTN 输出后行未结束——IsLineEnd=false
        var buttons = new[] { TestButtonFactory.CreateButton("continuing", input: 1) };
        var line = new ConsoleDisplayLine(buttons, isLogical: true, temporary: false, lineEnd: false);
        var list = new List<ConsoleDisplayLine> { line };

        var snapshot = DisplayState.BuildSnapshot(
            list, EmuColor.Black, ConsoleState.WaitInput, currentRequest: null, "TestFont");

        Assert.False(snapshot.lines[0].isLineEnd);
    }

    [Fact]
    public void Printl_complete_line_isLineEnd_true_preserved_in_snapshot()
    {
        var buttons = new[] { TestButtonFactory.CreateButton("[OK]", input: 1) };
        var line = new ConsoleDisplayLine(buttons, isLogical: true, temporary: false, lineEnd: true);
        var list = new List<ConsoleDisplayLine> { line };

        var snapshot = DisplayState.BuildSnapshot(
            list, EmuColor.Black, ConsoleState.WaitInput, currentRequest: null, "TestFont");

        Assert.True(snapshot.lines[0].isLineEnd);
    }

    // ---------- 对齐（LEFT/CENTER/RIGHT） ----------

    [Fact]
    public void Alignment_left_center_right_mapped_correctly()
    {
        var leftButtons = new[] { TestButtonFactory.CreateNonButton("left") };
        var centerButtons = new[] { TestButtonFactory.CreateNonButton("center") };
        var rightButtons = new[] { TestButtonFactory.CreateNonButton("right") };

        var leftLine = new ConsoleDisplayLine(leftButtons, isLogical: true, temporary: false);
        var centerLine = new ConsoleDisplayLine(centerButtons, isLogical: true, temporary: false);
        var rightLine = new ConsoleDisplayLine(rightButtons, isLogical: true, temporary: false);
        // 默认 align=LEFT(0)；CENTER/RIGHT 通过反射直接设置私有字段，绕过 SetAlignment 的 Config 依赖
        SetAlign(centerLine, DisplayLineAlignment.CENTER);
        SetAlign(rightLine, DisplayLineAlignment.RIGHT);

        var list = new List<ConsoleDisplayLine> { leftLine, centerLine, rightLine };

        var snapshot = DisplayState.BuildSnapshot(
            list, EmuColor.Black, ConsoleState.WaitInput, currentRequest: null, "TestFont");

        Assert.Equal("left", snapshot.lines[0].align);
        Assert.Equal("center", snapshot.lines[1].align);
        Assert.Equal("right", snapshot.lines[2].align);
    }

    // ---------- bgColor / state / inputType / needValue / protocolVersion ----------

    [Fact]
    public void BgColor_serialized_as_hex_string()
    {
        var snapshot = DisplayState.BuildSnapshot(
            new List<ConsoleDisplayLine>(),
            bgColor: EmuColor.FromArgb(0, 0, 139), // DarkBlue
            state: ConsoleState.WaitInput,
            currentRequest: null, "TestFont");

        Assert.Equal("#00008B", snapshot.bgColor);
    }

    [Fact]
    public void State_serialized_as_string_name()
    {
        foreach (ConsoleState s in new[] { ConsoleState.WaitInput, ConsoleState.Quit, ConsoleState.Error, ConsoleState.Running })
        {
            var snapshot = DisplayState.BuildSnapshot(
                new List<ConsoleDisplayLine>(), EmuColor.Black, s, currentRequest: null, "TestFont");
            Assert.Equal(s.ToString(), snapshot.state);
        }
    }

    [Fact]
    public void CurrentRequest_intValue_provides_inputType_and_needValue_true()
    {
        var req = new InputRequest { InputType = InputType.IntValue };

        var snapshot = DisplayState.BuildSnapshot(
            new List<ConsoleDisplayLine>(), EmuColor.Black, ConsoleState.WaitInput, req, "TestFont");

        Assert.Equal("IntValue", snapshot.inputType);
        Assert.True(snapshot.needValue);
    }

    [Fact]
    public void CurrentRequest_enterKey_provides_needValue_false()
    {
        var req = new InputRequest { InputType = InputType.EnterKey };

        var snapshot = DisplayState.BuildSnapshot(
            new List<ConsoleDisplayLine>(), EmuColor.Black, ConsoleState.WaitInput, req, "TestFont");

        Assert.Equal("EnterKey", snapshot.inputType);
        Assert.False(snapshot.needValue);
    }

    [Fact]
    public void Null_currentRequest_yields_null_inputType_and_false_needValue()
    {
        var snapshot = DisplayState.BuildSnapshot(
            new List<ConsoleDisplayLine>(), EmuColor.Black, ConsoleState.WaitInput, currentRequest: null, "TestFont");

        Assert.Null(snapshot.inputType);
        Assert.False(snapshot.needValue);
    }

    [Fact]
    public void ProtocolVersion_is_three()
    {
        var snapshot = DisplayState.BuildSnapshot(
            new List<ConsoleDisplayLine>(), EmuColor.Black, ConsoleState.WaitInput, currentRequest: null, "TestFont");

        Assert.Equal(3, snapshot.protocolVersion);
    }
}
