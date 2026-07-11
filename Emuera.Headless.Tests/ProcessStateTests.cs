using MinorShift.Emuera;
using MinorShift.Emuera.GameView;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Runtime.Script;
using MinorShift.Emuera.Runtime.Script.Parser;
using MinorShift.Emuera.Runtime.Script.Statements;
using Xunit;

namespace MinorShift.Emuera.Tests;

/// <summary>
/// ADR-0011 / Issue 03：ProcessState 隔离单测。
/// 直接以 fake EmueraConsole（DebugMode 下才使用，测试默认 false 故传 null）构造，
/// 不开启 GlobalStatic scope、不碰 AsyncLocal ambient，只验证可观察状态。
/// </summary>
public class ProcessStateTests
{
    // LogicalLine 为抽象类且无抽象成员，测试内造一个最小具体行。
    private sealed class FakeLine : LogicalLine { }

    private static ProcessState NewState() => new(null!);

    [Fact]
    public void ShiftNextLine_advances_to_NextLine_and_increments_lineCount()
    {
        var state = NewState();
        var a = new FakeLine();
        var b = new FakeLine();
        a.NextLine = b;
        state.CurrentLine = a;
        var before = state.lineCount;

        state.ShiftNextLine();

        Assert.Same(b, state.CurrentLine);
        Assert.Equal(before + 1, state.lineCount);
    }

    [Fact]
    public void JumpTo_sets_CurrentLine_and_increments_lineCount()
    {
        var state = NewState();
        var target = new FakeLine();
        var before = state.lineCount;

        state.JumpTo(target);

        Assert.Same(target, state.CurrentLine);
        Assert.Equal(before + 1, state.lineCount);
    }

    [Fact]
    public void ScriptEnd_is_true_when_no_function_on_stack()
    {
        var state = NewState();
        Assert.True(state.ScriptEnd);
    }

    [Fact]
    public void Begin_TITLE_transitions_State_to_Title_Begin()
    {
        var state = NewState();
        state.Begin(BeginType.TITLE);
        Assert.Equal(SystemStateCode.Title_Begin, state.SystemState);
        Assert.False(state.isBegun); // Begin() 末尾将 begintype 复位为 NULL
    }

    [Fact]
    public void Clone_copies_execution_state_fields()
    {
        var state = NewState();
        var line = new FakeLine();
        state.CurrentLine = line;
        state.SystemState = SystemStateCode.Train_Begin;
        state.Begin(BeginType.TRAIN);

        var clone = state.Clone();

        Assert.NotSame(state, clone);
        Assert.True(clone.IsClone);
        Assert.Same(line, clone.CurrentLine);
        Assert.Equal(SystemStateCode.Train_Begin, clone.SystemState);
    }

    [Fact]
    public void IntoFunction_pushes_and_Return_pops_the_call_frame()
    {
        // 注：FunctionLabelLine 构造依赖 Config（privateVar 字典用 Config.Config.StrComper），
        // 此为 FunctionLabelLine 既有耦合，非本重构引入；函数栈 push/pop 行为本身不依赖
        // GlobalStatic 引擎字段。故仅此处开最小 Config scope。
        using var scope = GlobalStatic.OpenScope(new ConfigData());
        var state = NewState();
        var label = new FunctionLabelLine(null, "TESTFUNC", new WordCollection());
        var called = CalledFunction.CreateCalledFunctionMethod(label, "TESTFUNC");

        Assert.True(state.ScriptEnd); // 空栈
        state.IntoFunction(called, null!, null!);
        Assert.False(state.ScriptEnd); // 已压入一帧
        Assert.Same(label, state.CurrentLine);

        state.Return(0);
        Assert.True(state.ScriptEnd); // 帧已弹出
        Assert.Null(state.CurrentLine);
    }
}
