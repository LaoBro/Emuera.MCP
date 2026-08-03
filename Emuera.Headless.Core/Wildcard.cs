using System;

namespace MinorShift.Emuera;

/// <summary>
/// 简单通配符匹配（O3，saf-accel 计划）——仅 <c>*</c> 与 <c>?</c> 特殊，
/// 其余字符（含 <c>.</c> <c>+</c> <c>(</c> 等正则元字符）按字面匹配；大小写不敏感；零分配。
/// <para>
/// 语义与原 SAF 实现（Regex.Escape + <c>*→.*</c>、<c>?→.</c> + IgnoreCase 锚定）等价：
/// <c>*</c> 匹配任意字符串（含空串），<c>?</c> 恰匹配 1 个字符。
/// 手写两指针带回溯，避免命中路径每子项一次 <c>new Regex</c>（构造 + 编译分配）。
/// </para>
/// </summary>
internal static class Wildcard
{
    /// <param name="input">被匹配的字符串；null 恒返回 false（保留原 MatchWildcard 语义）。</param>
    /// <param name="pattern">通配模式。</param>
    public static bool Matches(string? input, string pattern)
    {
        if (input == null) return false;

        // 经典带回溯的两指针匹配：star 记录最近一次 '*' 的 pattern 下标，
        // starMatch 记录该 '*' 之后已尝试消费的 input 下标（失败时回退重试）。
        int i = 0;
        int j = 0;
        int star = -1;
        int starMatch = 0;
        while (i < input.Length)
        {
            if (j < pattern.Length && (pattern[j] == '?' || CharEqualsIgnoreCase(input[i], pattern[j])))
            {
                i++;
                j++;
            }
            else if (j < pattern.Length && pattern[j] == '*')
            {
                star = j;
                starMatch = i;
                j++;
            }
            else if (star >= 0)
            {
                starMatch++;
                i = starMatch;
                j = star + 1;
            }
            else
            {
                return false;
            }
        }
        while (j < pattern.Length && pattern[j] == '*')
            j++;
        return j == pattern.Length;
    }

    private static bool CharEqualsIgnoreCase(char a, char b)
        => a == b || char.ToUpperInvariant(a) == char.ToUpperInvariant(b);
}
