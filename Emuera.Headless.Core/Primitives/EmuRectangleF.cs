namespace MinorShift.Emuera.Primitives;

/// <summary>
/// 轻量级浮点矩形类型，替代 System.Drawing.RectangleF。
/// 通过隐式转换兼容现有代码。
/// </summary>
public readonly record struct EmuRectangleF(float X, float Y, float Width, float Height)
{
    public static readonly EmuRectangleF Empty = new(0, 0, 0, 0);

    public float Left => X;
    public float Top => Y;
    public float Right => X + Width;
    public float Bottom => Y + Height;

    public bool Contains(float x, float y)
        => x >= X && x < X + Width && y >= Y && y < Y + Height;

#if !HEADLESS
    public static implicit operator EmuRectangleF(System.Drawing.RectangleF r)
        => new(r.X, r.Y, r.Width, r.Height);

    public static implicit operator System.Drawing.RectangleF(EmuRectangleF r)
        => new(r.X, r.Y, r.Width, r.Height);
#endif
}
