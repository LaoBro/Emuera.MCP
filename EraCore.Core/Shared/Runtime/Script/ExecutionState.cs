using MinorShift.Emuera.GameProc.Function;
using MinorShift.Emuera.Runtime.Script.Statements;
using MinorShift.Emuera.Runtime.Script.Statements.Expression;
using System.Collections.Generic;

namespace MinorShift.Emuera.Runtime.Script;

internal sealed class ExecutionState
{
	public bool skipPrint { get; set; }
	public bool isCTrain { get; set; }
	public List<long> coms { get; set; } = [];
	public int count { get; set; }
	public long doTrainSelectCom { get; set; } = -1;
	public long systemResult { get; set; }
	public long flowinputDef { get; set; }
	public bool flowinput { get; set; }
	public bool flowinputCanSkip { get; set; }
	public string flowinputDefString { get; set; } = "";
	public bool flowinputString { get; set; }
	public bool flowinputForceSkip { get; set; }
	/// <summary>shared: read by SystemProc.beginTitle to check load errors</summary>
	public bool noError { get; set; }
	public IProcessState CurrentState { get; set; } = null!;

	// ADR-0011 Phase 2：prev-state 栈原为 ScriptProc 私有，SystemProc 经 Process 反向引用触发。
	// 收掉该反向引用后迁入 ExecutionState——它本就是两模块共享的可变执行上下文。
	/// <summary>已保存的上一状态栈（SAVEGAME 取消 / LOADGAME 回 100 等场景回滚用）。</summary>
	public List<IProcessState> prevStateList { get; set; } = [];

	internal void DeletePrevState()
	{
		if (prevStateList.Count == 0)
			return;
		prevStateList.RemoveAt(prevStateList.Count - 1);
	}

	internal void DeleteAllPrevState()
	{
		foreach (IProcessState state in prevStateList)
			state.ClearFunctionList();
		prevStateList.Clear();
	}

	internal void SaveCurrentState(bool single)
	{
		if (CurrentState != null)
		{
			prevStateList.Add(CurrentState);
			CurrentState = CurrentState.Clone();
		}
	}

	internal void LoadPrevState()
	{
		if (CurrentState != null)
		{
			CurrentState.ClearFunctionList();
			CurrentState = prevStateList[prevStateList.Count - 1];
			DeletePrevState();
		}
	}
}
