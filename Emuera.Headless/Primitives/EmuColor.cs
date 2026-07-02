using System.Collections.Generic;

namespace MinorShift.Emuera.Primitives;

/// <summary>
/// 轻量级颜色类型，替代 System.Drawing.Color。
/// 通过隐式转换兼容现有代码。
/// </summary>
public readonly record struct EmuColor(byte R, byte G, byte B, byte A = 255)
{
    // 静态常量
    public static readonly EmuColor Empty = new(0, 0, 0, 0);
    public static readonly EmuColor Transparent = new(0, 0, 0, 0);
    public static readonly EmuColor Black = new(0, 0, 0);
    public static readonly EmuColor White = new(255, 255, 255);
    public static readonly EmuColor Red = new(255, 0, 0);
    public static readonly EmuColor Green = new(0, 128, 0);
    public static readonly EmuColor Blue = new(0, 0, 255);
    public static readonly EmuColor Yellow = new(255, 255, 0);
    public static readonly EmuColor Gray = new(128, 128, 128);
    public static readonly EmuColor DarkBlue = new(0, 0, 139);

    // 静态工厂方法
    public static EmuColor FromArgb(int r, int g, int b)
        => new((byte)r, (byte)g, (byte)b);

    public static EmuColor FromArgb(int a, int r, int g, int b)
        => new((byte)r, (byte)g, (byte)b, (byte)a);

    public static EmuColor FromArgb(int argb)
        => new((byte)((argb >> 16) & 0xFF),
               (byte)((argb >> 8) & 0xFF),
               (byte)(argb & 0xFF),
               (byte)((argb >> 24) & 0xFF));

    public int ToArgb()
        => (A << 24) | (R << 16) | (G << 8) | B;

    public static EmuColor FromName(string name)
        => ColorNameMap.GetValueOrDefault(name.ToLowerInvariant(), Black);

    /// <summary>
    /// 尝试按名称解析颜色，匹配 System.Drawing.Color.FromName 的失败语义。
    /// 未知名称返回 false 并将 color 置为 Empty（A=0）。
    /// </summary>
    public static bool TryFromName(string name, out EmuColor color)
    {
        if (ColorNameMap.TryGetValue(name.ToLowerInvariant(), out var c))
        {
            color = c;
            return true;
        }
        color = Empty;
        return false;
    }

    // 颜色名称映射（含常见 CSS/HTML 颜色名）
    private static readonly Dictionary<string, EmuColor> ColorNameMap = new()
    {
        ["black"] = Black,
        ["white"] = White,
        ["red"] = Red,
        ["green"] = Green,
        ["blue"] = Blue,
        ["yellow"] = Yellow,
        ["gray"] = Gray,
        ["grey"] = Gray,
        ["transparent"] = Transparent,
        ["darkblue"] = DarkBlue,
        // 常见 CSS/HTML 颜色名补充
        ["silver"] = new(192, 192, 192),
        ["maroon"] = new(128, 0, 0),
        ["olive"] = new(128, 128, 0),
        ["lime"] = new(0, 255, 0),
        ["aqua"] = new(0, 255, 255),
        ["teal"] = new(0, 128, 128),
        ["navy"] = new(0, 0, 128),
        ["fuchsia"] = new(255, 0, 255),
        ["purple"] = new(128, 0, 128),
        ["orange"] = new(255, 165, 0),
        ["pink"] = new(255, 192, 203),
        ["brown"] = new(165, 42, 42),
        ["cyan"] = new(0, 255, 255),
        ["magenta"] = new(255, 0, 255),
    };

    // 隐式转换
    public static implicit operator EmuColor(System.Drawing.Color c)
        => new(c.R, c.G, c.B, c.A);

    public static implicit operator System.Drawing.Color(EmuColor c)
        => System.Drawing.Color.FromArgb(c.A, c.R, c.G, c.B);
}
