using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using MinorShift.Emuera.Primitives;
using MinorShift.Emuera.Runtime;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.UI.Game;

namespace MinorShift.Emuera.GameView;

/// <summary>
/// 当前全量显示状态的快照（ADR-0013 决策二 / ADR-0016 加 timer 字段）。
/// state-based 格式——直接表示当前显示状态，不是操作序列的回放。
/// WS 晚加入者调 GET /snapshot 拿初始状态，再订阅 WS 收增量 ops。
/// ADR-0016：新增 timeLimit / displayTime / timeUpMessage 三字段（无 timedOut——
/// snapshot 表示"当前状态"，"上一帧是否超时触发"对晚加入者无意义）。
/// </summary>
internal record DisplaySnapshot(
    List<DisplayLine> lines,
    string? bgColor,
    string state,
    string? inputType,
    bool needValue,
    int protocolVersion,
    long generation,
    long? timeLimit = null,
    bool? displayTime = null,
    string? timeUpMessage = null
);

/// <summary>
/// 快照中的单行（ADR-0013 决策二）。
/// 每行含 entries[]（每个对应一个 ConsoleButtonString）+ 对齐 + 是否行末。
/// 不含 isLogicalLine / isTemporary——这些是服务端内部概念。
///
/// Phase 4-1（Q1 + R3）：新增 <see cref="LineNo"/> + <see cref="SourceLine"/> 两个 CLI 渲染专用字段。
/// 二者均标 <c>[JsonIgnore]</c> 不进入 JSON 序列化——Web 契约保持 ADR-0013 形态。
/// <see cref="LineNo"/> 用于 CLI delta 算法（<c>_lastRenderedLineNo</c> 比较）；
/// <see cref="SourceLine"/> 持原 <see cref="ConsoleDisplayLine"/> 引用，供 <see cref="TerminalLineFormatter.FormatLineForTerminal"/>
/// 格式化（PrintSegment 丢失 ConsoleSpacePart 几何，无法重建，故保留原引用）。
/// record 默认 Equals 会纳入这两个字段——Phase 2 的 <see cref="DisplayState.LinesEqual"/> 已手写深度值比较
/// 绕过 record.Equals，故 diff 正确性不受影响。
/// </summary>
internal record DisplayLine(
    List<DisplayEntry> entries,
    string? align,
    bool isLineEnd
)
{
    /// <summary>
    /// CLI 渲染专用：原 ConsoleDisplayLine.LineNo（Q1）。用于 delta 算法比较。
    /// BuildSnapshot 从 line.LineNo 传入。不进入 JSON。
    /// </summary>
    [JsonIgnore] internal int LineNo { get; set; }

    /// <summary>
    /// CLI 渲染专用：原 ConsoleDisplayLine 引用（R3 扩展）。
    /// TerminalRenderer 经此调 FormatLineForTerminal（PrintSegment 丢失 ConsoleSpacePart 几何，无法重建）。
    /// Web 路径不访问此字段。不进入 JSON。
    /// </summary>
    [JsonIgnore] internal ConsoleDisplayLine? SourceLine { get; set; }

    /// <summary>
    /// CLI 渲染专用：align 居中/右对齐时的前导空格数（绝对列偏移）。
    /// BuildSnapshot 经 TerminalLineFormatter.GetGameColumnWidth + BuildTerminalLine 计算——
    /// 与 FormatLineForTerminal 输出的前导空格数完全一致。
    /// ButtonRegionTracker.UpdateFromSnapshot 把此偏移加到 entry.button.col 上，
    /// 得到 PTY 显示的绝对列位置（与 SGR mouse 的 Cb 列匹配）。
    /// Web 路径不访问此字段（前端用 CSS 处理 align，col 保持相对）。不进入 JSON。
    /// </summary>
    [JsonIgnore] internal int AlignOffset { get; set; }
};

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
internal sealed class DisplayState : IDisplayState
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
    private readonly bool _fullDiffOnFirstTurn;
    private readonly object _gate = new();
    private DisplaySnapshot? _current;
    private DisplaySnapshot? _previous;

    /// <summary>
    /// plan C + shift_head 扩展：本回合从 _pendingOps 分类出的权威清空信号。
    /// ClearAll 主导（CLEAR），ClearLineCount 为累加的清行数，Bg 为最后一次 SetBgOp 颜色（可空）。
    /// ShiftHeadCount 为累加的头部截断行数（MaxLog 滚动）——由 ShiftHeadTurnOp 累加，
    /// ComputeDiff 在 lineOps 前置 ShiftHeadLineOp(ShiftHeadCount)。
    /// </summary>
    private sealed record TurnClearSignal
    {
        public bool ClearAll;
        public int ClearLineCount;
        public int ShiftHeadCount;
        public string? Bg;
    }

    /// <param name="fullDiffOnFirstTurn">
    /// MAUI 模式传 true——首帧（_previous==null）返回全量 AppendLinesOp 而非 null。
    /// MAUI 无 GET /snapshot 端点，首帧 diff 是唯一画面来源；返回 null 会导致游戏开头输出全部丢失。
    /// HTTP/CLI 模式传 false（默认）——首帧 diff=null，靠 GET /snapshot（HTTP）或全量重绘（CLI）获取画面。
    /// </param>
    internal DisplayState(EmueraConsole console, string defaultFontName, bool fullDiffOnFirstTurn = false)
    {
        _console = console;
        _defaultFontName = defaultFontName;
        _fullDiffOnFirstTurn = fullDiffOnFirstTurn;
    }

    bool IDisplayState.TryUpdate() => TryUpdate();
    DisplaySnapshot IDisplayState.Current => Current;

    /// <summary>
    /// 当前快照（线程安全）。读取时先 TryUpdate 保证最新。
    /// GET /snapshot 端点经此取快照；Phase 2 ComputeDiff 亦经此取连续快照对。
    /// </summary>
    internal DisplaySnapshot Current
    {
        get { lock (_gate) { TryUpdate(); return _current!; } }
    }

    /// <summary>
    /// Phase 5-3：peek _pendingOps 做变更检测，rebuild 后消费式清空（防无限增长）。
    /// _pendingOps 非空 → 重建 _current + Clear → 返回 true；空且 _current 已存在 → 返回 false 且 _current 不变。
    /// ConcurrentQueue 保证跨线程安全：游戏线程 Enqueue 与此处的 Clear 可并发。
    /// </summary>
    internal bool TryUpdate()
    {
        lock (_gate)
        {
            if (_current != null && _console.PendingOpCount == 0)
                return false; // 无变化
            _current = Rebuild();
            _console.ClearPendingOps();
            return true;
        }
    }

    /// <summary>
    /// 计算本回合 DisplayDiff（Phase 2 / ADR-0014 / plan C v5）。
    /// 原子化（持 _gate）：先确保 _current 最新——若 _pendingOps 非空（本回合有显示变更）则 Rebuild
    /// 并 drain 分类出<b>权威清空信号</b>（ClearOp/ClearLineOp/SetBgOp，见 ConsolePrintManager）；
    /// 再比对 _current（本回合）与 _previous（上一回合）产出 diff。
    /// 清空语义优先取权威事件（精确行数 n），队列被提前 drain（HTTP/CLI 帧刷新抢先 TryUpdate，见 Q11 race 降级）
    /// 时回退 k 结构分类（<see cref="StructuralDiff"/>），退化为「快照推导」。
    ///
    /// 返回 null 的三种情况：首次回合（_previous==null）、_current 未构建、本回合显示未变
    /// （ReferenceEquals(_previous, _current)——no-op 回合，仅 state/inputType 变化由 TurnRecord 顶层携带）。
    ///
    /// 调用契约：BuildTurn 直接调 ComputeDiff（不再前置 TryUpdate）；HTTP/CLI 的帧级刷新仍经 TryUpdate。
    /// _previous 读写与 _current 同处 _gate 锁内。仅游戏线程 BuildTurn 调用，无新增并发。
    /// </summary>
    internal DisplayDiff? ComputeDiff()
    {
        lock (_gate)
        {
            // 1. 刷新 _current + 分类权威清空信号（仅本回合有变更时 drain）
            TurnClearSignal signal;
            if (_current == null || _console.PendingOpCount > 0)
            {
                _current = Rebuild();
                signal = DrainAndClassifyClears();
            }
            else
            {
                signal = new TurnClearSignal(); // 无新变更，无权威清空事件
            }

            var current = _current;
            if (current == null) return null; // _current 未构建

            var prev = _previous;
            _previous = current;

            if (prev == null)
            {
                // 首次回合：HTTP/CLI 返回 null（靠 GET /snapshot 或全量重绘获取画面）；
                // MAUI 模式（_fullDiffOnFirstTurn=true）无 snapshot 端点，首帧 diff 是唯一画面来源，
                // 返回全量 AppendLinesOp 让 Vue 端 applyDiff 渲染游戏开头输出。
                if (_fullDiffOnFirstTurn && current.lines.Count > 0)
                    return new DisplayDiff(
                        new List<LineOp> { new AppendLinesOp(current.lines) },
                        current.bgColor);
                return null;
            }
            if (ReferenceEquals(prev, current)) return null;  // no-op：本回合显示未变

            // 2. 行级操作：权威清空优先，否则结构分类（兼 race 降级）
            //    shift_head 调整"effective prev"——prev 等价于去掉头部 ShiftHeadCount 行后的列表。
            //    后续 ClearLineCount / StructuralDiff 都基于 effectivePrev 计算，避免头部删除
            //    被 StructuralDiff 误判为 ClearScreenOp + Append 全量重印。
            List<LineOp> lineOps;
            if (signal.ClearAll)
            {
                // ClearOp 主导——全清已覆盖头部，shift_head 互斥不产出
                lineOps = new List<LineOp> { new ClearScreenOp() };
                if (current.lines.Count > 0)
                    lineOps.Add(new AppendLinesOp(current.lines));
            }
            else
            {
                // shift_head 调整 effectivePrev：去掉头部 ShiftHeadCount 行。
                // 后续 ClearLineCount / StructuralDiff 基于 effectivePrev 计算，
                // 让头部删除独立表达为 ShiftHeadLineOp，不污染尾部 diff。
                int skipHead = Math.Min(signal.ShiftHeadCount, prev.lines.Count);
                var effectivePrevLines = skipHead > 0
                    ? prev.lines.GetRange(skipHead, prev.lines.Count - skipHead)
                    : prev.lines;

                List<LineOp> tailOps;
                bool prependShiftHead = skipHead > 0;
                if (signal.ClearLineCount > 0)
                {
                    int keep = effectivePrevLines.Count - signal.ClearLineCount;
                    // race 降级（Q11）：部分清空信号被帧级 TryUpdate 抢先 drain，导致 clearCount
                    // 与真实快照行数冲突（keep<0 代表清行数超过上一帧总行；current 行数不足前缀
                    // 代表清行后又重印的行少于应保留的前缀）。两种情况都回退结构分类，避免
                    // GetRange 越界或将真实 CLEAR 误报为单行清空。
                    if (keep < 0 || current.lines.Count < keep)
                    {
                        tailOps = StructuralDiff(prev, current);
                        prependShiftHead = false; // race 降级路径不前置 shift_head
                    }
                    else
                    {
                        tailOps = new List<LineOp> { new ClearLineDiffOp(signal.ClearLineCount) };
                        var appended = current.lines.GetRange(keep, current.lines.Count - keep);
                        if (appended.Count > 0) tailOps.Add(new AppendLinesOp(appended));
                    }
                }
                else
                {
                    // 无权威 ClearLineCount —— 用 effectivePrev 走结构分类，
                    // 避免头部删除被 StructuralDiff 误判为 ClearScreenOp + Append 全量重印。
                    var effectivePrev = new DisplaySnapshot(
                        effectivePrevLines, prev.bgColor, prev.state, prev.inputType,
                        prev.needValue, prev.protocolVersion, prev.generation,
                        prev.timeLimit, prev.displayTime, prev.timeUpMessage);
                    tailOps = StructuralDiff(effectivePrev, current);
                }

                if (prependShiftHead)
                {
                    lineOps = new List<LineOp> { new ShiftHeadLineOp(skipHead) };
                    lineOps.AddRange(tailOps);
                }
                else
                {
                    lineOps = tailOps;
                }
            }

            // 3. 背景色：权威 SetBgOp 优先，否则从快照比较推断
            string? bg = signal.Bg ?? (prev.bgColor == current.bgColor ? null : current.bgColor);
            return new DisplayDiff(lineOps, bg);
        }
    }

    /// <summary>
    /// plan C + shift_head：drain _pendingOps 并分类出本回合的清空信号。ClearOp 主导（→ClearAll，吞掉 CLEARLINE）；
    /// 多个 ClearLineOp 的 n 累加；SetBgOp 取最后一次（bg 由 DisplayDiff.bgColor 携带，不计入 lineOps）；
    /// ShiftHeadTurnOp 的 count 累加为 ShiftHeadCount（MaxLog 头部截断，独立于尾部清空）。
    /// PrintOp/NewLineOp 不影响清空判定，忽略。
    /// </summary>
    private TurnClearSignal DrainAndClassifyClears()
    {
        var signal = new TurnClearSignal();
        foreach (var op in _console.DrainPendingOps())
        {
            switch (op)
            {
                case ClearOp: signal.ClearAll = true; break;
                case ClearLineOp clo: signal.ClearLineCount += clo.n; break;
                case SetBgOp sbo: signal.Bg = sbo.color; break;
                case ShiftHeadTurnOp sho: signal.ShiftHeadCount += sho.count; break;
            }
        }
        return signal;
    }

    /// <summary>
    /// 比对两个快照的 lines 产出 LineOp 序列（plan C v5 + race 降级）。
    /// Emuera 显示模型以追加为主——头部行仅在 MaxLog 截断时变化（由 ShiftHeadTurnOp 主动捕获，
    /// 不走此 fallback）；差异通常只在尾部。
    /// 公共前缀 k 之后的差异用 ClearLineDiffOp(clearCount=prev.Count-k)+Append 表达；
    /// k==0 且 prev 非空 → ClearScreenOp + Append（全清后重印，等价于旧 ReplaceAll）。
    /// race 降级路径：_pendingOps 被帧级 TryUpdate 抢先 drain 时，ShiftHeadTurnOp 不可见，
    /// 头部截断会令 CommonPrefix 检测到 k=0 → 退化为 ClearScreenOp + Append 全量重印。
    /// 这是罕见并发场景，正确性不破坏（前端 applyDiff 正确处理 clear_screen + append），仅性能受损。
    /// </summary>
    private static List<LineOp> StructuralDiff(DisplaySnapshot prev, DisplaySnapshot curr)
    {
        var p = prev.lines;
        var c = curr.lines;
        int k = CommonPrefix(p, c);
        if (k == p.Count)
        {
            // prev 是 curr 的前缀（含完全相等到此分支不可能——ReferenceEquals 已短路，
            // 但值相等不同引用可能到此。p.Count==c.Count 时 GetRange(k,0) 返回空列表 → Append 空）
            return c.Count == p.Count
                ? new List<LineOp>()
                : [new AppendLinesOp(c.GetRange(k, c.Count - k))];
        }
        if (k == c.Count) return [new ClearLineDiffOp(p.Count - k)];                 // 纯截尾（CLEARLINE）
        if (k == 0) return [new ClearScreenOp(), new AppendLinesOp(c)];              // 头部都变 = CLEAR/全重置
        return [new ClearLineDiffOp(p.Count - k), new AppendLinesOp(c.GetRange(k, c.Count - k))]; // 尾部替换
    }

    /// <summary>
    /// 逐行值比较求公共前缀长度。
    /// DisplayLine/DisplayEntry 是 record 但含 List&lt;&gt; 字段——C# record 对集合字段用引用相等，
    /// 故不能直接用 record.Equals。此处做深度值比较：align/isLineEnd + entries 逐条 segments/button 比对。
    /// PrintSegment 全为值类型/string 字段，record.Equals 正确；ButtonRef.value 是 object 但实际为 int/string，
    /// object.Equals 正确委派到对应类型的值相等。
    /// </summary>
    private static int CommonPrefix(List<DisplayLine> a, List<DisplayLine> b)
    {
        int n = Math.Min(a.Count, b.Count);
        int k = 0;
        while (k < n && LinesEqual(a[k], b[k])) k++;
        return k;
    }

    private static bool LinesEqual(DisplayLine a, DisplayLine b)
    {
        if (a.align != b.align) return false;
        if (a.isLineEnd != b.isLineEnd) return false;
        if (a.entries.Count != b.entries.Count) return false;
        for (int i = 0; i < a.entries.Count; i++)
        {
            if (!EntriesEqual(a.entries[i], b.entries[i])) return false;
        }
        return true;
    }

    private static bool EntriesEqual(DisplayEntry a, DisplayEntry b)
    {
        if (a.segments.Count != b.segments.Count) return false;
        for (int i = 0; i < a.segments.Count; i++)
        {
            if (!a.segments[i].Equals(b.segments[i])) return false;
        }
        return Equals(a.button, b.button);
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
            _console.State, _console.CurrentRequest, _defaultFontName, _console.LastButtonGeneration);
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
        string defaultFontName,
        long generation = 0)
    {
        var lines = new List<DisplayLine>(displayLineList.Count);
        foreach (var line in displayLineList)
        {
            // 复用 BuildPrintOpsForLine 的 segment 提取 + 几何计算——保证快照与增量 ops 一致
            var printOps = ConsolePrintManager.BuildPrintOpsForLine(line, defaultFontName);
            var entries = printOps
                .Select(op => new DisplayEntry(op.segments, op.button))
                .ToList();

            // Phase 4-1：填入 CLI 渲染专用字段——LineNo（delta 算法）+ SourceLine（FormatLineForTerminal）
            // + AlignOffset（绝对列偏移，与 FormatLineForTerminal 的 align 居中/右对齐前导空格一致）
            var dl = new DisplayLine(
                entries: entries,
                align: AlignToString(line.Align),
                isLineEnd: line.IsLineEnd
            );
            dl.LineNo = line.LineNo;
            dl.SourceLine = line;
            dl.AlignOffset = ComputeAlignOffset(line);
            lines.Add(dl);
        }

        return new DisplaySnapshot(
            lines: lines,
            bgColor: bgColor.ToHex(),
            state: state.ToString(),
            inputType: currentRequest?.InputType.ToString(),
            needValue: currentRequest?.NeedValue ?? false,
            protocolVersion: TurnRecord.CurrentProtocolVersion,
            generation: generation,
            // ADR-0016：TINPUT timer 元数据——仅在 TINPUT 期间（Timelimit > 0）填充。
            // 非 TINPUT 期间 / 无 currentRequest → 三字段均为 null（WhenWritingNull 时不写入 JSON）。
            // displayTime 取 InputRequest.DisplayTime——尊重 ERB 脚本"别给玩家看"的意图。
            timeLimit: currentRequest is { Timelimit: > 0 } req ? req.Timelimit : null,
            displayTime: currentRequest is { Timelimit: > 0, DisplayTime: true } ? true : null,
            timeUpMessage: currentRequest is { Timelimit: > 0 } reqWithMes
                && !string.IsNullOrEmpty(reqWithMes.TimeUpMes)
                ? reqWithMes.TimeUpMes
                : null
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

    /// <summary>
    /// 计算 align 居中/右对齐的前导空格数（与 TerminalLineFormatter.FormatLineForTerminal 一致）。
    /// ButtonRegionTracker.UpdateFromSnapshot 把此偏移加到 entry.button.col 上得到 PTY 绝对列。
    /// LEFT 或未知 align 返回 0。CENTER: (gameWidth - textWidth)/2，RIGHT: gameWidth - textWidth。
    /// 测试环境（Config.Current 为 null）返回 0——BuildSnapshot 单测用反射设 align 绕过 Config，
    /// 此处避免触发 NullReferenceException。生产路径 Config.Current 在 Program 启动时设置。
    /// </summary>
    private static int ComputeAlignOffset(ConsoleDisplayLine line)
    {
        if (line.Align != DisplayLineAlignment.CENTER && line.Align != DisplayLineAlignment.RIGHT)
            return 0;
        if (Config.Current == null)
            return 0;
        TerminalLineFormatter.BuildTerminalLine(line, out int textWidth);
        int gameWidth = TerminalLineFormatter.GetGameColumnWidth();
        return line.Align == DisplayLineAlignment.CENTER
            ? Math.Max((gameWidth - textWidth) / 2, 0)
            : Math.Max(gameWidth - textWidth, 0);
    }
}
