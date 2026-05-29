using System.Text;

namespace MinorShift.Emuera.GameView;

internal sealed partial class EmueraConsole
{
    private readonly StringBuilder _agentBuffer = new();
    internal readonly object _agentBufferLock = new();

    internal string ReadAgentBuffer()
    {
        lock (_agentBufferLock)
        {
            return _agentBuffer.ToString();
        }
    }

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
