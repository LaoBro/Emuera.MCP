using System.Collections.Generic;
using System.Threading.Tasks;
using MinorShift.Emuera.Runtime.Script.Data;
using MinorShift.Emuera.Runtime.Script.Loader;
using MinorShift.Emuera.Runtime.Script.Statements;
using MinorShift.Emuera.Runtime.Script.Statements.Function;
using Xunit;

namespace MinorShift.Emuera.Tests;

/// <summary>
/// ADR-0012 / Issue 02：ErbLoader 隔离单测。
/// 直接以显式依赖（console / exm / idDic / LoaderEnv / Action&lt;LogicalLine&gt; sink）构造，不持 Process 反向引用（F2）。
/// 下游 parser 经 ADR-0011 兼容层读 GlobalStatic——由 harness 的 Initialize 预热满足，非 ErbLoader 自身耦合。
/// 只验证可观察行为：标签装载、scaningLine sink 写入、返回值、空目录。
/// </summary>
[Collection("LoaderTests")]
public class ErbLoaderTests
{
	/// <summary>构造 ErbLoader + 一个记录 scaningLine 写入的 spy sink。</summary>
	private static ErbLoader NewLoaderWithSpy(LoaderTestHarness h, List<LogicalLine?> spy)
	{
		return new ErbLoader(h.Console, h.Exm, h.IdDic, h.Env, line => spy.Add(line));
	}

	[Fact]
	public async Task LoadErbDir_registers_event_label_and_returns_true()
	{
		using var h = new LoaderTestHarness();
		await h.InitializeAsync();
		h.WriteErb("EVENT.ERB", "@EVENTFOO\nRETURN\n");
		var spy = new List<LogicalLine?>();
		await h.PreloadAsync();
		var loader = NewLoaderWithSpy(h, spy);
		var labelDic = new LabelDictionary();

		var ok = await loader.LoadErbDir(h.ErbDir, displayReport: false, labelDic);

		Assert.True(ok);
		Assert.True(labelDic.Count > 0);
	}

	[Fact]
	public async Task LoadErbDir_scaningLine_sink_receives_the_label_line()
	{
		// ADR-0012 决策二：scaningLine 写经 sink 回调，不再直写 parentProcess.scaningLine。
		// spy 应在解析 @EVENTFOO 时收到该 FunctionLabelLine。
		using var h = new LoaderTestHarness();
		await h.InitializeAsync();
		h.WriteErb("EVENT.ERB", "@EVENTFOO\nRETURN\n");
		var spy = new List<LogicalLine?>();
		await h.PreloadAsync();
		var loader = NewLoaderWithSpy(h, spy);
		var labelDic = new LabelDictionary();

		await loader.LoadErbDir(h.ErbDir, displayReport: false, labelDic);

		Assert.Contains(spy, l => l is FunctionLabelLine fl && fl.LabelName == "EVENTFOO");
	}

	[Fact]
	public async Task LoadErbDir_empty_dir_returns_true_with_no_labels()
	{
		using var h = new LoaderTestHarness();
		await h.InitializeAsync();
		var spy = new List<LogicalLine?>();
		await h.PreloadAsync();
		var loader = NewLoaderWithSpy(h, spy);
		var labelDic = new LabelDictionary();

		var ok = await loader.LoadErbDir(h.ErbDir, displayReport: false, labelDic);

		Assert.True(ok);
		Assert.Equal(0, labelDic.Count);
	}

	[Fact]
	public async Task LoadErbDir_multiple_files_all_labels_registered()
	{
		using var h = new LoaderTestHarness();
		await h.InitializeAsync();
		h.WriteErb("A.ERB", "@FUNC_A\nRETURN\n");
		h.WriteErb("B.ERB", "@FUNC_B\nRETURN\n");
		var spy = new List<LogicalLine?>();
		await h.PreloadAsync();
		var loader = NewLoaderWithSpy(h, spy);
		var labelDic = new LabelDictionary();

		var ok = await loader.LoadErbDir(h.ErbDir, displayReport: false, labelDic);

		Assert.True(ok);
		Assert.True(labelDic.Count >= 2);
		Assert.Contains(spy, l => l is FunctionLabelLine fl && fl.LabelName == "FUNC_A");
		Assert.Contains(spy, l => l is FunctionLabelLine fl && fl.LabelName == "FUNC_B");
	}
}
