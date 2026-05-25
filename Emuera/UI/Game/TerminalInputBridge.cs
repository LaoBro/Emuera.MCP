using System;
using System.Threading;
using MinorShift.Emuera.Forms;
using MinorShift.Emuera.GameProc;
using MinorShift.Emuera.Runtime;

namespace MinorShift.Emuera.GameView
{
    /// <summary>
    /// 终端输入桥接器：后台线程仅轮询输入可用性，所有 Console 读写委托给 UI 线程，避免竞争
    /// </summary>
    internal sealed class TerminalInputBridge
    {
        private readonly EmueraConsole console;
        private readonly MainWindow window;
        private volatile bool _running;
        private readonly System.Text.StringBuilder _buf = new();

        private TerminalInputBridge(EmueraConsole console, MainWindow window)
        {
            this.console = console;
            this.window = window;
        }

        public static TerminalInputBridge Start(EmueraConsole console, MainWindow window)
        {
            var bridge = new TerminalInputBridge(console, window);
            bridge._running = true;
            var thread = new Thread(bridge.InputLoop)
            {
                IsBackground = true,
                Name = "TerminalInput"
            };
            thread.Start();
            return bridge;
        }

        public void Stop() => _running = false;

        private void InputLoop()
        {
            while (_running)
            {
                try
                {
                    if (Console.KeyAvailable)
                        window.BeginInvoke(new Action(ProcessKey));
                }
                catch (System.IO.IOException)
                {
                    _running = false;
                    return;
                }
                Thread.Sleep(10);
            }
        }

        /// <summary>
        /// 运行在 UI 线程——读取一个按键并手动回显，输入完成后 Dispatch
        /// </summary>
        private void ProcessKey()
        {
            if (!Console.KeyAvailable) return;

            var key = Console.ReadKey(true);

            if (key.Key == ConsoleKey.Enter)
            {
                Console.Write("\r" + new string(' ', _buf.Length) + "\r");
                string input = _buf.ToString();
                _buf.Clear();
                DispatchInput(input);
            }
            else if (key.Key == ConsoleKey.Backspace)
            {
                if (_buf.Length > 0)
                {
                    _buf.Remove(_buf.Length - 1, 1);
                    Console.Write("\b \b");
                }
            }
            else if (key.Key == ConsoleKey.Escape)
            {
                Console.Write("\r" + new string(' ', _buf.Length) + "\r");
                _buf.Clear();
            }
            else if (!char.IsControl(key.KeyChar))
            {
                _buf.Append(key.KeyChar);
                Console.Write(key.KeyChar);
            }
        }

        private void DispatchInput(string input)
        {
            if (console.State != ConsoleState.WaitInput)
            {
                Console.WriteLine("[终端] 当前不在等待输入状态，输入已忽略");
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
                        Console.WriteLine("[终端] 当前需要整数输入，请重试");
                    break;

                case InputType.PrimitiveMouseKey:
                    Console.WriteLine("[终端] 当前等待原始鼠标/键盘事件，终端无法模拟，请在窗口中操作");
                    break;

                default:
                    Console.WriteLine($"[终端] 未处理的输入类型: {req.InputType}");
                    break;
            }
        }
    }
}