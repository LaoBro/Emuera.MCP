namespace MinorShift.Emuera.Primitives;

/// <summary>
/// 轻量级二维点类型，替代 System.Drawing.Point。
/// 通过隐式转换兼容现有代码。
/// </summary>
public readonly record struct EmuPoint(int X, int Y)
{
    public static readonly EmuPoint Empty = new(0, 0);

    public static implicit operator EmuPoint(System.Drawing.Point p) => new(p.X, p.Y);
    public static implicit operator System.Drawing.Point(EmuPoint p) => new(p.X, p.Y);
}
