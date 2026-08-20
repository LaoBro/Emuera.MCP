namespace MinorShift.Emuera.Server;

/// <summary>
/// 输出广播抽象——供 <see cref="HttpSessionIO"/> 解耦对 <see cref="OutputHub"/> 具体类型的依赖。
///
/// 动机（issue 03 / ID4）： <see cref="OutputHub"/> 是 <c>internal sealed</c>，
/// 无法继承做 mock，<see cref="HttpSessionIO"/> 直接持有它就无法在单测中替换广播行为。
/// 抽出此接口后，<see cref="HttpSessionIO"/> 构造函数接 <see cref="IOutputBroadcaster"/>，
/// 测试可注入 mock 实现；<see cref="OutputHub"/> 仍是唯一生产实现。
///
/// 注意：本接口**仅为 <see cref="HttpSessionIO"/> 可测性服务**，<see cref="OutputHub"/> 是唯一生产实现
/// （HTTP 与 MAUI 托管共用；原 MauiBridgeIO 已随 issue 05 托管架构删除）。
/// </summary>
internal interface IOutputBroadcaster
{
    /// <summary>
    /// 向所有当前订阅者各推送一次 turn。已 <see cref="Complete"/> 后无操作。
    /// </summary>
    void Publish(string turn);

    /// <summary>
    /// 结束整个广播：完成所有订阅者 Channel 并清空。幂等。
    /// 由 <see cref="HttpSessionIO.Close"/> 在 session 结束时调用。
    /// </summary>
    void Complete();
}
