using System.Collections.Generic;
using System.Threading.Tasks;
using MinorShift.Emuera.Runtime.Script.Loader;
using MinorShift.Emuera.Runtime.Script.Statements.Variable;
using Xunit;

namespace MinorShift.Emuera.Tests;

/// <summary>
/// ADR-0012 / Issue 02：ErhLoader 隔离单测。
/// 直接以显式依赖（console / idDic / vEvaluator / LoaderEnv）构造，不持 Process 反向引用（F2）。
/// 不开 GlobalStatic.Process scope（ErhLoader 的 load 路径不读它；EE_ERD 由 UseERD=false 跳过）。
/// 只验证可观察行为：宏注册、返回值、错误路径。
/// </summary>
[Collection("LoaderTests")]
public class ErhLoaderTests
{
	[Fact]
	public async Task LoadHeaderFiles_registers_DEFINE_macro_and_returns_true()
	{
		using var h = new LoaderTestHarness();
		await h.InitializeAsync();
		h.WriteErh("MACROS.ERH", "#DEFINE FOO (1+2)\n");
		await h.PreloadAsync();
		var loader = new ErhLoader(h.Console, h.IdDic, h.VEvaluator, h.Env);

		var ok = loader.LoadHeaderFiles(h.ErbDir, displayReport: false);

		Assert.True(ok);
		Assert.NotNull(h.IdDic.GetMacro("FOO"));
	}

	[Fact]
	public async Task LoadHeaderFiles_empty_dir_returns_true_with_no_macros()
	{
		using var h = new LoaderTestHarness();
		await h.InitializeAsync();
		await h.PreloadAsync();
		var loader = new ErhLoader(h.Console, h.IdDic, h.VEvaluator, h.Env);

		var ok = loader.LoadHeaderFiles(h.ErbDir, displayReport: false);

		Assert.True(ok);
	}

	[Fact]
	public async Task LoadHeaderFiles_malformed_line_without_sharp_returns_false()
	{
		// ERH 每个有效行必须以 '#' 开头；裸行触发 CodeEE → 警告 → 返回 false。
		using var h = new LoaderTestHarness();
		await h.InitializeAsync();
		h.WriteErh("BAD.ERH", "NOT_A_SHARP_LINE\n");
		await h.PreloadAsync();
		var loader = new ErhLoader(h.Console, h.IdDic, h.VEvaluator, h.Env);

		var ok = loader.LoadHeaderFiles(h.ErbDir, displayReport: false);

		Assert.False(ok);
	}

	[Fact]
	public async Task LoadHeaderFiles_multiple_DEFINE_all_registered()
	{
		using var h = new LoaderTestHarness();
		await h.InitializeAsync();
		h.WriteErh("MULTI.ERH", "#DEFINE ALPHA (1)\n#DEFINE BETA (2)\n#DEFINE GAMMA (3)\n");
		await h.PreloadAsync();
		var loader = new ErhLoader(h.Console, h.IdDic, h.VEvaluator, h.Env);

		var ok = loader.LoadHeaderFiles(h.ErbDir, displayReport: false);

		Assert.True(ok);
		Assert.NotNull(h.IdDic.GetMacro("ALPHA"));
		Assert.NotNull(h.IdDic.GetMacro("BETA"));
		Assert.NotNull(h.IdDic.GetMacro("GAMMA"));
	}
}
