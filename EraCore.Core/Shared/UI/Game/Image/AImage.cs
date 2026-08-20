using System;

namespace MinorShift.Emuera.UI.Game.Image;

internal abstract class AbstractImage : IDisposable
{
	public const int MAX_IMAGESIZE = 8192;
#if HEADLESS
	public abstract int Width { get; }
	public abstract int Height { get; }
#else
	public abstract System.Drawing.Bitmap Bitmap { get; set; }
	public nint GDIhDC { get; protected set; }
	protected System.Drawing.Graphics g;
#endif

	public abstract bool IsCreated { get; }

	public abstract void Dispose();
}
