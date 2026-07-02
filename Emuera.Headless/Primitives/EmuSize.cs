namespace MinorShift.Emuera.Primitives;

/// <summary>
/// 轻量级尺寸类型，替代 System.Drawing.Size。
/// 通过隐式转换兼容现有代码。
/// </summary>
public readonly record struct EmuSize(int Width, int Height)
{
    public static readonly EmuSize Empty = new(0, 0);

    public static implicit operator EmuSize(System.Drawing.Size s) => new(s.Width, s.Height);
    public static implicit operator System.Drawing.Size(EmuSize s) => new(s.Width, s.Height);
}
