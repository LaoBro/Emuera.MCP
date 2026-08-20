using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace MinorShift.Emuera.Server;

/// <summary>
/// Server（Kestrel）程序集的 System.Text.Json 源生成上下文（3.3 NativeAOT 硬性改造）。
/// Core 的 EmueraJsonContext 在另一程序集且 Core 不反向引用 Server——Server 侧的
/// 反序列化类型（HTTP body）必须由本 context 注册。响应侧统一用 JsonObject。
/// Encoder 不在此设置（.NET 10 source-gen options 无 encoder 属性）——relaxed 转义
/// 由 HttpJsonOptions 默认（UnsafeRelaxedJsonEscaping）保证，与托管时代 wire 一致。
///
/// C1 后：/input、/load-game、/control/acquire 的 body POCO（<see cref="HttpRouteDispatcher"/>.HttpInput/
/// ControlRequest/LoadGameRequest）上收 Core 的 <see cref="HttpRouteDispatcher"/>；Kestrel 把本
/// context 注入 dispatcher 作源生成反序列化（NativeAOT 安全 + 大小写不敏感）。故此处须注册
/// 三个请求 POCO（Core 经 InternalsVisibleTo 对本程序集可见）。
/// </summary>
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(JsonObject))]
[JsonSerializable(typeof(HttpRouteDispatcher.HttpInput))]
[JsonSerializable(typeof(HttpRouteDispatcher.ControlRequest))]
[JsonSerializable(typeof(HttpRouteDispatcher.LoadGameRequest))]
internal partial class ServerJsonContext : JsonSerializerContext
{
}
