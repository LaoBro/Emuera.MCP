using System.Text;

namespace MinorShift.Emuera.GameView;

internal sealed partial class EmueraConsole
{
    internal readonly StringBuilder _agentBuffer = new();
    internal readonly object _agentBufferLock = new();

    internal string TakeAgentBuffer()
    {
        lock (_agentBufferLock)
        {
            var text = _agentBuffer.ToString();
            _agentBuffer.Clear();
            return text;
        }
    }
}
