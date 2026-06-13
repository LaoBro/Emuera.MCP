namespace MinorShift.Emuera.GameView;

internal static class TerminalDisplayWidth
{
    internal static int GetDisplayWidth(string str)
    {
        int width = 0;
        foreach (char c in str)
            width += IsWideChar(c) ? 2 : 1;
        return width;
    }

    internal static bool IsWideChar(char c)
    {
        return (c >= 0x2E80 && c <= 0x9FFF)
            || (c >= 0xAC00 && c <= 0xD7AF)
            || (c >= 0xF900 && c <= 0xFAFF)
            || (c >= 0xFF01 && c <= 0xFF60)
            || (c >= 0xFFE0 && c <= 0xFFE6);
    }
}
