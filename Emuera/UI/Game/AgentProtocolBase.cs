using System;
using System.Threading;
using MinorShift.Emuera.Forms;
using MinorShift.Emuera.Runtime;

namespace MinorShift.Emuera.GameView
{
    internal class AgentProtocolBase
    {
        private volatile bool _stopped;
        protected AgentProtocolBase _innerProtocol;
        protected Thread _thread;
        protected readonly EmueraConsole console;
        protected readonly MainWindow window;
        protected const int TurnTimeoutMs = 30000;
        protected const int PollIntervalMs = 50;

        internal AgentProtocolBase(EmueraConsole console, MainWindow window)
        {
            this.console = console;
            this.window = window;
        }

        protected bool IsStopped() => _stopped;

        public virtual void Run() { }

        public virtual void WriteOutput(string text, bool newLine = true)
        {
            lock (console._agentBufferLock)
            {
                if (newLine)
                    console._agentBuffer.AppendLine(text);
                else
                    console._agentBuffer.Append(text);
            }
        }
        public virtual void Stop()
        {
            _stopped = true;
            _innerProtocol?.Stop();
            _thread?.Join(TimeSpan.FromSeconds(2));
        }

        protected virtual void DispatchInput(string input)
        {
            if (console.State != ConsoleState.WaitInput)
                return;
            var req = console.CurrentRequest;
            if (req == null) return;

            switch (req.InputType)
            {
                case InputType.EnterKey:
                case InputType.AnyKey:
                case InputType.StrValue:
                case InputType.IntButton:
                case InputType.StrButton:
                    console.PressEnterKey(false, input, false);
                    break;
                case InputType.IntValue:
                case InputType.AnyValue:
                    if (long.TryParse(input, out _))
                        console.PressEnterKey(false, input, false);
                    break;
                case InputType.PrimitiveMouseKey:
                    break;
                default:
                    break;
            }
        }

        /// <summary>
        /// 检测运行环境并创建/运行对应的协议实例。
        /// stdin 重定向时使用 JSONL 协议，否则使用 CLI 交互模式。
        /// </summary>
        public static AgentProtocolBase DetectAndRun(EmueraConsole console, MainWindow window)
        {
            bool isAgentMode;
            try { isAgentMode = Console.IsInputRedirected; }
            catch { isAgentMode = false; }

            AgentProtocolBase protocol;
            if (isAgentMode)
            {
                protocol = new AgentJsonlProtocol(console, window);
            }
            else
            {
                if (!Environment.UserInteractive) return null;
                protocol = new AgentCliProtocol(console, window);
            }

            protocol.Run();
            return protocol;
        }

    }
}
