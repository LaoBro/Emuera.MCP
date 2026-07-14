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
    int? protocolVersion = null
)
{
    /// <summary>
    /// Agent 协议版本的单一来源（ADR-0013 / ADR-0014）。
    /// TurnRecord（增量 diff 流）与 DisplaySnapshot（全量快照）必须携带同一版本号——
    /// 晚加入者用 snapshot.protocolVersion 校验与 diff 流兼容性。
    /// AgentJsonlProtocol 与 DisplayState 共同引用此常量，避免版本升级时多处不同步。
    /// Phase 5-2：v4 = diff 模式（ops 字段已移除，仅 diff）。
    /// </summary>
    internal const int CurrentProtocolVersion = 4;
}

/// <summary>
/// 两个连续 DisplaySnapshot 之间的差异（Phase 2 / ADR-0014）。
/// 仅含行级操作序列 + 背景色；state/inputType/needValue/protocolVersion 由外层 TurnRecord 携带，不在此重复。
/// Phase 5 后 diff 是唯一的增量格式（ops 已废弃移除）。
/// </summary>
internal record DisplayDiff(
    List<LineOp> lineOps,
    string? bgColor
);

/// <summary>
/// 行级差异操作（Phase 2）。仅三种尾部操作——
/// Emuera 显示模型是追加式的：新内容追加到末尾，CLEARLINE 从末尾删除，CLEAR 全部清除，头部行永不变。
/// 故 diff 只需 append/truncate/replace_all 三种；公共前缀 k 之后的差异用 Truncate(k)+Append 表达
/// （含末行原地编辑：PRINT 不带换行落到最后一行 = Truncate(k)+Append(1)）。
/// </summary>
internal abstract record LineOp(string type);

/// <summary>追加新行（curr 比 prev 多出的尾部）。</summary>
internal record AppendLinesOp(List<DisplayLine> newLines) : LineOp("append");

/// <summary>截断尾部，保留前 keepCount 行（CLEARLINE 场景）。</summary>
internal record TruncateLinesOp(int keepCount) : LineOp("truncate");

/// <summary>全量替换（CLEAR / 全重置场景，diff 不经济时 fallback）。</summary>
internal record ReplaceAllOp(List<DisplayLine> allLines) : LineOp("replace_all");

internal abstract record TurnOp(string type);

internal record PrintOp(
    List<PrintSegment> segments,
    ButtonRef? button
) : TurnOp("print");

internal record NewLineOp(string? align) : TurnOp("newline");

internal record ClearLineOp(int n) : TurnOp("clearline");

internal record ClearOp() : TurnOp("clear");

internal record SetBgOp(string color) : TurnOp("set_bg");

internal record PrintSegment(
    string text,
    string? color,
    bool? bold,
    bool? italic,
    string? fontname
);

internal record ButtonRef(object value, bool isInteger, int? col = null, int? width = null);

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
/// LineOp 多态序列化（Phase 2）。与 TurnOpConverter 同模式——
/// 序列化时按具体子类型（AppendLinesOp/TruncateLinesOp/ReplaceAllOp）写出全部字段 + type 鉴别符；
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
