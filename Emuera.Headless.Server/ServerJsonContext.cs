using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace MinorShift.Emuera.Server;

/// <summary>
/// Server（Kestrel）程序集的 System.Text.Json 源生成上下文（3.3 NativeAOT 硬性改造）。
/// Core 的 EmueraJsonContext 在另一程序集且 Core 不反向引用 Server——Server 侧的
/// 反序列化类型（HTTP body）必须由本 context 注册。响应侧统一用 JsonObject：
/// JsonObject 需注册（Results.Json 走 HttpJsonOptions 的 resolver chain，AOT 下
/// 反射 resolver 被禁用，见 KestrelGameServer 的 ConfigureHttpJsonOptions）。
/// Encoder 不在此设置（.NET 10 source-gen options 无 encoder 属性）——relaxed 转义
/// 由 HttpJsonOptions 默认（UnsafeRelaxedJsonEscaping）保证，与托管时代 wire 一致。
/// </summary>
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(JsonObject))]
[JsonSerializable(typeof(KestrelGameServer.HttpInput))]
[JsonSerializable(typeof(KestrelGameServer.ControlRequest))]
[JsonSerializable(typeof(KestrelGameServer.LoadGameRequest))]
internal partial class ServerJsonContext : JsonSerializerContext
{
}
