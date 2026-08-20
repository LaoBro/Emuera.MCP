using System.Collections.Generic;
using MinorShift.Emuera.Runtime.Script.Statements;
using MinorShift.Emuera.Runtime.Script.Statements.Expression;
using MinorShift.Emuera.Runtime.Script.Statements.Function;
using Xunit;

namespace MinorShift.Emuera.Tests;

/// <summary>
/// Issue 00 特性测试：FunctionMethodTerm.Restructure 常量折叠路径。
/// 覆盖默认路径（全常量可折叠 / 不可折叠）与 UniqueRestructure 路径（成功 / 放弃）。
/// </summary>
public sealed class FunctionMethodTermRestructureTests
{
    private sealed class ConstMethod : FunctionMethod
    {
        private readonly long _value;
        private readonly bool _uniqueResult;

        public ConstMethod(long value, bool canRestructure = true, bool hasUniqueRestructure = false, bool uniqueResult = false)
        {
            ReturnType = typeof(long);
            _value = value;
            CanRestructure = canRestructure;
            HasUniqueRestructure = hasUniqueRestructure;
            _uniqueResult = uniqueResult;
        }

        public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments) => _value;

        public override bool UniqueRestructure(ExpressionMediator exm, List<AExpression> arguments) => _uniqueResult;
    }

    [Fact]
    public void Restructure_all_const_args_folds_to_single_term()
    {
        var term = new FunctionMethodTerm(new ConstMethod(42), new List<AExpression> { new SingleLongTerm(1) });

        var result = Assert.IsType<SingleLongTerm>(term.Restructure(null!));

        Assert.Equal(42, result.Int);
    }

    [Fact]
    public void Restructure_not_can_restructure_returns_self()
    {
        var term = new FunctionMethodTerm(new ConstMethod(42, canRestructure: false), new List<AExpression> { new SingleLongTerm(1) });

        Assert.Same(term, term.Restructure(null!));
    }

    [Fact]
    public void Restructure_unique_restructure_true_folds_to_single_term()
    {
        var term = new FunctionMethodTerm(
            new ConstMethod(7, hasUniqueRestructure: true, uniqueResult: true),
            new List<AExpression> { new SingleLongTerm(1) });

        var result = Assert.IsType<SingleLongTerm>(term.Restructure(null!));

        Assert.Equal(7, result.Int);
    }

    [Fact]
    public void Restructure_unique_restructure_false_returns_self()
    {
        var term = new FunctionMethodTerm(
            new ConstMethod(7, hasUniqueRestructure: true, uniqueResult: false),
            new List<AExpression> { new SingleLongTerm(1) });

        Assert.Same(term, term.Restructure(null!));
    }
}
