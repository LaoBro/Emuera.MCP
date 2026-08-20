using MinorShift.Emuera.GameView;
using Xunit;

namespace Emuera.Headless.Tests;

/// <summary>
/// EmueraLog 门面单测（T-027 Phase 1）。
/// 只测纯逻辑（级别映射 / 阈值判定），不触碰 Console / 文件 / 全局状态，可并行。
/// 环境无关：MinLevel / TerminalLevel 期望值由当前进程环境变量经 ParseLevel 推导——
/// 未设 EMUERA_LOG_* 时即默认（MinLevel=Debug、TerminalLevel=Warn）。
/// </summary>
public class EmueraLogTests
{
    private static readonly EmueraLog.Level MinLevel =
        EmueraLog.ParseLevel("EMUERA_LOG_LEVEL", EmueraLog.Level.Debug);
    private static readonly EmueraLog.Level TerminalLevel =
        EmueraLog.ParseLevel("EMUERA_LOG_TERMINAL", EmueraLog.Level.Warn);

    [Fact]
    public void Default_config_keeps_debug_in_file_but_hides_from_terminal()
    {
        // 存档类诊断（Debug，T-027 痛点）应记录到文件（IsEnabled）但不进终端（IsTerminalVisible）
        Assert.True(EmueraLog.IsEnabled(EmueraLog.Level.Debug), "Debug 应记录（agent.log 全量）");
        Assert.False(EmueraLog.IsTerminalVisible(EmueraLog.Level.Debug), "Debug 不应显示到终端（默认阈值 Warn）");
        Assert.Equal(EmueraLog.Level.Debug, MinLevel);
        Assert.Equal(EmueraLog.Level.Warn, TerminalLevel);
    }

    [Fact]
    public void Warn_and_error_are_visible_on_terminal()
    {
        Assert.True(EmueraLog.IsTerminalVisible(EmueraLog.Level.Warn));
        Assert.True(EmueraLog.IsTerminalVisible(EmueraLog.Level.Error));
    }

    [Fact]
    public void Info_below_terminal_threshold_is_hidden()
    {
        // 加载诊断（Info）默认不进终端
        Assert.True(EmueraLog.IsEnabled(EmueraLog.Level.Info));
        Assert.False(EmueraLog.IsTerminalVisible(EmueraLog.Level.Info));
    }

    [Fact]
    public void Off_disables_everything()
    {
        Assert.False(EmueraLog.IsEnabled(EmueraLog.Level.Off));
        Assert.False(EmueraLog.IsTerminalVisible(EmueraLog.Level.Off));
    }

    [Theory]
    [InlineData("debug", "Debug")]
    [InlineData("info", "Info")]
    [InlineData("warn", "Warn")]
    [InlineData("warning", "Warn")]
    [InlineData("error", "Error")]
    [InlineData("off", "Off")]
    [InlineData("none", "Off")]
    [InlineData("0", "Off")]
    [InlineData("false", "Off")]
    public void ParseLevelValue_maps_known_values(string value, string expectedName)
    {
        var actual = EmueraLog.ParseLevelValue(value, EmueraLog.Level.Info);
        Assert.Equal(expectedName, actual.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("garbage")]
    [InlineData("VERBOSE")]
    public void ParseLevelValue_falls_back_on_unknown(string? value)
    {
        Assert.Equal(EmueraLog.Level.Info, EmueraLog.ParseLevelValue(value, EmueraLog.Level.Info));
    }
}
