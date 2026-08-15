using Xunit;

namespace Emuera.Headless.Tests;

/// <summary>
/// 性能敏感测试的串行隔离集合。
/// BuildSnapshotPerformanceTests 用 Stopwatch 卡毫秒级阈值，若与其它 CPU 密集测试类并行，
/// 会受到线程调度/争抢影响而偶发超阈值（完整套件下 100/1000/5000 行均可能失败）。
/// DisableParallelization=true 使集合内测试与其他所有测试串行执行，保证计时稳定。
/// </summary>
[CollectionDefinition("PerformanceIsolated", DisableParallelization = true)]
public class PerformanceIsolatedCollection
{
}
