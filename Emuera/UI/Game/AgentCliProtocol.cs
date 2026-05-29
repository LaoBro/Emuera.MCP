using System;
using System.Collections.Concurrent;
using System.Text;
using System.Threading;
using MinorShift.Emuera.Forms;
using MinorShift.Emuera.Runtime;

namespace MinorShift.Emuera.GameView
{
    internal sealed class AgentCliProtocol : AgentProtocolBase
    {
        private readonly BlockingCollection<(string text, bool newLine)> _outputQueue = new(8192);
        private readonly StringBuilder _buf = new();
        private Thread _readThread;
        private Thread _writeThread;

        public AgentCliProtocol(EmueraConsole console, MainWindow window, Func<bool> isStopped)
            : base(console, window, isStopped) { }

        public override void Run(string firstLine)
        {
            _readThread = new Thread(ReadLoop)
            {
                IsBackground = true,
                Name = "TerminalInput"
            };
            _writeThread = new Thread(WriteLoop)
            {
                IsBackground = true,
                Name = "TerminalOutput"
            };
            _readThread.Start();
            _writeThread.Start();
        }

        public override void WriteOutput(string text, bool newLine = true)
        {
            _outputQueue.TryAdd((text, newLine));
        }

        public override void Stop()
        {
            _outputQueue.CompleteAdding();
            _readThread?.Interrupt();
            _readThread?.Join(TimeSpan.FromSeconds(2));
            _writeThread?.Join(TimeSpan.FromSeconds(2));
        }

        private void ReadLoop()
        {
            while (!isStopped())
            {
                ConsoleKeyInfo key;
                try { key = Console.ReadKey(true); }
                catch (ThreadInterruptedException) { break; }
                window.BeginInvoke(new Action(() => ProcessKey(key)));
            }
        }

        private void WriteLoop()
        {
            foreach (var (text, newLine) in _outputQueue.GetConsumingEnumerable())
            {
                if (newLine)
                    Console.WriteLine(text);
                else
                    Console.Write(text);
            }
        }

        private void ProcessKey(ConsoleKeyInfo key)
        {
            if (key.Key == ConsoleKey.Enter)
            {
                WriteOutput("\r" + new string(' ', _buf.Length) + "\r", false);
                string input = _buf.ToString();
                _buf.Clear();
                DispatchInput(input);
            }
            else if (key.Key == ConsoleKey.Backspace)
            {
                if (_buf.Length > 0)
                {
                    _buf.Remove(_buf.Length - 1, 1);
                    WriteOutput("\b \b", false);
                }
            }
            else if (key.Key == ConsoleKey.Escape)
            {
                WriteOutput("\r" + new string(' ', _buf.Length) + "\r", false);
                _buf.Clear();
            }
            else if (!char.IsControl(key.KeyChar))
            {
                _buf.Append(key.KeyChar);
                WriteOutput(key.KeyChar.ToString(), false);
            }
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
