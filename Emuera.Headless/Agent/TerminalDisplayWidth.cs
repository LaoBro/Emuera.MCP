using System;

namespace MinorShift.Emuera.GameView;

internal static class TerminalDisplayWidth
{
    /// <summary>
    /// 终端中 Ambiguous Width 字符（Box Drawing、Geometric Shapes 等）是否按全角渲染。
    /// 默认 true（全角），通过 DetectAmbiguousWidth() 检测实际值。
    /// </summary>
    internal static bool AmbiguousIsWide = true;

    internal static int GetDisplayWidth(string str)
    {
        int width = 0;
        foreach (char c in str)
            width += IsWideChar(c) ? 2 : 1;
        return width;
    }

    /// <summary>
    /// 判断字符是否为在终端中渲染为半角的 Ambiguous Width 字符。
    /// 仅 Box Drawing (U+2500-257F) 在 cmd/PowerShell 中渲染为半角，
    /// Geometric Shapes 和 Misc Symbols 在终端中仍为全角，无需补空格。
    /// </summary>
    internal static bool IsAmbiguousChar(char c)
    {
        // Box Drawing (U+2500-257F): ━┃┏┗┛┓┣┫┳┻╋ 等
        if (c >= 0x2500 && c <= 0x257F) return true;
        return false;
    }

    internal static bool IsWideChar(char c)
    {
        // CJK 统一汉字、韩文、兼容汉字 - 始终全角
        if ((c >= 0x2E80 && c <= 0x9FFF)
         || (c >= 0xAC00 && c <= 0xD7AF)
         || (c >= 0xF900 && c <= 0xFAFF))
            return true;

        // 全角 ASCII 变体、全角符号 - 始终全角
        if ((c >= 0xFF01 && c <= 0xFF60)
         || (c >= 0xFFE0 && c <= 0xFFE6))
            return true;

        // CJK Compatibility Forms (U+FE30-FE4F) - 始终全角
        if (c >= 0xFE30 && c <= 0xFE4F) return true;

        // Ambiguous Width 字符 - 游戏设计为全角，始终按全角计算宽度
        // 当终端渲染为半角时，由 ReplaceForTerminal 插入空格补齐
        if (IsAmbiguousChar(c)) return true;

        // Block Elements (U+2580-259F): ░▒▓█
        // 游戏脚本中按半角使用，始终标记为半角
        return false;
    }

    /// <summary>
    /// 检测当前终端中 Ambiguous Width 字符的实际渲染宽度。
    /// 写入 Box Drawing 测试字符，通过光标位移判断全角/半角。
    /// </summary>
    internal static void DetectAmbiguousWidth()
    {
        if (Console.IsOutputRedirected) return;

        try
        {
            int left = Console.CursorLeft;
            int top = Console.CursorTop;

            // 确保当前行有足够空间（至少4列），避免换行干扰
            if (Console.WindowWidth - left < 4)
            {
                Console.WriteLine();
                left = 0;
                top = Console.CursorTop;
            }

            Console.SetCursorPosition(left, top);
            Console.Write('\u2501'); // ━ (Box Drawing Heavy Horizontal)

            int newLeft = Console.CursorLeft;
            int delta = newLeft - left;

            // 清除测试输出
            Console.SetCursorPosition(left, top);
            Console.Write("  ");
            Console.SetCursorPosition(left, top);

            // delta >= 2 表示全角，1 表示半角
            AmbiguousIsWide = delta >= 2;

            Console.Error.WriteLine($"[terminal] Ambiguous width: ━ cursor delta={delta}, wide={AmbiguousIsWide}");
        }
        catch
        {
            // 检测失败，保持默认值（全角）
            Console.Error.WriteLine("[terminal] Ambiguous width detection failed, defaulting to wide=true");
        }
    }

    /// <summary>
    /// 将终端中无法正确渲染的字符替换为等价字符，并在需要时补齐宽度。
    /// 1. Block Elements (░▒▓) 始终替换为 ASCII 半角字符（视觉兼容性）。
    /// 2. 当终端将 Ambiguous Width 字符渲染为半角时，在每个此类字符后插入半角空格，
    ///    使其视觉宽度与游戏设计的全角宽度一致（字符1列 + 空格1列 = 2列）。
    /// </summary>
    internal static string ReplaceForTerminal(string str)
    {
        if (str.Length == 0) return str;

        bool needsAmbiguousPad = !AmbiguousIsWide;
        bool needsReplace = false;

        foreach (char c in str)
        {
            if (c >= 0x2580 && c <= 0x259F)
            { needsReplace = true; break; }
            if (needsAmbiguousPad && IsAmbiguousChar(c))
            { needsReplace = true; break; }
        }
        if (!needsReplace) return str;

        var sb = new System.Text.StringBuilder(str.Length * 2);
        foreach (char c in str)
        {
            switch (c)
            {
                case '\u2591': // ░ → ⠒ (盲文2点，≈25%灰度)
                    sb.Append('\u2812');
                    break;
                case '\u2592': // ▒ → ⠶ (盲文4点，≈50%灰度)
                    sb.Append('\u2836');
                    break;
                case '\u2593': // ▓ → ⠿ (盲文6点，≈75%灰度)
                    sb.Append('\u287F');
                    break;
                case '\u2588': // █ → ⣿ (盲文8点全满，≈100%灰度)
                    sb.Append('\u28FF');
                    break;
                default:
                    sb.Append(c);
                    // 终端渲染为半角时，在 Ambiguous 字符后补空格使其视觉占2列
                    if (needsAmbiguousPad && IsAmbiguousChar(c))
                        sb.Append(' ');
                    break;
            }
        }
        return sb.ToString();
    }
}
