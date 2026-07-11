using MinorShift.Emuera.GameProc.Function;
using MinorShift.Emuera.Runtime.Script.Statements;
using MinorShift.Emuera.Runtime.Script.Statements.Expression;

namespace MinorShift.Emuera.Runtime.Script;

/// <summary>
/// ADR-0011 Phase 3：ProcessState 的接口 seam，供 SystemProc/ScriptProc 注入解耦。
/// 仅覆盖两个子系统实际调用的 ProcessState API。
/// </summary>
internal interface IProcessState
{
	SystemStateCode SystemState { get; set; }
	LogicalLine CurrentLine { get; set; }
	bool ScriptEnd { get; }
	bool IsFunctionMethod { get; }
	bool isBegun { get; }
	bool calledWhenNormal { get; set; }
	CalledFunction CurrentCalled { get; }
	void ShiftNextLine();
	void JumpTo(LogicalLine line);
	void Return(long ret);
	void IntoFunction(CalledFunction call, UserDefinedFunctionArgument? srcArgs, ExpressionMediator exm);
	void Begin();
	void ClearFunctionList();
	IProcessState Clone();
}
