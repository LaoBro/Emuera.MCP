using System.Collections.Generic;

namespace MinorShift.Emuera.Primitives;

/// <summary>
/// 轻量级颜色类型，替代 System.Drawing.Color。
/// 通过隐式转换兼容现有代码。
/// </summary>
internal readonly record struct EmuColor(byte R, byte G, byte B, byte A = 255)
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

    // 颜色名称映射
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
    };

    // 隐式转换
    public static implicit operator EmuColor(System.Drawing.Color c)
        => new(c.R, c.G, c.B, c.A);

    public static implicit operator System.Drawing.Color(EmuColor c)
        => System.Drawing.Color.FromArgb(c.A, c.R, c.G, c.B);
}
