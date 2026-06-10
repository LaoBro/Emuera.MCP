using System;
using System.Threading;
using MinorShift.Emuera.Runtime;
using MinorShift.Emuera.UI.Game;

namespace MinorShift.Emuera.GameView
{
    internal abstract class AgentProtocolBase
    {
        private volatile bool _stopped;
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

        /// <summary>
        /// 获取初始 turn JSON。等待游戏进入 WaitInput/Quit/Error 状态后返回 turn，超时或停止返回 null。
        /// </summary>
        internal abstract string? GetInitialTurn();

        /// <summary>
        /// 提交输入并推进游戏，返回下一 turn JSON。停止或异常返回 null。
        /// </summary>
        internal abstract string? Step(string input);

        /// <summary>
        /// TINPUT 超时专用路径，调用 EmueraConsole 的 timer-timeout 等价逻辑。
        /// 默认抛出 NotSupportedException，由需要 TINPUT 的子类覆盖。
        /// </summary>
        internal virtual string? SubmitTimeout()
        {
            throw new NotSupportedException("TINPUT timeout is not supported by this protocol.");
        }

        public virtual void WriteOutput(string text, bool newLine = true)
        {
            if (newLine)
                console._agentBuffer.AppendLine(text);
            else
                console._agentBuffer.Append(text);
        }

        public virtual void Stop() => _stopped = true;

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
        /// 根据运行环境自动检测并创建对应的协议实例。
        /// 此方法为可选辅助方法，仅用于 --protocol auto 模式。
        /// 推荐由入口点（Program.cs）根据命令行参数显式创建协议，
        /// 并通过 EmueraConsole.SetAgentBridge() 注入。
        /// </summary>
        public static AgentProtocolBase? Detect(EmueraConsole console, IConsoleUI ui)
        {
            AgentProtocolBase protocol;
            if (Console.IsInputRedirected)
            {
                var stdin = Console.OpenStandardInput();
                if (stdin.CanSeek)
                    return null;
                protocol = new AgentJsonlProtocol(console, ui);
            }
            else
            {
                try { _ = Console.KeyAvailable; }
                catch { return null; }
                protocol = new AgentCliProtocol(console, ui);
            }

            return protocol;
        }
    }
}
