using System;
using System.Threading;
using MinorShift.Emuera.Forms;

namespace MinorShift.Emuera.GameView
{
    internal sealed class AgentDetectingProtocol : AgentProtocolBase
    {
        private AgentProtocolBase _innerProtocol;
        private Thread _agentThread;

        public AgentDetectingProtocol(EmueraConsole console, MainWindow window, Func<bool> isStopped)
            : base(console, window, isStopped)
        {
        }

        public override void Run(string firstLine)
        {
            bool isAgentMode;
            try { isAgentMode = Console.IsInputRedirected; }
            catch { isAgentMode = false; }

            if (!isAgentMode)
            {
                _innerProtocol = new AgentCliProtocol(console, window, isStopped);
                _innerProtocol.Run(null);
                return;
            }

            _agentThread = new Thread(() =>
            {
                string line = Console.ReadLine();
                if (line == null) return;

                _innerProtocol = line.Contains("\"jsonrpc\"")
                    ? new AgentMcpProtocol(console, window, isStopped)
                    : new AgentJsonlProtocol(console, window, isStopped);

                _innerProtocol.Run(line);

                if (!isStopped())
                    window.BeginInvoke(new Action(() => window.Close()));
            })
            {
                IsBackground = true,
                Name = "AgentInput"
            };
            _agentThread.Start();
        }

        public override void WriteOutput(string text, bool newLine = true)
        {
            if (_innerProtocol != null)
                _innerProtocol.WriteOutput(text, newLine);
            else
                base.WriteOutput(text, newLine);
        }

        public override void Stop()
        {
            _innerProtocol?.Stop();
            _agentThread?.Join(TimeSpan.FromSeconds(2));
        }
    }
}
