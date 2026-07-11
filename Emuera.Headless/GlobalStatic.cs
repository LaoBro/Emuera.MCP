using MinorShift.Emuera.GameData.Variable;
using MinorShift.Emuera.GameProc;
using MinorShift.Emuera.GameView;
using MinorShift.Emuera.Runtime.Script.Data;
using MinorShift.Emuera.Runtime.Script.Statements;
using MinorShift.Emuera.Runtime.Script.Statements.Variable;
using MinorShift.Emuera.Runtime.Utils;
using MinorShift.Emuera.Runtime.Config;
using System;
using System.Collections.Generic;
using System.Threading;

namespace MinorShift.Emuera;

/// <summary>
/// Headless 专用 GlobalStatic 实现（不与 WinForms 项目共享）。
///
/// 会话作用域实例 + ambient <see cref="Current"/> + scope RAII（ADR-0008）。
/// 9 个核心字段在单个 session 生命周期内一旦赋值就不再变更；session 结束时 scope.Dispose
/// 自动清理（Pfc.Dispose + Current 归 null），允许下一个 session 重新 OpenScope+Initialize。
///
/// 设计要点：
/// - <see cref="Current"/> 由 <see cref="AsyncLocal{T}"/> 承载，只在开 scope 的 async 上下文存活；
///   并行测试各自 scope 互不污染。
/// - 核心 9 字段用 static 转发属性 + 实例 backing field，setter 内加实例锁 + 严格断言（非 null 即拒）。
/// - 辅助字段（tempDic/ForceQuitAndRestart/Pfc/ctrlZ/StackList）全部为实例成员，随 scope 生灭。
/// - <see cref="OpenScope"/> 断言 Current 原为 null，set 新实例，返回 scope；scope.Dispose 恢复 null。
/// - 旧的 <c>Reset()</c> 方法和 <c>_resetCalled</c> 幂等标志已删除——scope RAII 单一归属。
/// </summary>
internal sealed class GlobalStatic
{
    private static readonly AsyncLocal<GlobalStatic?> _current = new();

    /// <summary>Ambient 会话作用域实例。scope 外为 null。</summary>
    public static GlobalStatic? Current => _current.Value;

    // ===== 实例锁 + 核心 9 字段 backing =====

    private readonly object _lock = new();

    private EmueraConsole _console = null!;
    private Process _process = null!;
    private GameBase _gameBaseData = null!;
    private ConstantData _constantData = null!;
    private VariableData _variableData = null!;
    private VariableEvaluator _vEvaluator = null!;
    private IdentifierDictionary _identifierDictionary = null!;
    private ExpressionMediator _eMediator = null!;
    private LabelDictionary _labelDictionary = null!;

    // ===== 辅助字段（实例成员，随 scope 生灭）=====

    /// <summary>ERB loader に引数解析の結果を渡すための橋渡し変数（AnalysisMode 用）。</summary>
    private Dictionary<string, long> _tempDic = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>連続実行を防ぐフラグ。bool 运行时标志，多处运行时赋值。</summary>
    private bool _forceQuitAndRestart;

    /// <summary>フォントファイルコレクション。scope.Dispose 时 Dispose。</summary>
    private HeadlessFontCollection _pfc = new();

    /// <summary>rewind 历史记录。随 scope 生灭。</summary>
    private CtrlZ _ctrlZ = new();

#if DEBUG
    /// <summary>DEBUG 用的スタックリスト。随 scope 生灭。</summary>
    private List<FunctionLabelLine> _stackList = [];
#endif

    // ===== 单赋值断言辅助 =====

    /// <summary>
    /// 核心 9 字段共用的单赋值断言：锁内检查 field 已非 null 则拒，否则赋值。
    /// </summary>
    private void SetCoreField<T>(ref T field, T value, string fieldName) where T : class
    {
        lock (_lock)
        {
            if (field != null)
                throw new InvalidOperationException(
                    $"GlobalStatic.{fieldName} 已被赋值，拒绝覆盖。scope 内仅允许首次赋值。");
            field = value;
        }
    }

    // ===== static 转发属性：核心 9 字段（保留 GlobalStatic.VEvaluator 调用语法）=====

    public static EmueraConsole Console
    {
        get => Current!._console;
        internal set => Current!.SetCoreField(ref Current!._console, value, nameof(Console));
    }

    public static Process Process
    {
        get => Current!._process;
        internal set => Current!.SetCoreField(ref Current!._process, value, nameof(Process));
    }

    public static GameBase GameBaseData
    {
        get => Current!._gameBaseData;
        internal set => Current!.SetCoreField(ref Current!._gameBaseData, value, nameof(GameBaseData));
    }

    public static ConstantData ConstantData
    {
        get => Current!._constantData;
        internal set => Current!.SetCoreField(ref Current!._constantData, value, nameof(ConstantData));
    }

    public static VariableData VariableData
    {
        get => Current!._variableData;
        internal set => Current!.SetCoreField(ref Current!._variableData, value, nameof(VariableData));
    }

    public static VariableEvaluator VEvaluator
    {
        get => Current!._vEvaluator;
        internal set => Current!.SetCoreField(ref Current!._vEvaluator, value, nameof(VEvaluator));
    }

    public static IdentifierDictionary IdentifierDictionary
    {
        get => Current!._identifierDictionary;
        internal set => Current!.SetCoreField(ref Current!._identifierDictionary, value, nameof(IdentifierDictionary));
    }

    public static ExpressionMediator EMediator
    {
        get => Current!._eMediator;
        internal set => Current!.SetCoreField(ref Current!._eMediator, value, nameof(EMediator));
    }

    public static LabelDictionary LabelDictionary
    {
        get => Current!._labelDictionary;
        internal set => Current!.SetCoreField(ref Current!._labelDictionary, value, nameof(LabelDictionary));
    }

    // ===== static 转发属性：辅助字段 =====

    /// <summary>ERB loader に引数解析の結果を渡すための橋渡し変数（AnalysisMode 用）。</summary>
    public static Dictionary<string, long> tempDic => Current!._tempDic;

    /// <summary>連続実行を防ぐフラグ。bool 运行时标志，多处运行时赋值。</summary>
    public static bool ForceQuitAndRestart
    {
        get => Current!._forceQuitAndRestart;
        set => Current!._forceQuitAndRestart = value;
    }

    /// <summary>フォントファイルコレクション。scope.Dispose 时 Dispose。</summary>
    public static HeadlessFontCollection Pfc => Current!._pfc;

    /// <summary>rewind 历史记录。随 scope 生灭。</summary>
    public static CtrlZ ctrlZ => Current!._ctrlZ;

#if DEBUG
    /// <summary>DEBUG 用的スタックリスト。随 scope 生灭。</summary>
    public static List<FunctionLabelLine> StackList => Current!._stackList;
#endif

    // ===== Scope RAII =====

    /// <summary>
    /// 开启一个会话作用域 scope：断言当前 async 上下文无已有 scope，set 新 <see cref="GlobalStatic"/> 实例到
    /// <see cref="Current"/>，并绑定 <see cref="Config"/>/<see cref="ConfigData"/> 的 ambient 当前配置
    /// （候选 2 / ADR-0009：配置仅经 scope 注入，再无静态单例）。Dispose 时 <see cref="Pfc"/>.Dispose +
    /// 三处 Current 归 null。
    /// </summary>
    public static IDisposable OpenScope(ConfigData configData)
    {
        if (_current.Value != null)
            throw new InvalidOperationException(
                "GlobalStatic scope 已在此 async 上下文中打开。请先 Dispose 现有 scope。");
        var instance = new GlobalStatic();
        _current.Value = instance;
        ConfigData.SetCurrent(configData);
        Config.SetCurrent(configData);
        return new Scope(instance);
    }

    private sealed class Scope : IDisposable
    {
        private GlobalStatic? _instance;

        internal Scope(GlobalStatic instance) => _instance = instance;

        public void Dispose()
        {
            var instance = _instance;
            if (instance == null) return; // 幂等：重复 Dispose 安全
            _instance = null;
            // 确保 Current 一定归 null，即使 Pfc.Dispose 抛异常也不残留 scope
            try
            {
                instance._pfc.Dispose();
            }
            finally
            {
                _current.Value = null;
                ConfigData.ClearCurrent();
                Config.ClearCurrent();
            }
        }
    }
}
