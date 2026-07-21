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
}
