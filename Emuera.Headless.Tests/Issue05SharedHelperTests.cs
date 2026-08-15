using System;
using System.Linq;
using MinorShift.Emuera.GameData.Function;
using MinorShift.Emuera.Runtime.Config;
using Xunit;

namespace MinorShift.Emuera.Tests;

/// <summary>
/// Issue 05：公共辅助提取后的直接单元测试。
/// </summary>
public sealed class Issue05SharedHelperTests
{
	[Fact]
	public void ArrayReorder_1D_long_reorders_by_index()
	{
		using var scope = GlobalStatic.OpenScope(new ConfigData());
		var array = new long[] { 30, 10, 20 };
		Assert.True(FunctionMethodCreator.ArrayReorder(array, new[] { 1, 2, 0 }));
		Assert.Equal(new long[] { 10, 20, 30 }, array);
	}

	[Fact]
	public void ArrayReorder_1D_string_reorders_by_index()
	{
		using var scope = GlobalStatic.OpenScope(new ConfigData());
		var array = new string[] { "c", "a", "b" };
		Assert.True(FunctionMethodCreator.ArrayReorder(array, new[] { 1, 2, 0 }));
		Assert.Equal(new string[] { "a", "b", "c" }, array);
	}

	[Fact]
	public void ArrayReorder_2D_long_reorders_first_dimension()
	{
		using var scope = GlobalStatic.OpenScope(new ConfigData());
		var array = new long[,] { { 30, 31 }, { 10, 11 }, { 20, 21 } };
		Assert.True(FunctionMethodCreator.ArrayReorder(array, new[] { 1, 2, 0 }));
		Assert.Equal(new long[,] { { 10, 11 }, { 20, 21 }, { 30, 31 } }.Cast<long>(), array.Cast<long>());
	}

	[Fact]
	public void ArrayReorder_2D_string_reorders_first_dimension()
	{
		using var scope = GlobalStatic.OpenScope(new ConfigData());
		var array = new string[,] { { "c1", "c2" }, { "a1", "a2" }, { "b1", "b2" } };
		Assert.True(FunctionMethodCreator.ArrayReorder(array, new[] { 1, 2, 0 }));
		Assert.Equal(new string[,] { { "a1", "a2" }, { "b1", "b2" }, { "c1", "c2" } }.Cast<string>(), array.Cast<string>());
	}

	[Fact]
	public void ArrayReorder_3D_long_reorders_first_dimension()
	{
		using var scope = GlobalStatic.OpenScope(new ConfigData());
		var array = new long[,,]
		{
			{ { 300, 301 }, { 310, 311 } },
			{ { 100, 101 }, { 110, 111 } },
			{ { 200, 201 }, { 210, 211 } },
		};
		Assert.True(FunctionMethodCreator.ArrayReorder(array, new[] { 1, 2, 0 }));
		var expected = new long[,,]
		{
			{ { 100, 101 }, { 110, 111 } },
			{ { 200, 201 }, { 210, 211 } },
			{ { 300, 301 }, { 310, 311 } },
		};
		Assert.Equal(expected.Cast<long>(), array.Cast<long>());
	}

	[Fact]
	public void ArrayReorder_3D_string_reorders_first_dimension()
	{
		using var scope = GlobalStatic.OpenScope(new ConfigData());
		var array = new string[,,]
		{
			{ { "c0", "c1" }, { "c2", "c3" } },
			{ { "a0", "a1" }, { "a2", "a3" } },
			{ { "b0", "b1" }, { "b2", "b3" } },
		};
		Assert.True(FunctionMethodCreator.ArrayReorder(array, new[] { 1, 2, 0 }));
		var expected = new string[,,]
		{
			{ { "a0", "a1" }, { "a2", "a3" } },
			{ { "b0", "b1" }, { "b2", "b3" } },
			{ { "c0", "c1" }, { "c2", "c3" } },
		};
		Assert.Equal(expected.Cast<string>(), array.Cast<string>());
	}

	[Fact]
	public void ArrayReorder_returns_false_without_mutation_when_target_is_too_short()
	{
		using var scope = GlobalStatic.OpenScope(new ConfigData());
		var array = new long[] { 1, 2 };
		Assert.False(FunctionMethodCreator.ArrayReorder(array, new[] { 0, 1, 2 }));
		Assert.Equal(new long[] { 1, 2 }, array);
	}
}
