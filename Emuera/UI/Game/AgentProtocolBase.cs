using System;
using System.Threading;
using MinorShift.Emuera.Runtime;
using MinorShift.Emuera.UI.Game;

namespace MinorShift.Emuera.GameView
{
    internal class AgentProtocolBase
    {
        private volatile bool _stopped;
        protected AgentProtocolBase _innerProtocol;
        protected Thread _thread;
        protected readonly EmueraConsole console;
        protected readonly IConsoleUI ui;
        protected const int TurnTimeoutMs = 30000;
        protected const int PollIntervalMs = 50;

        internal AgentProtocolBase(EmueraConsole console, IConsoleUI ui)
        {
            this.console = console;
            this.ui = ui;
        }

        internal bool IsStopped => _stopped;

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
        /// stdin 通过管道重定向时使用 JSONL 协议，有终端时使用 CLI 交互模式。
        /// 双击 WinExe（无 console、无 pipe）时返回 null，由 WinForms 正常处理。
        /// </summary>
        public static AgentProtocolBase DetectAndRun(EmueraConsole console, IConsoleUI ui)
        {
            AgentProtocolBase protocol;
            if (Console.IsInputRedirected)
            {
                // IsInputRedirected=true 有两种情况：
                // 1) stdin 被管道重定向（JSONL agent 模式）→ stdin 是真实管道流，不可 Seek
                // 2) 双击 WinExe（无 console）→ stdin 是 NullStream，可 Seek
                // 用 CanSeek 区分：管道不可 Seek，NullStream 可以
                var stdin = Console.OpenStandardInput();
                if (stdin.CanSeek)
                    return null; // 无 console 且无 pipe，普通 WinForms 模式
                protocol = new AgentJsonlProtocol(console, ui);
            }
            else
            {
                try { _ = Console.KeyAvailable; }
                catch { return null; }
                protocol = new AgentCliProtocol(console, ui);
            }

            protocol.Run();
            return protocol;
        }

    }
}
