#if HEADLESS
using MinorShift.Emuera.UI.Game;

namespace MinorShift.Emuera.Runtime.Script.Statements;

internal partial class ClipboardProcessor
{
    internal enum CBTriggers
    {
        LeftClick,
        MiddleClick,
        DoubleLeftClick,
        AnyKeyWait,
        InputWait,
    }

    public void Check(CBTriggers trigger) { }
    public void AddLine(ConsoleDisplayLine inputLine, bool left) { }
    public void DelLine(int count) { }
    public void ClearScreen() { }
    public static string StripHTML(string input) => input;
}
#endif
