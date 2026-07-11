using System;
using System.Threading.Tasks;
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
    /// T-c：单赋值断言契约——scope 内二次 set 同字段抛 InvalidOperationException。
    /// GameBase 有无参构造且无静态依赖，适合用于测试。
    /// </summary>
    [Fact]
    public void Second_set_of_same_core_field_throws_InvalidOperation()
    {
        using var scope = GlobalStatic.OpenScope(new ConfigData());

        GlobalStatic.GameBaseData = new GameBase();

        Assert.Throws<InvalidOperationException>(() =>
            GlobalStatic.GameBaseData = new GameBase());
    }
}
