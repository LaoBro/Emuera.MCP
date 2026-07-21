using System;

namespace MinorShift.Emuera.GameView;

/// <summary>
/// 各字符组在当前终端中的实际渲染宽度配置（全角=true / 半角=false）。
/// 实例化以消除全局可变状态，由调用方持有并在 <see cref="TerminalDisplayWidth.ReplaceForTerminal"/> 时传入。
///
/// 这些值仅影响输出端的 ReplaceForTerminal，绝不影响 IsWideChar。
/// IsWideChar 始终与游戏设计一致（Box/Geometric/Misc=全角，Block=半角）。
/// </summary>
internal readonly record struct TerminalCharWidthConfig(
    bool BoxDrawingIsWide,
    bool GeometricIsWide,
    bool MiscSymbolsIsWide,
    bool BlockElementsIsWide)
{
    /// <summary>默认配置：全部半角（非 CJK 行为）。</summary>
    internal static TerminalCharWidthConfig Default => new(false, false, false, false);
}

internal static class TerminalDisplayWidth
{
    internal static int GetDisplayWidth(string str)
    {
        int width = 0;
        foreach (char c in str)
            width += IsWideChar(c) ? 2 : 1;
        return width;
    }

    /// <summary>
    /// 计算字符串前导空白（对齐缩进）的显示宽度：
    /// 半角空格 ' ' 计 1 列，全角空格 '\u3000' 计 2 列，遇到首个非空白字符停止。
    /// 用于按格式化行计算按钮区域的起始列。
    /// </summary>
    internal static int LeadingDisplayWidth(string s)
    {
        int width = 0;
        foreach (char c in s)
        {
            if (c == ' ') { width += 1; continue; }
            if (c == '\u3000') { width += 2; continue; }
            break;
        }
        return width;
    }

    /// <summary>
    /// 判断字符在游戏设计中是否为全角（占 2 列）。
    /// 此判定始终与游戏（MS Gothic 字体）的设计一致，不受终端实际渲染影响。
    /// 终端渲染差异由 ReplaceForTerminal 在输出端补偿。
    /// </summary>
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

        // Box Drawing (U+2500-257F): ━┃┏┗┛┓┣┫┳┻╋ 等 - 游戏设计为全角
        if (c >= 0x2500 && c <= 0x257F) return true;

        // Geometric Shapes (U+25A0-25FF): ●■◆▲ 等 - 游戏设计为全角
        if (c >= 0x25A0 && c <= 0x25FF) return true;

        // Miscellaneous Symbols (U+2600-26FF): ★☆ 等 - 游戏设计为全角
        if (c >= 0x2600 && c <= 0x26FF) return true;

        // Block Elements (U+2580-259F): ░▒▓█ - 游戏脚本中按半角使用，始终半角
        return false;
    }

    /// <summary>
    /// 根据用户提供的宽度提示生成各字符组的渲染宽度配置。
    /// 用于探测不可用（如 mintty 直连、输出重定向、非 Windows）或用户明确指定的情况。
    /// hint 取值：
    ///   "cjk"    - 4 组全部按全角（对应 MS Gothic 等 CJK 字体终端）
    ///   "latin"  - 4 组全部按半角（对应非 CJK 字体终端，概率最大）
    ///   "auto"   - 返回 Default，由 DetectCharWidths() 探测决定
    /// </summary>
    internal static TerminalCharWidthConfig ApplyWidthHint(string hint)
    {
        switch ((hint ?? "auto").Trim().ToLowerInvariant())
        {
            case "cjk":
                return new TerminalCharWidthConfig(true, true, true, true);
            case "latin":
                return TerminalCharWidthConfig.Default;
            // "auto" 或其它值：返回默认，由调用方决定是否探测
            default:
                return TerminalCharWidthConfig.Default;
        }
    }

    /// <summary>
    /// 探测当前终端中各字符组的实际渲染宽度。
    /// 对每个代表字符写字符后读取光标位移，delta>=2 判为全角，1 判为半角。
    /// 在所有能交互的控制台（conhost、Windows Terminal/conpty）中可靠；
    /// 在输出重定向或探测异常时保持默认值（全 false = 非 CJK）。
    /// </summary>
    internal static TerminalCharWidthConfig DetectCharWidths()
    {
        if (Console.IsOutputRedirected) return TerminalCharWidthConfig.Default;

        // (代表字符, 输出字段引用) —— 用局部变量承接探测结果
        ProbeGroup('\u2501', out bool boxWide);       // ━ Box Drawing
        ProbeGroup('\u25CF', out bool geoWide);       // ● Geometric Shapes
        ProbeGroup('\u2605', out bool miscWide);      // ★ Miscellaneous Symbols
        ProbeGroup('\u2588', out bool blockWide);     // █ Block Elements

        Console.Error.WriteLine(
            $"[terminal] Char widths: BoxDrawing={(boxWide ? "wide" : "half")} " +
            $"Geometric={(geoWide ? "wide" : "half")} " +
            $"MiscSymbols={(miscWide ? "wide" : "half")} " +
            $"BlockElements={(blockWide ? "wide" : "half")}");

        return new TerminalCharWidthConfig(boxWide, geoWide, miscWide, blockWide);
    }

    /// <summary>
    /// 探测单个字符在终端中的渲染宽度。
    /// 写入字符后读取 CursorLeft 位移判断全角/半角。
    /// </summary>
    private static void ProbeGroup(char probe, out bool isWide)
    {
        isWide = false; // 探测失败时默认半角（非 CJK）
        try
        {
            int left = Console.CursorLeft;
            int top = Console.CursorTop;

            // 确保当前行有足够空间（至少 4 列），避免换行干扰位移读数
            if (Console.WindowWidth - left < 4)
            {
                Console.WriteLine();
                left = 0;
                top = Console.CursorTop;
            }

            Console.SetCursorPosition(left, top);
            Console.Write(probe);

            int newLeft = Console.CursorLeft;
            int delta = newLeft - left;

            // 清除测试输出
            Console.SetCursorPosition(left, top);
            Console.Write("  ");
            Console.SetCursorPosition(left, top);

            // delta >= 2 表示全角，1 表示半角
            // delta 为负或 0 说明发生换行，视为探测失败（保持默认半角）
            isWide = delta >= 2;
        }
        catch
        {
            // 探测失败，保持默认（半角）
        }
    }

    /// <summary>
    /// 将终端中无法正确渲染的字符替换/补齐，使终端实际视觉宽度与游戏期望宽度一致。
    ///
    /// 核心不变量：此方法绝不改变字符串的行结构——不插入换行符，不增减行数，
    /// 不因终端宽度不足而重排或截断。一切换行以游戏本身的逻辑为准。
    /// 补空格/替换仅是把"终端视觉宽度"校准回"游戏期望宽度"，使游戏内部的
    /// 换行点在终端里精确呈现。
    ///
    /// 处理策略（宽度计算始终与游戏一致，终端差异在此补偿）：
    ///   Block Elements (░▒▓█)：游戏按半角用。终端渲染为全角时替换为盲文点阵（半角）；
    ///                          终端已为半角时不处理。
    ///   Box Drawing / Geometric / Misc Symbols：游戏按全角用。终端渲染为半角时
    ///                          在字符后补一个半角空格（字符 1 列 + 空格 1 列 = 2 列）；
    ///                          终端已为全角时不处理。
    /// </summary>
    internal static string ReplaceForTerminal(string str, TerminalCharWidthConfig config)
    {
        if (str.Length == 0) return str;

        // 快速路径：判断是否需要任何处理
        bool padBox = !config.BoxDrawingIsWide;
        bool padGeo = !config.GeometricIsWide;
        bool padMisc = !config.MiscSymbolsIsWide;
        bool replaceBlock = config.BlockElementsIsWide;

        if (!padBox && !padGeo && !padMisc && !replaceBlock) return str;

        var sb = new System.Text.StringBuilder(str.Length * 2);
        foreach (char c in str)
        {
            // Block Elements：终端全角时替换为半角盲文点阵
            if (replaceBlock && c >= 0x2580 && c <= 0x259F)
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
                    default: // 其它 Block Elements（▀▄▌▐ 等）暂不替换
                        sb.Append(c);
                        break;
                }
                continue;
            }

            // 其它字符：原样输出，必要时在半角渲染的字符组后补空格
            sb.Append(c);
            if (padBox && c >= 0x2500 && c <= 0x257F)
                sb.Append(' ');
            else if (padGeo && c >= 0x25A0 && c <= 0x25FF)
                sb.Append(' ');
            else if (padMisc && c >= 0x2600 && c <= 0x26FF)
                sb.Append(' ');
        }
        return sb.ToString();
    }
}
