using Xunit;

namespace MinorShift.Emuera.Tests;

/// <summary>
/// O3：<see cref="Wildcard"/> 通配匹配语义（* / ?，大小写不敏感）。
/// 对照原 SAF Regex 实现（Escape + IgnoreCase 锚定）逐条锁定行为。
/// </summary>
public class WildcardTests
{
    [Fact]
    public void star_matches_empty_input()
    {
        Assert.True(Wildcard.Matches("", "*"));
    }

    [Fact]
    public void star_matches_any_input()
    {
        Assert.True(Wildcard.Matches("abc.txt", "*"));
    }

    [Fact]
    public void empty_pattern_matches_only_empty_input()
    {
        Assert.True(Wildcard.Matches("", ""));
        Assert.False(Wildcard.Matches("a", ""));
    }

    [Fact]
    public void question_matches_single_char()
    {
        Assert.True(Wildcard.Matches("a", "?"));
        Assert.False(Wildcard.Matches("ab", "?"));
    }

    [Fact]
    public void question_does_not_match_empty()
    {
        Assert.False(Wildcard.Matches("", "?"));
    }

    [Fact]
    public void exact_pattern_is_case_insensitive()
    {
        Assert.True(Wildcard.Matches("GLOBAL.SAV", "global.sav"));
        Assert.True(Wildcard.Matches("global.sav", "GLOBAL.SAV"));
    }

    [Fact]
    public void mixed_star_and_question()
    {
        Assert.True(Wildcard.Matches("a1bc", "a?b*"));
        Assert.False(Wildcard.Matches("abc", "a?b*"));
    }

    [Fact]
    public void multiple_stars()
    {
        Assert.True(Wildcard.Matches("abxxxc", "a**c"));
        Assert.True(Wildcard.Matches("ac", "a*c"));
    }

    [Fact]
    public void trailing_star_consumes_rest()
    {
        Assert.True(Wildcard.Matches("a", "a*"));
        Assert.True(Wildcard.Matches("a-very-long-name.ERB", "a*"));
    }

    [Fact]
    public void null_input_returns_false()
    {
        Assert.False(Wildcard.Matches(null, "*"));
        Assert.False(Wildcard.Matches(null, ""));
    }

    [Fact]
    public void dot_and_regex_chars_are_literal()
    {
        // 原实现走 Regex.Escape：. + ( 等按字面匹配，不作正则元字符
        Assert.True(Wildcard.Matches("a.b", "a.b"));
        Assert.False(Wildcard.Matches("axb", "a.b"));
        Assert.True(Wildcard.Matches("c(d).txt", "c(d)*"));
    }

    [Fact]
    public void unicode_name_matches_by_char()
    {
        Assert.True(Wildcard.Matches("中文", "??"));
        Assert.False(Wildcard.Matches("中文", "?"));
    }
}
