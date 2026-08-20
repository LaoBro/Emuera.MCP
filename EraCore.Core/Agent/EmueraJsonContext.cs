using System.Text.Json.Serialization;
using MinorShift.Emuera.Runtime.Config.JSON;

namespace MinorShift.Emuera.GameView;

/// <summary>
/// System.Text.Json 源生成上下文（3.3 NativeAOT 硬性改造）。
/// AOT 下反射序列化被禁用（JsonSerializerIsReflectionDisabled），所有 JSON 路径必须经此上下文。
/// <para>
/// wire 契约保持不变的三个机制：
/// 1. <see cref="TurnJsonOptions"/> / <see cref="DisplayState.JsonOpts"/> 加 <c>TypeInfoResolver = EmueraJsonContext.Default</c>，
///    直接序列化 TurnRecord/DisplaySnapshot 走源生成 TypeInfo，输出与反射版逐字节一致（同 WhenWritingNull、无 naming policy）；
/// 2. <see cref="TurnOpConverter"/>/<see cref="LineOpConverter"/> 的装箱 Write 递归 <c>Serialize((object)value, options)</c>
///    经 options.TypeInfoResolver 按运行时具体类型解析——此处显式注册全部多态叶子类型，保证 GetTypeInfo 命中；
/// 3. 无 options 的调用点（JSONConfig / JsonlCommand 反序列化）改显式 context 调用。
/// </para>
/// </summary>
[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(TurnRecord))]
[JsonSerializable(typeof(DisplayDiff))]
[JsonSerializable(typeof(DisplaySnapshot))]
[JsonSerializable(typeof(DisplayLine))]
[JsonSerializable(typeof(DisplayEntry))]
[JsonSerializable(typeof(PrintSegment))]
[JsonSerializable(typeof(SegmentImage))]
[JsonSerializable(typeof(SegmentCrop))]
[JsonSerializable(typeof(SegmentShape))]
[JsonSerializable(typeof(BgImageState))]
[JsonSerializable(typeof(ButtonRef))]
[JsonSerializable(typeof(JsonlCommand))]
[JsonSerializable(typeof(JSONConfigData))]
// LineOp 多态叶子——LineOpConverter.Write 按运行时类型解析 TypeInfo 所需（抽象基类 converter 已挂 options.Converters）
[JsonSerializable(typeof(AppendLinesOp))]
[JsonSerializable(typeof(ClearLineDiffOp))]
[JsonSerializable(typeof(ClearScreenOp))]
[JsonSerializable(typeof(ShiftHeadLineOp))]
// TurnOp 多态叶子——TurnOpConverter.Write 同上
[JsonSerializable(typeof(PrintOp))]
[JsonSerializable(typeof(NewLineOp))]
[JsonSerializable(typeof(ClearLineOp))]
[JsonSerializable(typeof(ClearOp))]
[JsonSerializable(typeof(SetBgOp))]
[JsonSerializable(typeof(SetBgImageOp))]
[JsonSerializable(typeof(RemoveBgImageOp))]
[JsonSerializable(typeof(ClearBgImageOp))]
[JsonSerializable(typeof(ShiftHeadTurnOp))]
internal partial class EmueraJsonContext : JsonSerializerContext
{
}
