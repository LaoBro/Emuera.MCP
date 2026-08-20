using System;
using System.Threading.Tasks;
using MinorShift.Emuera.GameData.Variable;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Runtime.Script.Data;
using Xunit;

namespace MinorShift.Emuera.Tests;

public class GlobalStaticScopeTests
{
    /// <summary>
    /// T-a：scope 生命周期契约——OpenScope 后 Current != null，Dispose 后 Current == null。
    /// </summary>
    [Fact]
    public void OpenScope_sets_Current_and_Dispose_clears_it()
    {
        Assert.Null(GlobalStatic.Current);

        using (var scope = GlobalStatic.OpenScope(new ConfigData()))
        {
            Assert.NotNull(GlobalStatic.Current);
        }

        Assert.Null(GlobalStatic.Current);
    }

    /// <summary>
    /// T-b：并行 scope 隔离契约——两并行 task 各自开 scope、各设 ForceQuitAndRestart 不同值，互不污染。
    /// 验证 AsyncLocal 在并行 async 上下文间的隔离性。
    /// </summary>
    [Fact]
    public async Task Parallel_scopes_do_not_pollute_each_other()
    {
        // 不在测试线程开 scope——子 task 各自开，避免 AsyncLocal 继承
        var results = await Task.WhenAll(
            Task.Run(async () =>
            {
                using var scope = GlobalStatic.OpenScope(new ConfigData());
                GlobalStatic.ForceQuitAndRestart = true;
                await Task.Yield(); // 让出调度，模拟真实并发
                return GlobalStatic.ForceQuitAndRestart;
            }),
            Task.Run(async () =>
            {
                using var scope = GlobalStatic.OpenScope(new ConfigData());
                GlobalStatic.ForceQuitAndRestart = false;
                await Task.Yield();
                return GlobalStatic.ForceQuitAndRestart;
            })
        );

        Assert.True(results[0]);  // task 1 设 true，不受 task 2 影响
        Assert.False(results[1]); // task 2 设 false，不受 task 1 影响
        Assert.Null(GlobalStatic.Current); // 测试线程未被污染
    }

    /// <summary>
    /// T-c：ADR-0011 只读转发契约——6 个引擎字段（GameBaseData/ConstantData/VEvaluator/
    /// IdentifierDictionary/EMediator/LabelDictionary）自候选 3 起收归 Process 实例，
    /// GlobalStatic 仅保留只读转发属性，不再拥有 setter（写入路径已无残留）。
    /// 单赋值语义现由 Process 实例在 Initialize 内持有。
    /// </summary>
    [Fact]
    public void Engine_fields_are_read_only_forwarding()
    {
        using var scope = GlobalStatic.OpenScope(new ConfigData());

        var engineFieldNames = new[]
        {
            "GameBaseData", "ConstantData", "VEvaluator",
            "IdentifierDictionary", "EMediator", "LabelDictionary"
        };
        foreach (var name in engineFieldNames)
        {
            var prop = typeof(GlobalStatic).GetProperty(name)!;
            Assert.Null(prop.GetSetMethod()); // 只读转发，无 setter
        }
    }
}
