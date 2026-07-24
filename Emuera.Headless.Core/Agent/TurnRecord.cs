using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MinorShift.Emuera.GameView;

internal record TurnRecord(
    string state,
    string? inputType,
    bool needValue,
    DisplayDiff? diff,
    string? error = null,
    int? protocolVersion = null,
    long? timeLimit = null,
    bool? displayTime = null,
    string? timeUpMessage = null,
    bool timedOut = false,
    long generation = 0
)
{
    /// <summary>
    /// Agent 协议版本的单一来源（ADR-0013 / ADR-0014 / ADR-0016）。
    /// TurnRecord（增量 diff 流）与 DisplaySnapshot（全量快照）必须携带同一版本号——
    /// 晚加入者用 snapshot.protocolVersion 校验与 diff 流兼容性。
    /// AgentJsonlProtocol 与 DisplayState 共同引用此常量，避免版本升级时多处不同步。
    /// Phase 5-2：v4 = diff 模式（ops 字段已移除，仅 diff）。
    /// Phase 5-3/plan C：v5 = diff 显式清空信号——TruncateLinesOp/ReplaceAllOp 被
    /// ClearLineDiffOp(截断即清行)/ClearScreenOp(全清) 取代，清空语义自描述（ADR-0014 统一真相源）。
    /// ADR-0016（v6）：新增 4 个 TINPUT timer 字段——timeLimit / displayTime / timeUpMessage
    /// （nullable + WhenWritingNull，非 TINPUT 时不出现）+ timedOut（非 nullable bool，默认 false）。
    /// </summary>
    internal const int CurrentProtocolVersion = 7;
}

/// <summary>
/// 两个连续 DisplaySnapshot 之间的差异（Phase 2 / ADR-0014）。
/// 仅含行级操作序列 + 背景色；state/inputType/needValue/protocolVersion 由外层 TurnRecord 携带，不在此重复。
/// Phase 5 后 diff 是唯一的增量格式（ops 已废弃移除）。
/// plan C（v5）：行级操作显式携带清空语义——AppendLinesOp（追加）/ ClearLineDiffOp（截尾=清行）/
/// ClearScreenOp（全清）。清空不再是隐含的结构推断，而是 op 名即语义（DisplayState 统一真相源）。
/// </summary>
internal record DisplayDiff(
    List<LineOp> lineOps,
    string? bgColor
);

/// <summary>
/// 行级差异操作（Phase 2 / plan C v5 + shift_head 扩展）。Emuera 显示模型以追加为主：
/// 新内容追加到末尾，CLEARLINE 从末尾删除，CLEAR 全部清除；MaxLog 截断时头部行被删除。
/// 故 diff 需要四类操作：
/// - AppendLinesOp：curr 比 prev 多出的尾部行
/// - ClearLineDiffOp：清行（CLEARLINE）——截断末 clearCount 行，其后 AppendLinesOp 为 reprint
/// - ClearScreenOp：全清（CLEAR / 全重置）——清空全部行，其后 AppendLinesOp 为重印全量
/// - ShiftHeadLineOp：头部截断（MaxLog 滚动）——删除头部 count 行，与 AppendLinesOp 共存
/// 公共前缀 k 之后的差异用 ClearLineDiffOp(clearCount=prev.Count-k)+Append 表达
/// （含末行原地编辑：PRINT 不带换行落到最后一行 = ClearLineDiffOp(1)+Append(1)）。
/// shift_head 由 ConsolePrintManager.RemoveAt(0) 主动 enqueue ShiftHeadTurnOp 捕获，
/// 避免 StructuralDiff 走 CommonPrefix 检测到 k=0 误判为 ClearScreenOp + 全量重印。
/// </summary>
internal abstract record LineOp(string type);

/// <summary>追加新行（curr 比 prev 多出的尾部）。</summary>
internal record AppendLinesOp(List<DisplayLine> newLines) : LineOp("append");

/// <summary>清行（CLEARLINE 场景）：从末尾删除 clearCount 行。其后如有 AppendLinesOp 即 reprint。
/// 取代 Phase 2-4 的 TruncateLinesOp——op 名即语义，前端无需从「截断」推断「清行」。</summary>
internal record ClearLineDiffOp(int clearCount) : LineOp("clear_line_diff");

/// <summary>全清（CLEAR / 全重置场景）：清空全部行。其后如有 AppendLinesOp 即重印全量内容。</summary>
internal record ClearScreenOp() : LineOp("clear_screen");

/// <summary>头部截断（MaxLog 滚动场景）：删除头部 count 行。
/// 由 ConsolePrintManager 在 displayLineList.Count > Config.MaxLog 时 RemoveAt(0) 主动 enqueue
/// ShiftHeadTurnOp 捕获——避免 StructuralDiff 把头部删除误判为 ClearScreenOp + 全量重印。
/// 与 AppendLinesOp 共存：长游戏达 MaxLog 后每新增一行 = ShiftHeadLineOp(1) + AppendLinesOp(1)。
/// 与 ClearLineDiffOp 共存：头部截断 + 尾部 CLEARLINE 可同回合发生。
/// 与 ClearScreenOp 互斥：全清已无头部可截。</summary>
internal record ShiftHeadLineOp(int count) : LineOp("shift_head");

internal abstract record TurnOp(string type);

internal record PrintOp(
    List<PrintSegment> segments,
    ButtonRef? button
) : TurnOp("print");

internal record NewLineOp(string? align) : TurnOp("newline");

internal record ClearLineOp(int n) : TurnOp("clearline");

internal record ClearOp() : TurnOp("clear");

internal record SetBgOp(string color) : TurnOp("set_bg");

/// <summary>头部截断（MaxLog 滚动）：ConsolePrintManager.RemoveAt(0) 时 enqueue。
/// DrainAndClassifyClears 累加为 ShiftHeadCount，ComputeDiff 在 lineOps 前置 ShiftHeadLineOp(count)。</summary>
internal record ShiftHeadTurnOp(int count) : TurnOp("shift_head");

internal record PrintSegment(
    string text,
    string? color,
    bool? bold,
    bool? italic,
    string? fontname
);

internal record ButtonRef(object value, bool isInteger, long generation, int? col = null, int? width = null);

internal sealed class TurnOpConverter : JsonConverter<TurnOp>
{
    public override TurnOp? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => throw new NotSupportedException("TurnOp deserialization not supported");

    public override void Write(Utf8JsonWriter writer, TurnOp value, JsonSerializerOptions options)
    {
        JsonSerializer.Serialize(writer, (object)value, options);
    }
}

/// <summary>
/// LineOp 多态序列化（Phase 2 / plan C v5）。与 TurnOpConverter 同模式——
/// 序列化时按具体子类型（AppendLinesOp/ClearLineDiffOp/ClearScreenOp）写出全部字段 + type 鉴别符；
/// 反序列化不支持（服务端只产出 diff，前端单向消费）。
/// </summary>
internal sealed class LineOpConverter : JsonConverter<LineOp>
{
    public override LineOp? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => throw new NotSupportedException("LineOp deserialization not supported");

    public override void Write(Utf8JsonWriter writer, LineOp value, JsonSerializerOptions options)
    {
        JsonSerializer.Serialize(writer, (object)value, options);
    }
}
