using MinorShift.Emuera.Forms;

namespace MinorShift.Emuera.GameView
{
    internal sealed class TerminalInputBridge
    {
        private volatile bool _stopped;
        private readonly AgentProtocolBase _agentProtocol;

        private TerminalInputBridge(EmueraConsole console, MainWindow window)
        {
            _agentProtocol = new AgentDetectingProtocol(console, window, () => _stopped);
        }

        public static TerminalInputBridge Start(EmueraConsole console, MainWindow window)
        {
            var bridge = new TerminalInputBridge(console, window);
            bridge._agentProtocol.Run(null);
            return bridge;
        }

        public void Stop()
        {
            _stopped = true;
            _agentProtocol.Stop();
        }

        public void WriteOutput(string text, bool newLine = true)
        {
            _agentProtocol.WriteOutput(text, newLine);
        }
    }
}
