using System;

static class Dialog
{
    public enum Result
    {
        Yes,
        No
    }

    public static void Show(string text)
    {
        Console.Error.WriteLine($"[dialog] {text}");
    }

    public static void Show(string title, string text)
    {
        Console.Error.WriteLine($"[dialog:{title}] {text}");
    }

    public static bool ShowPrompt(string title, string text)
    {
        Console.Error.WriteLine($"[dialog:{title}] {text} (auto-select: No)");
        return false;
    }
}
