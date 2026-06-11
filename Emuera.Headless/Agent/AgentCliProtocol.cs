using System;
using System.IO;
using System.Text;
using System.Threading;
using MinorShift.Emuera.Runtime;
using MinorShift.Emuera.UI.Game;

namespace MinorShift.Emuera.GameView
{
    internal sealed class AgentCliProtocol : AgentProtocolBase
    {
        private readonly StringBuilder _buf = new();

        public AgentCliProtocol(EmueraConsole console, IConsoleUI ui)
            : base(console, ui) { }

        internal override string? GetInitialTurn() => null;

        internal override string? Step(string input) => null;

        /// <summary>
        /// 同步 CLI 主循环，由 Program.RunHeadless() 调用。
        /// </summary>
        internal void RunCliLoop()
        {
            FlushBuffer();

            if (Console.IsInputRedirected)
                RunPipeCliLoop(Console.In);
            else
                RunConsoleKeyLoop();

            FlushBuffer();
        }

        private void RunConsoleKeyLoop()
        {
            while (!IsStopped)
            {
                var timeoutMs = console.InputTimeoutMs;
                if (timeoutMs.HasValue && timeoutMs.Value <= 0)
                {
                    if (_buf.Length > 0)
                    {
                        WriteOutput("\r" + new string(' ', _buf.Length) + "\r", false);
                        _buf.Clear();
                    }
                    console.SubmitTimeout();
                    FlushBuffer();
                    continue;
                }

                if (Console.KeyAvailable)
                {
                    var key = Console.ReadKey(true);
                    ProcessKey(key);
                }

                FlushBuffer();
                Thread.Sleep(PollIntervalMs);
            }
        }

        private void RunPipeCliLoop(TextReader input)
        {
            while (!IsStopped)
            {
                string? line = input.ReadLine();
                if (line == null)
                    break;

                foreach (char ch in line)
                    ProcessChar(ch);

                ProcessChar('\r');
                FlushBuffer();
            }
        }

        private void FlushBuffer()
        {
            string text = console.TakeAgentBuffer();
            if (text.Length > 0)
                Console.Write(text);
        }

        private void ProcessChar(char ch)
        {
            if (ch == '\r' || ch == '\n')
            {
                WriteOutput("\r" + new string(' ', _buf.Length) + "\r", false);
                string input = _buf.ToString();
                _buf.Clear();
                DispatchInput(input);
            }
            else if (ch == '\b')
            {
                if (_buf.Length > 0)
                {
                    _buf.Remove(_buf.Length - 1, 1);
                    WriteOutput("\b \b", false);
                }
            }
            else if (ch == 27)
            {
                WriteOutput("\r" + new string(' ', _buf.Length) + "\r", false);
                _buf.Clear();
            }
            else if (!char.IsControl(ch))
            {
                _buf.Append(ch);
                WriteOutput(ch.ToString(), false);
            }
        }

        private void ProcessKey(ConsoleKeyInfo key)
        {
            if (key.Key == ConsoleKey.Enter)
                ProcessChar('\r');
            else if (key.Key == ConsoleKey.Backspace)
                ProcessChar('\b');
            else if (key.Key == ConsoleKey.Escape)
                ProcessChar((char)27);
            else if (!char.IsControl(key.KeyChar))
                ProcessChar(key.KeyChar);
        }

        protected override void DispatchInput(string input)
        {
            if (console.State != ConsoleState.WaitInput)
            {
                WriteOutput("[终端] 当前不在等待输入状态，输入已忽略");
                return;
            }

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
                        WriteOutput("[终端] 当前需要整数输入，请重试");
                    break;

                case InputType.PrimitiveMouseKey:
                    WriteOutput("[终端] 当前等待原始鼠标/键盘事件，终端无法模拟，请在窗口中操作");
                    break;

                default:
                    WriteOutput($"[终端] 未处理的输入类型: {req.InputType}");
                    break;
            }
        }
    }
}
