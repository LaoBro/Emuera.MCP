using System;
using System.Drawing;
using MinorShift.Emuera.GameView;
using MinorShift.Emuera.Runtime.Config;

namespace MinorShift.Emuera.UI.Game;

/// <summary>
/// 无头模式下的文本宽度估算。不依赖 Graphics.MeasureString，用字符数 × 估算宽度。
/// </summary>
internal sealed class StringMeasure : IDisposable
{
    private readonly float _charWidth;

    public StringMeasure()
    {
        _charWidth = Config.FontSize / 2f;
    }

    public int GetDisplayLength(ReadOnlySpan<char> chars, Font f)
    {
        return GetDisplayLength(chars.ToString(), f);
    }

    public int GetDisplayLength(string s, Font font)
    {
        if (string.IsNullOrEmpty(s))
            return 0;
        int width = 0;
        foreach (char c in s)
        {
            width += TerminalDisplayWidth.IsWideChar(c) ? 2 : 1;
        }
        return (int)(width * _charWidth);
    }

    public void Dispose() { }
}
