using MinorShift.Emuera.GameData.Variable;
using MinorShift.Emuera.GameProc;
using MinorShift.Emuera.GameView;
using MinorShift.Emuera.Runtime.Script.Data;
using MinorShift.Emuera.Runtime.Script.Statements;
using MinorShift.Emuera.Runtime.Script.Statements.Variable;
using MinorShift.Emuera.Runtime.Utils;
using System;
using System.Collections.Generic;
using System.Drawing.Text;

namespace MinorShift.Emuera;

/// <summary>
/// Headless 专用 GlobalStatic 实现（不与 WinForms 项目共享）。
///
/// 硬约束：本类是「单会话不可变全局」——9 个核心字段在单个 session 生命周期内
/// 一旦赋值就不再变更，session 结束时通过 <see cref="Reset"/> 清理，
/// 允许下一个 session 重新 Initialize。不支持并发多会话（字段是进程级 static）。
///
/// 设计要点：
/// - 核心 9 字段用 property + internal set，setter 内加锁 + 严格断言（非 null 即拒）
/// - <see cref="Reset"/> 直接操作 backing field 绕过 setter，避免重入 lock 与 _resetCalled 误重置
/// - 辅助字段（tempDic/Pfc/ctrlZ/StackList）用 auto-property + private set，无断言
/// - <see cref="ForceQuitAndRestart"/> 是 bool 运行时标志，保持 public field（多处运行时赋值）
/// </summary>
internal static class GlobalStatic
{
    private static readonly object _lock = new();
    private static bool _resetCalled;

    // ===== 核心 9 字段：property + internal set + 严格断言（非 null 即拒）=====

    private static EmueraConsole _console = null!;
    public static EmueraConsole Console
    {
        get => _console;
        internal set
        {
            lock (_lock)
            {
                if (_console != null)
                    throw new InvalidOperationException(
                        "GlobalStatic.Console 已被赋值且未释放，拒绝覆盖。请先调用 Reset()。");
                _console = value;
                _resetCalled = false;
            }
        }
    }

    private static Process _process = null!;
    public static Process Process
    {
        get => _process;
        internal set
        {
            lock (_lock)
            {
                if (_process != null)
                    throw new InvalidOperationException(
                        "GlobalStatic.Process 已被赋值且未释放，拒绝覆盖。请先调用 Reset()。");
                _process = value;
                _resetCalled = false;
            }
        }
    }

    private static GameBase _gameBaseData = null!;
    public static GameBase GameBaseData
    {
        get => _gameBaseData;
        internal set
        {
            lock (_lock)
            {
                if (_gameBaseData != null)
                    throw new InvalidOperationException(
                        "GlobalStatic.GameBaseData 已被赋值且未释放，拒绝覆盖。请先调用 Reset()。");
                _gameBaseData = value;
                _resetCalled = false;
            }
        }
    }

    private static ConstantData _constantData = null!;
    public static ConstantData ConstantData
    {
        get => _constantData;
        internal set
        {
            lock (_lock)
            {
                if (_constantData != null)
                    throw new InvalidOperationException(
                        "GlobalStatic.ConstantData 已被赋值且未释放，拒绝覆盖。请先调用 Reset()。");
                _constantData = value;
                _resetCalled = false;
            }
        }
    }

    private static VariableData _variableData = null!;
    public static VariableData VariableData
    {
        get => _variableData;
        internal set
        {
            lock (_lock)
            {
                if (_variableData != null)
                    throw new InvalidOperationException(
                        "GlobalStatic.VariableData 已被赋值且未释放，拒绝覆盖。请先调用 Reset()。");
                _variableData = value;
                _resetCalled = false;
            }
        }
    }

    private static VariableEvaluator _vEvaluator = null!;
    public static VariableEvaluator VEvaluator
    {
        get => _vEvaluator;
        internal set
        {
            lock (_lock)
            {
                if (_vEvaluator != null)
                    throw new InvalidOperationException(
                        "GlobalStatic.VEvaluator 已被赋值且未释放，拒绝覆盖。请先调用 Reset()。");
                _vEvaluator = value;
                _resetCalled = false;
            }
        }
    }

    private static IdentifierDictionary _identifierDictionary = null!;
    public static IdentifierDictionary IdentifierDictionary
    {
        get => _identifierDictionary;
        internal set
        {
            lock (_lock)
            {
                if (_identifierDictionary != null)
                    throw new InvalidOperationException(
                        "GlobalStatic.IdentifierDictionary 已被赋值且未释放，拒绝覆盖。请先调用 Reset()。");
                _identifierDictionary = value;
                _resetCalled = false;
            }
        }
    }

    private static ExpressionMediator _eMediator = null!;
    public static ExpressionMediator EMediator
    {
        get => _eMediator;
        internal set
        {
            lock (_lock)
            {
                if (_eMediator != null)
                    throw new InvalidOperationException(
                        "GlobalStatic.EMediator 已被赋值且未释放，拒绝覆盖。请先调用 Reset()。");
                _eMediator = value;
                _resetCalled = false;
            }
        }
    }

    private static LabelDictionary _labelDictionary = null!;
    public static LabelDictionary LabelDictionary
    {
        get => _labelDictionary;
        internal set
        {
            lock (_lock)
            {
                if (_labelDictionary != null)
                    throw new InvalidOperationException(
                        "GlobalStatic.LabelDictionary 已被赋值且未释放，拒绝覆盖。请先调用 Reset()。");
                _labelDictionary = value;
                _resetCalled = false;
            }
        }
    }

    // ===== 辅助字段：auto-property + private set，无断言 =====

    /// <summary>ERB loader に引数解析の結果を渡すための橋渡し変数（AnalysisMode 用）。</summary>
    public static Dictionary<string, long> tempDic { get; private set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>連続実行を防ぐフラグ。bool 运行时标志，保持 public field（Headless 内多处赋值）。</summary>
    public static bool ForceQuitAndRestart;

    /// <summary>フォントファイルコレクション。Reset 时 Dispose + 重新 new（字体丢失，接受权衡）。</summary>
    public static PrivateFontCollection Pfc { get; private set; } = new();

    /// <summary>rewind 历史记录。Reset 时重新 new。</summary>
    public static CtrlZ ctrlZ { get; private set; } = new();

#if DEBUG
    /// <summary>DEBUG 用的スタックリスト。</summary>
    public static List<FunctionLabelLine> StackList { get; private set; } = [];
#endif

    /// <summary>
    /// 清理所有字段，允许下一个 session 重新 Initialize。
    /// 直接操作 backing field 绕过 setter，避免重入 lock 与 _resetCalled 误重置。
    /// 幂等：<c>_resetCalled</c> 标志防止重复调用（Session.GameLoopAsync finally 与 Session.Dispose 均可能调用）。
    /// </summary>
    internal static void Reset()
    {
        lock (_lock)
        {
            if (_resetCalled) return;
            _resetCalled = true;

            // 核心 9 字段置 null（直接操作 backing field，绕过 setter 断言）
            _console = null!;
            _process = null!;
            _gameBaseData = null!;
            _constantData = null!;
            _variableData = null!;
            _vEvaluator = null!;
            _identifierDictionary = null!;
            _eMediator = null!;
            _labelDictionary = null!;

            // 辅助字段重新初始化（通过 private set，在 Reset 的 lock 内安全）
            tempDic = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            ForceQuitAndRestart = false;
            Pfc.Dispose();
            Pfc = new PrivateFontCollection();
            ctrlZ = new CtrlZ();
#if DEBUG
            StackList = [];
#endif
        }
    }
}
