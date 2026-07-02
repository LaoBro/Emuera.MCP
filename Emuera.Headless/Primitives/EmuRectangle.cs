namespace MinorShift.Emuera.Primitives;

/// <summary>
/// 轻量级矩形类型，替代 System.Drawing.Rectangle。
/// 通过隐式转换兼容现有代码。
/// </summary>
internal readonly record struct EmuRectangle(int X, int Y, int Width, int Height)
{
    public static readonly EmuRectangle Empty = new(0, 0, 0, 0);

    public EmuSize Size => new(Width, Height);

    public bool Contains(EmuPoint point)
        => point.X >= X && point.X < X + Width && point.Y >= Y && point.Y < Y + Height;

    public bool Contains(int x, int y)
        => x >= X && x < X + Width && y >= Y && y < Y + Height;

    public static implicit operator EmuRectangle(System.Drawing.Rectangle r)
        => new(r.X, r.Y, r.Width, r.Height);

    public static implicit operator System.Drawing.Rectangle(EmuRectangle r)
        => new(r.X, r.Y, r.Width, r.Height);
}
