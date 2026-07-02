namespace MinorShift.Emuera.Primitives;

/// <summary>
/// 轻量级矩形类型，替代 System.Drawing.Rectangle。
/// 通过隐式转换兼容现有代码。
/// </summary>
public readonly record struct EmuRectangle(int X, int Y, int Width, int Height)
{
    public static readonly EmuRectangle Empty = new(0, 0, 0, 0);

    public EmuSize Size => new(Width, Height);

    public bool Contains(EmuPoint point)
        => point.X >= X && point.X < X + Width && point.Y >= Y && point.Y < Y + Height;

    public bool Contains(int x, int y)
        => x >= X && x < X + Width && y >= Y && y < Y + Height;

    public bool IntersectsWith(EmuRectangle other)
        => X < other.X + other.Width && other.X < X + Width
        && Y < other.Y + other.Height && other.Y < Y + Height;

    public static implicit operator EmuRectangle(System.Drawing.Rectangle r)
        => new(r.X, r.Y, r.Width, r.Height);

    public static implicit operator System.Drawing.Rectangle(EmuRectangle r)
        => new(r.X, r.Y, r.Width, r.Height);
}
