using System;
using System.IO;
using System.Threading;
using MinorShift.Emuera.Forms;

namespace MinorShift.Emuera.GameView
{
    /// <summary>
    /// Bridges console I/O to the active protocol (CLI, JSONL, or MCP).
    /// In agent mode, detects the protocol from the first stdin line and delegates.
    /// In CLI mode, delegates to AgentCliProtocol for key-by-key interaction.
    /// </summary>
    internal sealed class TerminalInputBridge
    {
        private readonly EmueraConsole console;
        private readonly MainWindow window;
        private volatile bool _stopped;
        private Thread _agentReadThread;
        private AgentProtocolBase _agentProtocol;

        private TerminalInputBridge(EmueraConsole console, MainWindow window)
        {
            this.console = console;
            this.window = window;
        }

        public static TerminalInputBridge Start(EmueraConsole console, MainWindow window, bool agentMode)
        {
            var bridge = new TerminalInputBridge(console, window);

            if (agentMode)
            {
                bridge._agentReadThread = new Thread(bridge.AgentReadLoop)
                {
                    IsBackground = true,
                    Name = "AgentInput"
                };
                bridge._agentReadThread.Start();
                return bridge;
            }

            try
            {
                if (Console.OpenStandardOutput() == Stream.Null)
                    return bridge;
            }
            catch
            {
                return bridge;
            }

            var cliProtocol = new AgentCliProtocol(console, window, () => bridge._stopped);
            bridge._agentProtocol = cliProtocol;
            cliProtocol.Run(null);
            return bridge;
        }

        public void Stop()
        {
            _stopped = true;
            _agentProtocol?.Stop();
            _agentReadThread?.Interrupt();
            _agentReadThread?.Join(TimeSpan.FromSeconds(2));
        }

        public void WriteOutput(string text, bool newLine = true)
        {
            _agentProtocol?.WriteOutput(text, newLine);
        }

        private void AgentReadLoop()
        {
            string firstLine = Console.ReadLine();
            if (firstLine == null) return;

            bool isMcp = firstLine.Contains("\"jsonrpc\"");
            _agentProtocol = isMcp
                ? new AgentMcpProtocol(console, window, () => _stopped)
                : new AgentJsonlProtocol(console, window, () => _stopped);

            _agentProtocol.Run(firstLine);

            if (!_stopped)
                window.BeginInvoke(new Action(() => window.Close()));
        }
    }
}
