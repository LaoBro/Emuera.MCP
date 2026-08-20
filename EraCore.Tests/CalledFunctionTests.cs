using MinorShift.Emuera;
using MinorShift.Emuera.GameData.Variable;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Runtime.Script;
using MinorShift.Emuera.Runtime.Script.Parser;
using MinorShift.Emuera.Runtime.Script.Statements;
using MinorShift.Emuera.Runtime.Script.Statements.Expression;
using MinorShift.Emuera.Runtime.Script.Statements.Variable;
using Xunit;

namespace MinorShift.Emuera.Tests;

/// <summary>
/// ADR-0011 / Issue 04：CalledFunction 隔离单测。
/// 直接以显式依赖（FunctionLabelLine / 空参数数组）构造，不开启 GlobalStatic scope、
/// 不碰 AsyncLocal ambient，只验证可观察行为（call-return 地址 / Clone / ConvertArg）。
/// 注：CalledFunction 工厂方法已采用依赖注入（传 LabelDictionary），不持 Process 反向引用，属 F2。
/// </summary>
public class CalledFunctionTests
{
    private static FunctionLabelLine NewLabel(string name) => new(null, name, new WordCollection());

    [Fact]
    public void CreateCalledFunctionMethod_sets_TopLabel_CurrentLabel_and_IsEvent_false()
    {
        // FunctionLabelLine 构造依赖 Config（既有耦合），此处开最小 scope。
        using var scope = GlobalStatic.OpenScope(new ConfigData());
        var label = NewLabel("FOO");
        var called = CalledFunction.CreateCalledFunctionMethod(label, "FOO");

        Assert.Same(label, called.TopLabel);
        Assert.Same(label, called.CurrentLabel);
        Assert.False(called.IsEvent);
        Assert.Equal("FOO", called.FunctionName);
    }

    [Fact]
    public void Clone_copies_core_fields()
    {
        using var scope = GlobalStatic.OpenScope(new ConfigData());
        var label = NewLabel("BAR");
        var called = CalledFunction.CreateCalledFunctionMethod(label, "BAR");

        var clone = called.Clone();

        Assert.NotSame(called, clone);
        Assert.Equal("BAR", clone.FunctionName);
        Assert.Same(label, clone.TopLabel);
        Assert.Same(label, clone.CurrentLabel);
        Assert.False(clone.IsEvent);
    }

    [Fact]
    public void ConvertArg_with_no_arguments_returns_transporter_without_error()
    {
        using var scope = GlobalStatic.OpenScope(new ConfigData());
        var label = NewLabel("BAZ");
        label.Arg = System.Array.Empty<VariableTerm>();
        label.Def = System.Array.Empty<SingleTerm>();

        var called = CalledFunction.CreateCalledFunctionMethod(label, "BAZ");

        var result = called.ConvertArg(new System.Collections.Generic.List<AExpression>(), out var errMes);

        Assert.Null(errMes);
        Assert.NotNull(result);
        Assert.Empty(result!.Arguments);
    }

    [Fact]
    public void UpdateRetAddress_sets_return_address()
    {
        using var scope = GlobalStatic.OpenScope(new ConfigData());
        var label = NewLabel("QUX");
        var called = CalledFunction.CreateCalledFunctionMethod(label, "QUX");
        var ret = new LogicalLineFake();

        called.updateRetAddress(ret);

        Assert.Same(ret, called.ReturnAddress);
    }

    private sealed class LogicalLineFake : LogicalLine { }
}
