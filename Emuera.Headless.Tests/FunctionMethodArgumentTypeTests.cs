using System;
using System.Collections.Generic;
using MinorShift.Emuera.GameData.Variable;
using MinorShift.Emuera.Runtime.Script.Statements.Expression;
using MinorShift.Emuera.Runtime.Script.Statements.Function;
using MinorShift.Emuera.Runtime.Script.Statements.Variable;
using Xunit;

namespace MinorShift.Emuera.Tests;

/// <summary>
/// Issue 00 特性测试：内置函数参数系统（FunctionMethod.CheckArgumentType / _ArgType.ArrayDims）。
/// 其中 Array3D / RefInt3D / RefString3D / RefAny3D 的维度判定在修复前为红。
/// </summary>
public sealed class FunctionMethodArgumentTypeTests
{
    private sealed class TestMethod : FunctionMethod
    {
        public TestMethod(string[] argSpecs, int omitStart = -1, bool matchVariadicGroup = false)
        {
            ReturnType = typeof(long);
            argumentTypeArrayEx = [BuildRule(argSpecs, omitStart, matchVariadicGroup)];
        }

        public TestMethod(string[][] ruleSpecs)
        {
            ReturnType = typeof(long);
            var rules = new ArgTypeList[ruleSpecs.Length];
            for (var i = 0; i < ruleSpecs.Length; i++)
                rules[i] = BuildRule(ruleSpecs[i], -1, false);
            argumentTypeArrayEx = rules;
        }

        public static int ArrayDimsOf(string spec) => Parse(spec).ArrayDims;

        private static ArgTypeList BuildRule(string[] specs, int omitStart, bool matchVariadicGroup)
        {
            var rule = new ArgTypeList { OmitStart = omitStart, MatchVariadicGroup = matchVariadicGroup };
            foreach (var spec in specs)
                rule.ArgTypes.Add(Parse(spec));
            return rule;
        }

        private static _ArgType Parse(string spec)
        {
            var type = ArgType.Invalid;
            foreach (var part in spec.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                type |= part switch
                {
                    "Any" => ArgType.Any,
                    "Int" => ArgType.Int,
                    "String" => ArgType.String,
                    "Ref" => ArgType.Ref,
                    "Array" => ArgType.Array,
                    "Array1D" => ArgType.Array1D,
                    "Array2D" => ArgType.Array2D,
                    "Array3D" => ArgType.Array3D,
                    "Variadic" => ArgType.Variadic,
                    "SameAsFirst" => ArgType.SameAsFirst,
                    "CharacterData" => ArgType.CharacterData,
                    "AllowConstRef" => ArgType.AllowConstRef,
                    "DisallowVoid" => ArgType.DisallowVoid,
                    "RefInt" => ArgType.RefInt,
                    "RefAny" => ArgType.RefAny,
                    "RefString" => ArgType.RefString,
                    "RefAnyArray" => ArgType.RefAnyArray,
                    "RefIntArray" => ArgType.RefIntArray,
                    "RefStringArray" => ArgType.RefStringArray,
                    "RefAny1D" => ArgType.RefAny1D,
                    "RefInt1D" => ArgType.RefInt1D,
                    "RefString1D" => ArgType.RefString1D,
                    "RefAny2D" => ArgType.RefAny2D,
                    "RefInt2D" => ArgType.RefInt2D,
                    "RefString2D" => ArgType.RefString2D,
                    "RefAny3D" => ArgType.RefAny3D,
                    "RefInt3D" => ArgType.RefInt3D,
                    "RefString3D" => ArgType.RefString3D,
                    "VariadicAny" => ArgType.VariadicAny,
                    "VariadicInt" => ArgType.VariadicInt,
                    "VariadicString" => ArgType.VariadicString,
                    "VariadicSameAsFirst" => ArgType.VariadicSameAsFirst,
                    _ => throw new ArgumentException($"unknown ArgType spec: {part}")
                };
            }
            return new _ArgType(type);
        }
    }

    private sealed class StubArrayVariableToken(VariableCode code) : VariableToken(code, null!)
    {
    }

    private static VariableTerm ArrayVar(VariableCode code) => new(new StubArrayVariableToken(code), Array.Empty<AExpression>());

    // ---------- ArrayDims（bug：Array3D 误判为 Array2D） ----------

    [Fact]
    public void ArrayDimsOf_Array3D_returns_3()
    {
        Assert.Equal(3, TestMethod.ArrayDimsOf("Array3D"));
        Assert.Equal(3, TestMethod.ArrayDimsOf("RefInt3D"));
        Assert.Equal(3, TestMethod.ArrayDimsOf("RefString3D"));
        Assert.Equal(3, TestMethod.ArrayDimsOf("RefAny3D"));
    }

    [Fact]
    public void ArrayDimsOf_other_dimensions_keep_current_behavior()
    {
        Assert.Equal(0, TestMethod.ArrayDimsOf("Int"));
        Assert.Equal(0, TestMethod.ArrayDimsOf("RefInt"));
        Assert.Equal(-1, TestMethod.ArrayDimsOf("Array"));
        Assert.Equal(1, TestMethod.ArrayDimsOf("Array1D"));
        Assert.Equal(2, TestMethod.ArrayDimsOf("Array2D"));
        Assert.Equal(1, TestMethod.ArrayDimsOf("RefInt1D"));
        Assert.Equal(2, TestMethod.ArrayDimsOf("RefInt2D"));
    }

    // ---------- 固定参数 / 类型不匹配 ----------

    [Fact]
    public void CheckArgumentType_fixed_int_string_accepts_matching_args()
    {
        var method = new TestMethod(["Int", "String"]);
        var args = new List<AExpression> { new SingleLongTerm(1), new SingleStrTerm("x") };

        Assert.Null(method.CheckArgumentType("TEST", args));
    }

    [Fact]
    public void CheckArgumentType_fixed_int_string_rejects_wrong_type()
    {
        var method = new TestMethod(["Int", "String"]);
        var args = new List<AExpression> { new SingleStrTerm("x"), new SingleStrTerm("y") };

        Assert.NotNull(method.CheckArgumentType("TEST", args));
    }

    [Fact]
    public void CheckArgumentType_fixed_args_rejects_wrong_count()
    {
        var method = new TestMethod(["Int", "String"]);
        var tooFew = new List<AExpression> { new SingleLongTerm(1) };
        var tooMany = new List<AExpression> { new SingleLongTerm(1), new SingleStrTerm("x"), new SingleLongTerm(2) };

        Assert.NotNull(method.CheckArgumentType("TEST", tooFew));
        Assert.NotNull(method.CheckArgumentType("TEST", tooMany));
    }

    // ---------- 省略参数 ----------

    [Fact]
    public void CheckArgumentType_omit_start_allows_omitted_tail_args()
    {
        var method = new TestMethod(["String", "Int"], omitStart: 1);
        var oneArg = new List<AExpression> { new SingleStrTerm("x") };
        var twoArgs = new List<AExpression> { new SingleStrTerm("x"), new SingleLongTerm(1) };

        Assert.Null(method.CheckArgumentType("TEST", oneArg));
        Assert.Null(method.CheckArgumentType("TEST", twoArgs));
    }

    // ---------- 可变参 ----------

    [Fact]
    public void CheckArgumentType_variadic_int_accepts_one_or_many()
    {
        var method = new TestMethod(["Int", "VariadicInt"], omitStart: 1);
        var oneArg = new List<AExpression> { new SingleLongTerm(1) };
        var manyArgs = new List<AExpression> { new SingleLongTerm(1), new SingleLongTerm(2), new SingleLongTerm(3) };

        Assert.Null(method.CheckArgumentType("TEST", oneArg));
        Assert.Null(method.CheckArgumentType("TEST", manyArgs));
    }

    [Fact]
    public void CheckArgumentType_variadic_int_rejects_wrong_tail_type()
    {
        var method = new TestMethod(["Int", "VariadicInt"], omitStart: 1);
        var args = new List<AExpression> { new SingleLongTerm(1), new SingleStrTerm("x") };

        Assert.NotNull(method.CheckArgumentType("TEST", args));
    }

    // ---------- SameAsFirst ----------

    [Fact]
    public void CheckArgumentType_same_as_first_accepts_same_type_and_rejects_different_type()
    {
        var method = new TestMethod(["Any", "SameAsFirst"]);
        var sameType = new List<AExpression> { new SingleLongTerm(1), new SingleLongTerm(2) };
        var differentType = new List<AExpression> { new SingleLongTerm(1), new SingleStrTerm("x") };

        Assert.Null(method.CheckArgumentType("TEST", sameType));
        Assert.NotNull(method.CheckArgumentType("TEST", differentType));
    }

    // ---------- DisallowVoid ----------

    [Fact]
    public void CheckArgumentType_disallow_void_rejects_null_in_omittable_tail()
    {
        var method = new TestMethod(["String", "Int|DisallowVoid"], omitStart: 1);
        var nullTail = new List<AExpression> { new SingleStrTerm("x"), null! };

        Assert.NotNull(method.CheckArgumentType("TEST", nullTail));
    }

    // ---------- 多规则选择 ----------

    [Fact]
    public void CheckArgumentType_multiple_rules_selects_matching_rule()
    {
        var method = new TestMethod([new[] { "Int", "String" }, new[] { "String", "Int" }]);
        var matchesSecond = new List<AExpression> { new SingleStrTerm("x"), new SingleLongTerm(1) };
        var matchesNone = new List<AExpression> { new SingleStrTerm("x"), new SingleStrTerm("y") };

        Assert.Null(method.CheckArgumentType("TEST", matchesSecond));
        Assert.NotNull(method.CheckArgumentType("TEST", matchesNone));
    }

    // ---------- 三维数组参数（修复前为红） ----------

    [Fact]
    public void CheckArgumentType_RefInt3D_accepts_3D_int_array_variable()
    {
        var method = new TestMethod(["RefInt3D"]);
        var args = new List<AExpression> { ArrayVar(VariableCode.__INTEGER__ | VariableCode.__ARRAY_3D__) };

        Assert.Null(method.CheckArgumentType("TEST", args));
    }

    [Fact]
    public void CheckArgumentType_RefInt3D_rejects_scalar_1D_and_2D_int_variables()
    {
        var method = new TestMethod(["RefInt3D"]);

        Assert.NotNull(method.CheckArgumentType("TEST", new List<AExpression> { ArrayVar(VariableCode.__INTEGER__) }));
        Assert.NotNull(method.CheckArgumentType("TEST", new List<AExpression> { ArrayVar(VariableCode.__INTEGER__ | VariableCode.__ARRAY_1D__) }));
        Assert.NotNull(method.CheckArgumentType("TEST", new List<AExpression> { ArrayVar(VariableCode.__INTEGER__ | VariableCode.__ARRAY_2D__) }));
    }

    [Fact]
    public void CheckArgumentType_RefString3D_accepts_3D_and_rejects_2D_string_variables()
    {
        var method = new TestMethod(["RefString3D"]);

        Assert.Null(method.CheckArgumentType("TEST", new List<AExpression> { ArrayVar(VariableCode.__STRING__ | VariableCode.__ARRAY_3D__) }));
        Assert.NotNull(method.CheckArgumentType("TEST", new List<AExpression> { ArrayVar(VariableCode.__STRING__ | VariableCode.__ARRAY_2D__) }));
    }

    [Fact]
    public void CheckArgumentType_RefAny3D_accepts_3D_and_rejects_scalar()
    {
        var method = new TestMethod(["RefAny3D"]);

        Assert.Null(method.CheckArgumentType("TEST", new List<AExpression> { ArrayVar(VariableCode.__INTEGER__ | VariableCode.__ARRAY_3D__) }));
        Assert.Null(method.CheckArgumentType("TEST", new List<AExpression> { ArrayVar(VariableCode.__STRING__ | VariableCode.__ARRAY_3D__) }));
        Assert.NotNull(method.CheckArgumentType("TEST", new List<AExpression> { ArrayVar(VariableCode.__INTEGER__) }));
    }
}
