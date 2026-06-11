using System.Text;

namespace MinorShift.Emuera.GameView;

internal sealed partial class EmueraConsole
{
    internal readonly StringBuilder _agentBuffer = new();

    internal string TakeAgentBuffer()
    {
        var text = _agentBuffer.ToString();
        _agentBuffer.Clear();
        return text;
    }
}
