using System;
using System.Collections.Generic;
using MinorShift.Emuera;
using MinorShift.Emuera.GameView;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Terminal.Platform;
using MinorShift.Emuera.UI.Game;
using Xunit;

namespace Emuera.Headless.Tests;

/// <summary>
/// 2.2 按钮匹配索引单测（current_plan/2026.8.3.android-perf.md）。
///
/// ButtonIndex：displayLineList 中 Generation==当前代 的按钮按 int/string 值建字典，
/// 输入匹配（ConsoleInputHandler 的 IntButton/StrButton 分支）O(1) 命中；失效双信号
/// （行结构 Invalidate / lastButtonGeneration 代切换）任一变化时惰性重建。
///
/// 等价性对照旧倒序扫描：Generation 单调递增 ⇒ 旧代行后不可能再有当前代按钮，
/// 主扫描等价于「扫全部当前代按钮」；同值多按钮覆盖写入保留最新行；
/// div 逃逸按钮（escapedParts）不在此索引（匹配回退路径保留原扫描）。
///
/// TestButtonFactory 按钮 Generation=0（null console），测试以 generation 参数模拟新旧代。
/// </summary>
public class ButtonIndexTests : IDisposable
{
    private readonly IDisposable _scope;
    private readonly EmueraConsole _console;

    public ButtonIndexTests()
    {
        _scope = GlobalStatic.OpenScope(new ConfigData());
        _console = new EmueraConsole(new HeadlessConsole(), new NullTerminalSetup());
    }

    public void Dispose() => _scope.Dispose();

    private static ConsoleDisplayLine Line(params ConsoleButtonString[] buttons) =>
        new(buttons, isLogical: true, temporary: false);

    /// <summary>反射设置私有 set 的 Generation（TestButtonFactory 恒为 0，构造非 0 代按钮用）。</summary>
    private static void SetGeneration(ConsoleButtonString button, long generation) =>
        typeof(ConsoleButtonString).GetProperty("Generation")!.SetValue(button, generation);

    /// <summary>模拟 ConsolePrintManager.AddDisplayLine 的行追加 + 索引失效。</summary>
    private void AddLine(params ConsoleButtonString[] buttons)
    {
        _console._state.displayLineList.Add(Line(buttons));
        _console._state.buttonIndex.Invalidate();
    }

    // ==================== ButtonIndex 类单测 ====================

    [Fact]
    public void Index_builds_int_and_string_buttons()
    {
        var index = new ButtonIndex();
        var lines = new List<ConsoleDisplayLine>
        {
            Line(TestButtonFactory.CreateButton("1", 1), TestButtonFactory.CreateStringButton("はい", "はい")),
            Line(TestButtonFactory.CreateButton("2", 2)),
        };

        index.EnsureSynced(lines, generation: 0);

        Assert.True(index.TryGetInteger(1, out _));
        Assert.True(index.TryGetInteger(2, out _));
        Assert.False(index.TryGetInteger(3, out _));
        // int 按钮也按 Inputs（=Input.ToString()）进字符串索引（StrButton 分支语义）
        Assert.True(index.TryGetString("1", out _));
        Assert.True(index.TryGetString("はい", out _));
        Assert.False(index.TryGetString("nope", out _));
    }

    [Fact]
    public void Index_filters_old_generation_buttons()
    {
        var index = new ButtonIndex();
        var lines = new List<ConsoleDisplayLine>
        {
            Line(TestButtonFactory.CreateButton("1", 1)),
            Line(TestButtonFactory.CreateStringButton("はい", "はい")),
        };

        // 按钮 Gen=0（TestButtonFactory）≠ generation=1 → 全部视为旧代，过滤
        index.EnsureSynced(lines, generation: 1);

        Assert.False(index.TryGetInteger(1, out _));
        Assert.False(index.TryGetString("はい", out _));
    }

    [Fact]
    public void Index_ignores_non_buttons()
    {
        var index = new ButtonIndex();
        var lines = new List<ConsoleDisplayLine>
        {
            Line(TestButtonFactory.CreateNonButton("text")),
        };

        index.EnsureSynced(lines, generation: 0);

        Assert.False(index.TryGetString("text", out _));
    }

    [Fact]
    public void Index_handles_empty_button_lines()
    {
        var index = new ButtonIndex();
        var lines = new List<ConsoleDisplayLine>
        {
            new(Array.Empty<ConsoleButtonString>(), isLogical: true, temporary: false),
            Line(TestButtonFactory.CreateButton("1", 1)),
        };

        index.EnsureSynced(lines, generation: 0);

        Assert.True(index.TryGetInteger(1, out _));
    }

    [Fact]
    public void Index_keeps_latest_button_for_duplicate_values()
    {
        var index = new ButtonIndex();
        var older = TestButtonFactory.CreateButton("A", 5);
        var newer = TestButtonFactory.CreateButton("B", 5);
        var lines = new List<ConsoleDisplayLine> { Line(older), Line(newer) };

        index.EnsureSynced(lines, generation: 0);

        // 同值多按钮（同一代多行同 Input）：旧逻辑倒序命中最新行 → 索引覆盖保留最新
        Assert.True(index.TryGetInteger(5, out var btn));
        Assert.Same(newer, btn);
    }

    [Fact]
    public void Index_matches_int_button_by_both_str_spellings()
    {
        var index = new ButtonIndex();
        // 4 参构造（HtmlManager HTML 按钮）：Input=5 但 Inputs="05"——
        // 旧 StrButton 条件 (IsInteger && Input.ToString()==str) || Inputs==str 两种拼写都命中
        var lines = new List<ConsoleDisplayLine>
        {
            Line(TestButtonFactory.CreateButtonWithInputs("5", input: 5, inputs: "05")),
        };

        index.EnsureSynced(lines, generation: 0);

        Assert.True(index.TryGetString("5", out _));   // Input.ToString() 拼写
        Assert.True(index.TryGetString("05", out _));  // Inputs 拼写
    }

    [Fact]
    public void Index_accepts_current_gen_button_in_mixed_generation_line()
    {
        var index = new ButtonIndex();
        // ChangeStr 合并产生的混合代行：[旧代按钮(Gen=0), 当前代按钮(Gen=1)] 同一行。
        // 旧逻辑数组正序遍历遇数组内旧代按钮即停（连当前代按钮也拒绝）；索引按 Gen
        // 过滤后仍接受当前代按钮——差异方向「新更宽容」，见 ButtonIndex 类注释。
        var oldBtn = TestButtonFactory.CreateButton("1", 1);       // Gen=0
        var curBtn = TestButtonFactory.CreateButton("2", 2);
        SetGeneration(curBtn, 1);                                  // Gen=1
        var lines = new List<ConsoleDisplayLine> { Line(oldBtn, curBtn) };

        index.EnsureSynced(lines, generation: 1);

        Assert.True(index.TryGetInteger(2, out _)); // 当前代按钮可命中
        Assert.False(index.TryGetInteger(1, out _)); // 旧代按钮过滤
    }

    [Fact]
    public void Index_stays_stale_until_invalidate()
    {
        var index = new ButtonIndex();
        var btn1 = TestButtonFactory.CreateButton("1", 1);
        var btn2 = TestButtonFactory.CreateButton("2", 2);
        var lines = new List<ConsoleDisplayLine> { Line(btn1) };

        index.EnsureSynced(lines, generation: 0);
        Assert.True(index.TryGetInteger(1, out _));

        // 行结构变化但未 Invalidate → 索引保持陈旧（Invalidate 契约：结构变更必须显式失效）
        lines.Add(Line(btn2));
        index.EnsureSynced(lines, generation: 0);
        Assert.False(index.TryGetInteger(2, out _));
        Assert.True(index.TryGetInteger(1, out _));

        // Invalidate 后重建 → 新按钮可见
        index.Invalidate();
        index.EnsureSynced(lines, generation: 0);
        Assert.True(index.TryGetInteger(2, out _));
    }

    [Fact]
    public void Index_rebuilds_after_invalidate_removes_deleted_lines()
    {
        var index = new ButtonIndex();
        var btn1 = TestButtonFactory.CreateButton("1", 1);
        var btn2 = TestButtonFactory.CreateButton("2", 2);
        var lines = new List<ConsoleDisplayLine> { Line(btn1), Line(btn2) };

        index.EnsureSynced(lines, generation: 0);
        Assert.True(index.TryGetInteger(1, out _));

        // 模拟 MaxLog 裁剪 / DeleteLine：删除行 + Invalidate（ConsolePrintManager 契约）
        lines.RemoveAt(0);
        index.Invalidate();
        index.EnsureSynced(lines, generation: 0);

        Assert.False(index.TryGetInteger(1, out _));
        Assert.True(index.TryGetInteger(2, out _));
    }

    [Fact]
    public void Index_rebuilds_on_generation_change_even_without_invalidate()
    {
        var index = new ButtonIndex();
        var btn1 = TestButtonFactory.CreateButton("1", 1);
        var btn2 = TestButtonFactory.CreateButton("2", 2);
        var lines = new List<ConsoleDisplayLine> { Line(btn1), Line(btn2) };

        index.EnsureSynced(lines, generation: 0);
        Assert.True(index.TryGetInteger(1, out _));

        // 代切换（ADR-0018 lastButtonGeneration 失效机制）：即使版本未变也重建，
        // 旧代（Gen=0）按钮被过滤——旧逻辑在 last 变化后同样不再命中旧代按钮
        index.EnsureSynced(lines, generation: 1);
        Assert.False(index.TryGetInteger(1, out _));
        Assert.False(index.TryGetInteger(2, out _));
    }

    // ==================== ConsoleInputHandler 集成 ====================

    [Fact]
    public void FindIntegerButton_hits_current_generation()
    {
        AddLine(TestButtonFactory.CreateButton("1", 1), TestButtonFactory.CreateButton("2", 2));

        Assert.True(_console._inputHandler.FindIntegerButton(1));
        Assert.True(_console._inputHandler.FindIntegerButton(2));
        Assert.False(_console._inputHandler.FindIntegerButton(9));
    }

    [Fact]
    public void FindIntegerButton_filters_old_generation()
    {
        AddLine(TestButtonFactory.CreateButton("1", 1));
        // 按钮 Gen=0（TestButtonFactory）；last 推进到 1 → 旧代，不命中
        _console._state.lastButtonGeneration = 1;

        Assert.False(_console._inputHandler.FindIntegerButton(1));
    }

    [Fact]
    public void FindIntegerButton_reflects_line_removal_after_invalidate()
    {
        var line = Line(TestButtonFactory.CreateButton("1", 1));
        _console._state.displayLineList.Add(line);
        _console._state.buttonIndex.Invalidate();
        Assert.True(_console._inputHandler.FindIntegerButton(1));

        // 行被裁剪（MaxLog / CLEARLINE 场景）→ ConsolePrintManager 会 Invalidate
        _console._state.displayLineList.Remove(line);
        _console._state.buttonIndex.Invalidate();
        Assert.False(_console._inputHandler.FindIntegerButton(1));
    }

    [Fact]
    public void FindStringButton_hits_string_and_int_buttons()
    {
        AddLine(TestButtonFactory.CreateButton("1", 1), TestButtonFactory.CreateStringButton("はい", "はい"));

        Assert.True(_console._inputHandler.FindStringButton("はい"));
        Assert.True(_console._inputHandler.FindStringButton("1")); // int 按钮按 Inputs 匹配
        Assert.False(_console._inputHandler.FindStringButton("nope"));
    }

    [Fact]
    public void FindStringButton_hits_html_int_button_both_spellings()
    {
        // 4 参构造（HtmlManager HTML 按钮）：Input=5 / Inputs="05"
        AddLine(TestButtonFactory.CreateButtonWithInputs("5", input: 5, inputs: "05"));

        Assert.True(_console._inputHandler.FindStringButton("5"));  // Input.ToString() 拼写
        Assert.True(_console._inputHandler.FindStringButton("05")); // Inputs 拼写
        Assert.True(_console._inputHandler.FindIntegerButton(5));   // int 路径不受影响
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
