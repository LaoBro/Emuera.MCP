using System;
using MinorShift.Emuera.Forms;
using MinorShift.Emuera.GameProc;
using MinorShift.Emuera.Runtime;

namespace MinorShift.Emuera.GameView
{
    /// <summary>
    /// 终端输入桥接器：后台线程读取终端输入，通过 BeginInvoke 回到 UI 线程
    /// </summary>
    internal sealed class TerminalInputBridge
    {
        private readonly EmueraConsole console;
        private readonly MainWindow window;

        private TerminalInputBridge(EmueraConsole console, MainWindow window)
        {
            this.console = console;
            this.window = window;
        }

        public static TerminalInputBridge Start(EmueraConsole console, MainWindow window)
        {
            var bridge = new TerminalInputBridge(console, window);
            var thread = new System.Threading.Thread(bridge.InputLoop)
            {
                IsBackground = true,
                Name = "TerminalInput"
            };
            thread.Start();
            return bridge;
        }

        private void InputLoop()
        {
            while (true)
            {
                string input = ReadLineSilent();
                if (input == null)
                    continue;

                window.BeginInvoke(new Action(() => DispatchInput(input)));
            }
        }

        /// <summary>
        /// 无回显地从终端读取一行输入，仅靠 Emuera 的显示输出来回显
        /// </summary>
        private static string ReadLineSilent()
        {
            var buf = new System.Text.StringBuilder();
            while (true)
            {
                var key = Console.ReadKey(true);

                if (key.Key == ConsoleKey.Enter)
                {
                    // 用空格覆盖整行，擦掉手动回显的输入字符
                    Console.Write("\r" + new string(' ', buf.Length) + "\r");
                    return buf.ToString();
                }
                else if (key.Key == ConsoleKey.Backspace)
                {
                    if (buf.Length > 0)
                    {
                        buf.Remove(buf.Length - 1, 1);
                        Console.Write("\b \b"); // 退格+空格+退格，擦掉终端上的字符
                    }
                }
                else if (key.Key == ConsoleKey.Escape)
                {
                    buf.Clear();
                    Console.Write("\r" + new string(' ', Console.BufferWidth) + "\r");
                }
                else if (!char.IsControl(key.KeyChar))
                {
                    buf.Append(key.KeyChar);
                    Console.Write(key.KeyChar); // 手动回显：输入时可见
                }
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
            if (req == null)
                return;

            switch (req.InputType)
            {
                case InputType.EnterKey:
                case InputType.AnyKey:
                    console.PressEnterKey(false, input, false);
                    break;

                case InputType.IntValue:
                    if (long.TryParse(input, out _))
                        console.PressEnterKey(false, input, false);
                    else
                        Console.WriteLine("[终端] 当前需要整数输入，请重试");
                    break;

                case InputType.StrValue:
                    console.PressEnterKey(false, input, false);
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