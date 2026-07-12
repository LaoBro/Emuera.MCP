using MinorShift.Emuera.GameData.Variable;
using MinorShift.Emuera.GameProc;
using MinorShift.Emuera.GameProc.Function;
using MinorShift.Emuera.GameView;
using MinorShift.Emuera.Runtime.Script;
using MinorShift.Emuera.Runtime.Script.Statements;
using MinorShift.Emuera.Runtime.Script.Statements.Expression;
using MinorShift.Emuera.Runtime.Script.Statements.Variable;
using MinorShift.Emuera.Runtime.Utils;
using Xunit;

namespace MinorShift.Emuera.Tests;

/// <summary>
/// Issue 07：ScriptProc 已升为独立顶层类，构造注入 ExecutionState / IVariableEvaluator；
/// 以下测试不开启 GlobalStatic scope、不碰 AsyncLocal ambient，只验证 seam 可达。
/// </summary>
public sealed class ScriptProcTests
{
    sealed class FakeProcessState : IProcessState
    {
        public SystemStateCode SystemState { get; set; }
        public LogicalLine CurrentLine { get; set; } = null!;
        public bool ScriptEnd { get; set; }
        public bool IsFunctionMethod { get; set; }
        public bool isBegun { get; set; }
        public bool calledWhenNormal { get; set; }
        public CalledFunction CurrentCalled { get; set; } = null!;
        public int lineCount { get; set; }

        public void ShiftNextLine() { }
        public void JumpTo(LogicalLine line) { }
        public void Return(long ret) { }
        public void IntoFunction(CalledFunction call, UserDefinedFunctionArgument? srcArgs, ExpressionMediator exm) { }
        public void Begin() { }
        public void ClearFunctionList() { }
        public IProcessState Clone() => new FakeProcessState
        {
            SystemState = SystemState,
            lineCount = lineCount,
            calledWhenNormal = calledWhenNormal,
            ScriptEnd = ScriptEnd,
            IsFunctionMethod = IsFunctionMethod,
            isBegun = isBegun,
            CurrentCalled = CurrentCalled,
            CurrentLine = CurrentLine,
        };
    }

    sealed class FakeEvaluator : IVariableEvaluator
    {
        public VariableData VariableData { get; set; } = null!;
        public long RESULT { get; set; }
        public string RESULTS { get; set; } = "";
        public long[] RESULT_ARRAY { get; set; } = [];
        public string[] RESULTS_ARRAY { get; set; } = [];
        public long SELECTCOM { get; set; }
        public long[] SELECTCOM_ARRAY { get; set; } = [];
        public long NEXTCOM { get; set; }
        public string SAVEDATA_TEXT { get; set; } = "";
        public string[] ITEMNAME { get; set; } = [];
        public long[] ITEMSALES { get; set; } = [];
        public long[] ITEMPRICE { get; set; } = [];
        public long CHARANUM { get; set; }
        public long COUNT { get; set; }
        public long TARGET { get; set; }
        public long MASTER { get; set; }
        public long ASSI { get; set; }
        public long ASSIPLAY { set { } }
        public long PREVCOM { get; set; }

        public long GetNextRand(long max) => 0;
        public void Randomize(long seed) { }
        public void InitRanddata() { }
        public void DumpRanddata() { }
        public void ResetData() { }
        public void AddCharacterFromCsvNo(long no) { }
        public void DelAllCharacter() { }
        public void PickUpChara(long[] noList) { }
        public string GetCharacterDataString(long target, FunctionCode func) => "";
        public string GetCharacterParamString(long target, int paramCode) => "";
        public string GetHavingItemsString() => "";
        public void UpdateInBeginTrain() { }
        public void UpdateAfterShowUsercom() { }
        public void UpdateAfterInputCom() { }
        public void UpdateAfterSourceCheck() { }
        public void UpdateInUpcheck(EmueraConsole window, bool skipPrint) { }
        public void CUpdateInUpcheck(EmueraConsole window, long target, bool skipPrint) { }
        public bool SaveTo(int index, string text) => true;
        public EraDataResult CheckData(int saveIndex, EraSaveFileType type) => new();
        public bool LoadFrom(int index) => true;
        public void VarSize(VariableToken varID) { }
        public void SetDefaultStain(long no) { }
        public void SetEncodingResult(int[] ary) { }
        public void IamaMunchkin() { }
        public bool ItemSales(long index) => true;
        public bool BuyItem(long index) => true;
    }

    static ScriptProc CreateScriptProc(ExecutionState es, IVariableEvaluator eval)
    {
        return new ScriptProc(null!, null!, eval, null!, null!, es, null!, null!);
    }

    [Fact]
    public void SetCommnds_populates_COMITEM_list_from_SELECTCOM_ARRAY()
    {
        var es = new ExecutionState();
        es.CurrentState = new FakeProcessState();
        var eval = new FakeEvaluator();
        eval.SELECTCOM_ARRAY = [0, 101, 102, 103];
        var sp = CreateScriptProc(es, eval);

        sp.SetCommnds(3);

        Assert.True(es.isCTrain);
        Assert.Equal(3, es.coms.Count);
        Assert.Equal([101, 102, 103], es.coms);
    }

    [Fact]
    public void SetCommnds_throws_when_count_exceeds_SELECTCOM_ARRAY_length()
    {
        var es = new ExecutionState();
        es.CurrentState = new FakeProcessState();
        var eval = new FakeEvaluator();
        eval.SELECTCOM_ARRAY = [0, 1, 2];
        var sp = CreateScriptProc(es, eval);

        Assert.Throws<CodeEE>(() => sp.SetCommnds(3));
    }

    [Fact]
    public void SaveCurrentState_then_LoadPrevState_restores_state_fields()
    {
        var es = new ExecutionState();
        var original = new FakeProcessState { SystemState = SystemStateCode.Title_Begin, lineCount = 42 };
        es.CurrentState = original;
        var sp = CreateScriptProc(es, new FakeEvaluator());

        sp.SaveCurrentState(false);
        es.CurrentState.SystemState = SystemStateCode.Shop_Begin;
        sp.LoadPrevState();

        Assert.Equal(SystemStateCode.Title_Begin, ((FakeProcessState)es.CurrentState).SystemState);
        Assert.Equal(42, es.CurrentState.lineCount);
    }

    [Fact]
    public void DeleteAllPrevState_clears_saved_states_without_affecting_CurrentState()
    {
        var es = new ExecutionState();
        es.CurrentState = new FakeProcessState { SystemState = SystemStateCode.Title_Begin, lineCount = 42 };
        var sp = CreateScriptProc(es, new FakeEvaluator());

        sp.SaveCurrentState(false);
        sp.DeleteAllPrevState();

        Assert.Equal(42, es.CurrentState.lineCount);
        Assert.Equal(SystemStateCode.Title_Begin, ((FakeProcessState)es.CurrentState).SystemState);
    }

    [Fact]
    public void ResetInfiniteLoopTimer_resets_lineCount()
    {
        var es = new ExecutionState();
        es.CurrentState = new FakeProcessState { lineCount = 99 };
        var sp = CreateScriptProc(es, new FakeEvaluator());

        sp.ResetInfiniteLoopTimer();

        Assert.Equal(0, es.CurrentState.lineCount);
    }
}
