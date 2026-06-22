using System;
using System.Threading;
using MinorShift.Emuera.Runtime;
using MinorShift.Emuera.UI.Game;

namespace MinorShift.Emuera.GameView
{
    internal abstract class AgentProtocolBase
    {
        private readonly CancellationTokenSource _cts = new();
        protected readonly EmueraConsole console;
        protected readonly IConsoleUI ui;
        protected const int TurnTimeoutMs = 30000;
        protected const int PollIntervalMs = 50;

        internal AgentProtocolBase(EmueraConsole console, IConsoleUI ui)
        {
            this.console = console;
            this.ui = ui;
        }

        internal bool IsStopped => _cts.IsCancellationRequested;
        internal CancellationToken StopToken => _cts.Token;

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
        /// 仅由 server 模式的 Session 轮询线程调用；CLI 模式在自身轮询中直接检查
        /// InputTimeoutMs 并调用 console.SubmitTimeout()，不走此路径；
        /// JSONL 管道模式不检查超时，永不调用此方法。
        /// </summary>
        internal virtual string? SubmitTimeout()
        {
            throw new NotSupportedException("TINPUT timeout is not supported by this protocol.");
        }

        internal virtual void WriteOutput(string text, bool newLine = true)
        {
            if (newLine)
                console._agentBuffer.AppendLine(text);
            else
                console._agentBuffer.Append(text);
        }

        internal virtual void Stop() => _cts.Cancel();

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
                    else
                        OnInputRejected("当前需要整数输入，请重试");
                    break;
                case InputType.PrimitiveMouseKey:
                    OnInputRejected("当前等待原始鼠标/键盘事件，终端无法模拟，请在窗口中操作");
                    break;
                default:
                    OnInputRejected($"未处理的输入类型: {req.InputType}");
                    break;
            }
        }

        protected virtual void OnInputRejected(string reason) { }
    }
}
