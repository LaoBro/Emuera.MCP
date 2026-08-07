using System;
using MinorShift.Emuera.GameView;

static class Dialog
{
    public enum Result
    {
        Yes,
        No
    }

    public static void Show(string text)
    {
        EmueraLog.Warn("dialog", text);
    }

    public static void Show(string title, string text)
    {
        EmueraLog.Warn("dialog", $"{title}: {text}");
    }

    public static bool ShowPrompt(string title, string text)
    {
        EmueraLog.Warn("dialog", $"{title}: {text} (auto-select: No)");
        return false;
    }
}
