using System;
using System.Threading.Tasks;
using MinorShift.Emuera.GameData.Variable;
using MinorShift.Emuera.Runtime.Script.Statements;
using MinorShift.Emuera.Runtime.Script.Statements.Variable;
using MinorShift.Emuera.UI.Game;
using Xunit;

namespace MinorShift.Emuera.Tests;

/// <summary>
/// 3.2 热路径减分配（S2/S3/S4）的语义等价回归：
/// - GetJoinedStr：2D/3D 与 int 数组路径由 += 改为 StringBuilder + 复用索引缓冲（1D string 的 string.Join 分支保留）；
/// - Html2PlainText：改走 RegexFactory 静态缓存；
/// - CheckEscape：无转义快速路径。
/// STRJOIN 用例遵循仓库「内部 API 直调」模式：用 StubVariableToken（InternalsVisibleTo 子类化）控制
/// 维度/类型/取值，直调 VariableEvaluator.GetJoinedStr。
/// </summary>
[Collection("LoaderTests")]
public sealed class HotPathTests
{
	// ---------- CheckEscape（public static 直调） ----------

	[Fact]
	public void CheckEscape_null_returns_empty()
	{
		Assert.Equal("", ExpressionMediator.CheckEscape(null!));
	}

	[Fact]
	public void CheckEscape_no_escape_returns_original_string()
	{
		const string s = "plain text 123, 日本語";
		// 快速路径直接返回原引用（string 不可变，引用安全）。
		Assert.Same(s, ExpressionMediator.CheckEscape(s));
	}

	[Fact]
	public void CheckEscape_empty_string_returns_same()
	{
		Assert.Same("", ExpressionMediator.CheckEscape(""));
	}

	[Fact]
	public void CheckEscape_escaped_brace_preserved()
	{
		// \{ 属合法转义：case '{' 分支原样保留
		Assert.Equal("a\\{b}", ExpressionMediator.CheckEscape("a\\{b}"));
	}

	[Fact]
	public void CheckEscape_backslash_pair_preserved()
	{
		// \\ 属合法转义：case '\\' 分支原样保留（输入 2 反斜杠 → 输出 2 反斜杠）
		Assert.Equal("\\\\", ExpressionMediator.CheckEscape("\\\\"));
	}

	[Fact]
	public void CheckEscape_unknown_escape_duplicates_backslash()
	{
		// \a（未知转义）→ default 分支：双倍反斜杠 → \\a
		Assert.Equal("\\\\a", ExpressionMediator.CheckEscape("\\a"));
	}

	[Fact]
	public void CheckEscape_percent_and_at_preserved()
	{
		// \% \@ 属合法转义：原样保留
		Assert.Equal("\\%\\@", ExpressionMediator.CheckEscape("\\%\\@"));
	}

	[Fact]
	public void CheckEscape_trailing_backslash_goes_escape_path()
	{
		// 尾部反斜杠为既有行为（走转义路径，输出含两个反斜杠），锁定现状不"顺手修"。
		string ret = ExpressionMediator.CheckEscape("ab\\");
		Assert.Contains("\\\\", ret);
		Assert.StartsWith("ab", ret);
	}

	// ---------- Html2PlainText（public static 直调） ----------

	[Fact]
	public void Html2PlainText_strips_nested_tags()
	{
		Assert.Equal("bold text", HtmlManager.Html2PlainText("<b><i>bold</i></b> text"));
	}

	[Fact]
	public void Html2PlainText_no_tags_unchanged()
	{
		Assert.Equal("plain text", HtmlManager.Html2PlainText("plain text"));
	}

	[Fact]
	public void Html2PlainText_unclosed_tag_keeps_open_text()
	{
		Assert.Equal("only", HtmlManager.Html2PlainText("<b>only"));
	}

	[Fact]
	public void Html2PlainText_unescapes_entities()
	{
		// 实体形式的标签不含字面 <，正则不剥除；Unescape 仍执行。
		Assert.Equal("<b>", HtmlManager.Html2PlainText("&lt;b&gt;"));
		Assert.Equal("a & b", HtmlManager.Html2PlainText("a &amp; b"));
	}

	[Fact]
	public void Html2PlainText_empty_string()
	{
		Assert.Equal("", HtmlManager.Html2PlainText(""));
	}

	// ---------- STRJOIN / GetJoinedStr（StubVariableToken 直调） ----------

	[Fact]
	public async Task StrJoin_2DInt_multiCharDelimiter()
	{
		using var h = new LoaderTestHarness();
		await h.InitializeAsync();
		var fvp = StubInt(h.VEvaluator.VariableData, 2, args => args[0] * 10 + args[1]);
		fvp.Index1 = 5;
		// args = [5, 1], [5, 2], [5, 3] → 51, 52, 53
		Assert.Equal("51::52::53", VariableEvaluator.GetJoinedStr(fvp, "::", 1, 3));
	}

	[Fact]
	public async Task StrJoin_2DInt_emptyDelimiter()
	{
		using var h = new LoaderTestHarness();
		await h.InitializeAsync();
		var fvp = StubInt(h.VEvaluator.VariableData, 2, args => args[1] + 1);
		fvp.Index1 = 0;
		Assert.Equal("123", VariableEvaluator.GetJoinedStr(fvp, "", 0, 3));
	}

	[Fact]
	public async Task StrJoin_2DInt_length1_noTrailingDelimiter()
	{
		using var h = new LoaderTestHarness();
		await h.InitializeAsync();
		var fvp = StubInt(h.VEvaluator.VariableData, 2, args => args[0] * 10 + args[1]);
		fvp.Index1 = 5;
		Assert.Equal("51", VariableEvaluator.GetJoinedStr(fvp, "::", 1, 1));
	}

	[Fact]
	public async Task StrJoin_2DInt_length0_empty()
	{
		using var h = new LoaderTestHarness();
		await h.InitializeAsync();
		var fvp = StubInt(h.VEvaluator.VariableData, 2, args => args[0] * 10 + args[1]);
		fvp.Index1 = 5;
		Assert.Equal("", VariableEvaluator.GetJoinedStr(fvp, "::", 1, 0));
	}

	[Fact]
	public async Task StrJoin_2DInt_negativeValues()
	{
		using var h = new LoaderTestHarness();
		await h.InitializeAsync();
		var fvp = StubInt(h.VEvaluator.VariableData, 2, args => args[1] == 0 ? -5 : -2);
		fvp.Index1 = 0;
		Assert.Equal("-5::-2", VariableEvaluator.GetJoinedStr(fvp, "::", 0, 2));
	}

	[Fact]
	public async Task StrJoin_3DInt_roundtrip()
	{
		using var h = new LoaderTestHarness();
		await h.InitializeAsync();
		var fvp = StubInt(h.VEvaluator.VariableData, 3, args => 100 * args[0] + 10 * args[1] + args[2]);
		fvp.Index1 = 1;
		fvp.Index2 = 2;
		// args = [1, 2, 0], [1, 2, 1] → 120, 121
		Assert.Equal("120::121", VariableEvaluator.GetJoinedStr(fvp, "::", 0, 2));
	}

	[Fact]
	public async Task StrJoin_2DString_roundtrip()
	{
		using var h = new LoaderTestHarness();
		await h.InitializeAsync();
		var fvp = StubString(h.VEvaluator.VariableData, 2, args => "v" + args[1]);
		fvp.Index1 = 0;
		Assert.Equal("v1::v2", VariableEvaluator.GetJoinedStr(fvp, "::", 1, 2));
	}

	[Fact]
	public async Task StrJoin_2DString_nullElement_treated_as_empty()
	{
		using var h = new LoaderTestHarness();
		await h.InitializeAsync();
		// 元素 GetStrValue 返回 null：null 当作空串占位（null + delimiter = delimiter），
		// 故 null 元素两侧 delimiter 紧邻 → "v0" + "::" + (null→"") + "::" + "v2" = "v0::::v2"。
		var fvp = StubString(h.VEvaluator.VariableData, 2, args => args[1] == 1 ? null! : "v" + args[1]);
		fvp.Index1 = 0;
		Assert.Equal("v0::::v2", VariableEvaluator.GetJoinedStr(fvp, "::", 0, 3));
	}

	[Fact]
	public async Task StrJoin_1DString_stringJoin_regression()
	{
		using var h = new LoaderTestHarness();
		await h.InitializeAsync();
		// IsString + IsArray1D 走保留的 string.Join 分支。
		var fvp = StubString(h.VEvaluator.VariableData, 1, null, ["a", "b", "c"]);
		Assert.Equal("a,b", VariableEvaluator.GetJoinedStr(fvp, ",", 0, 2));
	}

	// ---------- helpers ----------

	private static FixedVariableTerm StubInt(VariableData varData, int dimension, Func<long[], long> reader)
		=> new(new StubVariableToken(varData, VariableCode.__INTEGER__ | DimFlag(dimension), reader, null, null));

	private static FixedVariableTerm StubString(VariableData varData, int dimension, Func<long[], string>? reader, string[]? array1D = null)
		=> new(new StubVariableToken(varData, VariableCode.__STRING__ | DimFlag(dimension), null, reader, array1D));

	private static VariableCode DimFlag(int dimension)
		=> dimension switch
		{
			1 => VariableCode.__ARRAY_1D__,
			2 => VariableCode.__ARRAY_2D__,
			_ => VariableCode.__ARRAY_3D__,
		};

	/// <summary>可控的 VariableToken 桩：按测试需要覆写 GetIntValue/GetStrValue/GetArray。</summary>
	private sealed class StubVariableToken : VariableToken
	{
		private readonly Func<long[], long>? _intReader;
		private readonly Func<long[], string>? _strReader;
		private readonly string[]? _array1D;

		public StubVariableToken(VariableData varData, VariableCode code, Func<long[], long>? intReader, Func<long[], string>? strReader, string[]? array1D)
			: base(code, varData)
		{
			_intReader = intReader;
			_strReader = strReader;
			_array1D = array1D;
		}

		public override long GetIntValue(ExpressionMediator exm, long[] arguments)
			=> _intReader?.Invoke(arguments) ?? 0;

		public override string GetStrValue(ExpressionMediator exm, long[] arguments)
			// 刻意允许返回 null 以覆盖引擎"元素为 null"的语义（原 null + delimiter = delimiter，即空串占位）。
			=> _strReader?.Invoke(arguments)!;

		public override object GetArray()
			=> _array1D ?? base.GetArray();
	}
}
