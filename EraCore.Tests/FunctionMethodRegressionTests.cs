using System;
using System.Collections.Generic;
using System.Linq;
using MinorShift.Emuera.GameData.Function;
using MinorShift.Emuera.GameData.Variable;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Runtime.Script.Statements.Expression;
using MinorShift.Emuera.Runtime.Script.Statements.Variable;
using MinorShift.Emuera.UI.Game.Image;
using Xunit;

namespace MinorShift.Emuera.Tests;

/// <summary>
/// Issue 00 特性测试：内置函数中与本次重构直接相关的回归面。
/// - ARRAYMSORT 极值排序不应溢出（修复前为红）
/// - GraphicsGetColor / GraphicsSetColor 的负 Y 坐标应被拒绝（修复前为红）
/// </summary>
public sealed class FunctionMethodRegressionTests
{
    private sealed class StubArrayVariableToken(VariableCode code, object array) : VariableToken(code, null!)
    {
        private readonly object _array = array;

        public override object GetArray() => _array;
    }

    private static VariableTerm ArrayVar(VariableCode code, object array)
        => new(new StubArrayVariableToken(code, array), Array.Empty<AExpression>());

    [Fact]
    public void ArrayMultiSort_int_extreme_values_sorts_without_overflow()
    {
        using var scope = GlobalStatic.OpenScope(new ConfigData());
        var method = FunctionMethodCreator.GetMethodList()["ARRAYMSORT"];
        var baseArray = new long[] { long.MaxValue, -1, 1 };
        var args = new List<AExpression> { ArrayVar(VariableCode.__INTEGER__ | VariableCode.__ARRAY_1D__, baseArray) };

        var result = method.GetIntValue(null!, args);

        Assert.Equal(1, result);
        Assert.True(baseArray.SequenceEqual(new long[] { -1, 1, long.MaxValue }));
    }

    [Fact]
    public void ArrayMultiSort_string_1D_and_int_2D_reorder_by_sort_index()
    {
        using var scope = GlobalStatic.OpenScope(new ConfigData());
        var method = FunctionMethodCreator.GetMethodList()["ARRAYMSORT"];
        var baseStrings = new string[] { "c", "a", "b" };
        var secondStrings = new string[] { "C", "A", "B" };
        var int2D = new long[,] { { 30, 31 }, { 10, 11 }, { 20, 21 } };
        var args = new List<AExpression>
        {
            ArrayVar(VariableCode.__STRING__ | VariableCode.__ARRAY_1D__, baseStrings),
            ArrayVar(VariableCode.__STRING__ | VariableCode.__ARRAY_1D__, secondStrings),
            ArrayVar(VariableCode.__INTEGER__ | VariableCode.__ARRAY_2D__, int2D),
        };

        var result = method.GetIntValue(null!, args);

        Assert.Equal(1, result);
        Assert.True(baseStrings.SequenceEqual(new string[] { "a", "b", "c" }));
        Assert.True(secondStrings.SequenceEqual(new string[] { "A", "B", "C" }));
        Assert.True(new long[,] { { 10, 11 }, { 20, 21 }, { 30, 31 } }.Cast<long>().SequenceEqual(int2D.Cast<long>()));
    }

    [Fact]
    public void GraphicsGetColor_rejects_negative_y()
    {
        using var scope = GlobalStatic.OpenScope(new ConfigData());
        var g = AppContents.GetGraphics(123456);
        g.GCreate(10, 10, false);
        var method = new FunctionMethodCreator.GraphicsGetColorMethod();
        var args = new List<AExpression> { new SingleLongTerm(123456), new SingleLongTerm(0), new SingleLongTerm(-1) };

        var result = method.GetIntValue(null!, args);

        Assert.Equal(-1, result);
    }

    [Fact]
    public void GraphicsSetColor_rejects_negative_y()
    {
        using var scope = GlobalStatic.OpenScope(new ConfigData());
        var g = AppContents.GetGraphics(123457);
        g.GCreate(10, 10, false);
        var method = new FunctionMethodCreator.GraphicsSetColorMethod();
        var args = new List<AExpression> { new SingleLongTerm(123457), new SingleLongTerm(0xFF000000), new SingleLongTerm(0), new SingleLongTerm(-1) };

        var result = method.GetIntValue(null!, args);

        Assert.Equal(0, result);
    }
}
