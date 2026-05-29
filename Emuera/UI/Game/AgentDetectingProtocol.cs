using System;
using MinorShift.Emuera.Forms;

namespace MinorShift.Emuera.GameView
{
    internal sealed class AgentDetectingProtocol : AgentProtocolBase
    {
        public AgentDetectingProtocol(EmueraConsole console, MainWindow window)
            : base(console, window)
        {
        }

        public override void WriteOutput(string text, bool newLine = true)
        {
            _innerProtocol?.WriteOutput(text, newLine);
        }

        public override void Run(string firstLine)
        {
            bool isAgentMode;
            try { isAgentMode = Console.IsInputRedirected; }
            catch { isAgentMode = false; }

            string line;
            if (isAgentMode)
            {
                line = firstLine ?? Console.ReadLine();
                if (line == null) return;

                _innerProtocol = line.Contains("\"jsonrpc\"")
                    ? new AgentMcpProtocol(console, window)
                    : new AgentJsonlProtocol(console, window);
            }
            else
            {
                if (!Environment.UserInteractive) return;
                line = null;
                _innerProtocol = new AgentCliProtocol(console, window);
            }

            _innerProtocol.Run(line);
        }
    }
}
