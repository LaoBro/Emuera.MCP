using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using MinorShift.Emuera.Primitives;
using MinorShift.Emuera.Runtime;
using MinorShift.Emuera.UI.Game;

namespace MinorShift.Emuera.GameView;

/// <summary>
/// 当前全量显示状态的快照（ADR-0013 决策二）。
/// state-based 格式——直接表示当前显示状态，不是操作序列的回放。
/// WS 晚加入者调 GET /snapshot 拿初始状态，再订阅 WS 收增量 ops。
/// </summary>
internal record DisplaySnapshot(
    List<DisplayLine> lines,
    string? bgColor,
    string state,
    string? inputType,
    bool needValue,
    int protocolVersion
);

/// <summary>
/// 快照中的单行（ADR-0013 决策二）。
/// 每行含 entries[]（每个对应一个 ConsoleButtonString）+ 对齐 + 是否行末。
/// 不含 isLogicalLine / isTemporary / LineNo——这些是服务端内部概念。
/// </summary>
internal record DisplayLine(
    List<DisplayEntry> entries,
    string? align,
    bool isLineEnd
);

/// <summary>
/// 行内条目（ADR-0013 决策二）。
/// 含 segments[]（复用 PrintSegment）+ button（ButtonRef?，含 col/width）。
/// </summary>
internal record DisplayEntry(
    List<PrintSegment> segments,
    ButtonRef? button
);

/// <summary>
/// DisplayState 模块（ADR-0013 决策一/二；ADR-0014 推翻决策四）。
/// 封装 EmueraConsole.DisplayLineList → DisplaySnapshot 序列化 + 几何计算。
/// 深模块：删除测试——删掉后序列化 + 几何计算复杂度转移到 KestrelGameServer。
/// 当前服务于 Web 路径（GET /snapshot 端点 + 未来 Web 前端）；CLI 将在 Phase 4 改为消费 DisplayState（ADR-0014 统一真相源）。
///
/// Phase 1（DisplayState 统一真相源执行计划）：从无状态工具类升级为有状态的显示模型。
/// 持有当前 DisplaySnapshot（_current），以 _pendingOps 为权威变更信号做变更检测（TryUpdate）。
/// _gate 锁保护跨线程读写——游戏线程 BuildTurn 调 TryUpdate，HTTP 线程 GET /snapshot 读 Current。
/// </summary>
internal sealed class DisplayState
{
    /// <summary>
    /// DisplaySnapshot 的 JSON 序列化选项（ADR-0013 决策二）。
    /// 与 TurnRecord 一致使用 WhenWritingNull——nullable 字段（bgColor/inputType/align/button/col/width 等）
    /// 在为 null 时不写入 JSON，保持响应体紧凑。
    /// </summary>
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly EmueraConsole _console;
    private readonly string _defaultFontName;
    private readonly object _gate = new();
    private DisplaySnapshot? _current;

    internal DisplayState(EmueraConsole console, string defaultFontName)
    {
        _console = console;
        _defaultFontName = defaultFontName;
    }

    /// <summary>
    /// 当前快照（线程安全）。读取时先 TryUpdate 保证最新。
    /// GET /snapshot 端点经此取快照；Phase 2 ComputeDiff 亦经此取连续快照对。
    /// </summary>
    internal DisplaySnapshot Current
    {
        get { lock (_gate) { TryUpdate(); return _current!; } }
    }

    /// <summary>
    /// peek _pendingOps 做变更检测（不 drain；drain 仍由 BuildTurn 的 TakePendingOps 负责）。
    /// _pendingOps 非空 → 重建 _current 并返回 true；空且 _current 已存在 → 返回 false 且 _current 不变。
    /// 必须在 BuildTurn 的 TakePendingOps() 之前调用，否则 pendingOps 已空，检测永远 false。
    /// </summary>
    internal bool TryUpdate()
    {
        lock (_gate)
        {
            if (_current != null && _console.PendingOpCount == 0)
                return false; // 无变化
            _current = Rebuild();
            return true;
        }
    }

    /// <summary>
    /// 从当前 console 状态重建全量快照。
    /// 浅拷贝 displayLineList 防止 HTTP 线程并发遍历时游戏线程写入（Q5）。
    ///
    /// 注意：游戏线程（ConsolePrintManager）写 displayLineList 不持 _gate，故
    /// new List&lt;T&gt;(source) 的 CopyTo 可能与 Add 竞态（ArgumentException: array not long enough）。
    /// 重试即可——竞态窗口极短，下一次拷贝时数组长度已匹配。Phase 2/3 评估是否需让游戏线程也持 _gate。
    /// </summary>
    private DisplaySnapshot Rebuild()
    {
        List<ConsoleDisplayLine> linesCopy;
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                linesCopy = new List<ConsoleDisplayLine>(_console.DisplayLineList);
                break;
            }
            catch (ArgumentException) when (attempt < 10)
            {
                // displayLineList 在 CopyTo 期间被游戏线程修改，重试
            }
        }
        return BuildSnapshot(linesCopy, _console.bgColor,
            _console.State, _console.CurrentRequest, _defaultFontName);
    }

    /// <summary>
    /// 生成全量快照并序列化为 JSON 字符串（GET /snapshot 端点使用）。
    /// 经 Current → TryUpdate 保证快照随游戏打印实时推进。
    /// </summary>
    internal string GetSnapshotJson() => JsonSerializer.Serialize(Current, JsonOpts);

    /// <summary>
    /// 从已知 displayLineList 构造 DisplaySnapshot（ADR-0013 决策二）。
    /// 遍历 displayLineList，每行映射为 DisplayLine：ConsoleButtonString[] → entries[]，
    /// 每个 entry 的 segments 从 StrArray 提取（复用 ConsolePrintManager.BuildPrintOpsForLine 的
    /// segment + 几何计算），button 填充 ButtonRef（含 col/width）。
    /// 提取为 internal static 以便单元测试（DisplayStateTests）直测序列化，无需构造 EmueraConsole。
    ///
    /// defaultFontName 参数：默认字体名，传给 BuildPrintOpsForLine 判断 segment fontname 是否为默认。
    /// 调用方负责传入——DisplayState.Rebuild 从构造函数注入的值传（HTTP 线程由 Session 从 ConfigData 读）。
    /// </summary>
    internal static DisplaySnapshot BuildSnapshot(
        List<ConsoleDisplayLine> displayLineList,
        EmuColor bgColor,
        ConsoleState state,
        InputRequest? currentRequest,
        string defaultFontName)
    {
        var lines = new List<DisplayLine>(displayLineList.Count);
        foreach (var line in displayLineList)
        {
            // 复用 BuildPrintOpsForLine 的 segment 提取 + 几何计算——保证快照与增量 ops 一致
            var printOps = ConsolePrintManager.BuildPrintOpsForLine(line, defaultFontName);
            var entries = printOps
                .Select(op => new DisplayEntry(op.segments, op.button))
                .ToList();

            lines.Add(new DisplayLine(
                entries: entries,
                align: AlignToString(line.Align),
                isLineEnd: line.IsLineEnd
            ));
        }

        return new DisplaySnapshot(
            lines: lines,
            bgColor: bgColor.ToHex(),
            state: state.ToString(),
            inputType: currentRequest?.InputType.ToString(),
            needValue: currentRequest?.NeedValue ?? false,
            protocolVersion: TurnRecord.CurrentProtocolVersion
        );
    }

    /// <summary>
    /// DisplayLineAlignment → 字符串映射。
    /// LEFT → "left"，CENTER → "center"，RIGHT → "right"，未知值 → null。
    /// </summary>
    private static string? AlignToString(DisplayLineAlignment align) => align switch
    {
        DisplayLineAlignment.LEFT => "left",
        DisplayLineAlignment.CENTER => "center",
        DisplayLineAlignment.RIGHT => "right",
        _ => null
    };
}
