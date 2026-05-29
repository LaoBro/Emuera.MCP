using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Threading;
using MinorShift.Emuera.Forms;
using MinorShift.Emuera.GameProc;
using MinorShift.Emuera.Runtime;

namespace MinorShift.Emuera.GameView
{
    /// <summary>
    /// 终端输入输出桥接器：异步等待按键，所有 Console 写操作委托给专用输出线程，避免 UI 线程阻塞
    /// </summary>
    internal sealed class TerminalInputBridge
    {
        private readonly EmueraConsole console;
        private readonly MainWindow window;
        private volatile bool _stopped;
        private Thread _readThread;
        private Thread _writeThread;
        private readonly StringBuilder _buf = new();
        private readonly BlockingCollection<(string text, bool newLine)> _outputQueue = new(8192);
        private IAgentProtocol _agentProtocol;

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
                bridge._readThread = new Thread(bridge.AgentReadLoop)
                {
                    IsBackground = true,
                    Name = "AgentInput"
                };
                bridge._readThread.Start();
                return bridge;
            }

            // 检查终端可用性，无控制台时不启动读写线程
            try
            {
                if (Console.OpenStandardOutput() == Stream.Null)
                    return bridge;
            }
            catch
            {
                return bridge;
            }

            bridge._readThread = new Thread(bridge.ReadLoop)
            {
                IsBackground = true,
                Name = "TerminalInput"
            };
            bridge._writeThread = new Thread(bridge.WriteLoop)
            {
                IsBackground = true,
                Name = "TerminalOutput"
            };
            bridge._readThread.Start();
            bridge._writeThread.Start();
            return bridge;
        }

        public void Stop()
        {
            _stopped = true;
            _outputQueue.CompleteAdding();
            _readThread?.Interrupt();
            _readThread?.Join(TimeSpan.FromSeconds(2));
            _writeThread?.Join(TimeSpan.FromSeconds(2));
        }

        /// <summary>
        /// 将文本写入终端输出队列，由专用输出线程异步写入控制台，不会阻塞 UI 线程
        /// </summary>
        public void WriteOutput(string text, bool newLine = true)
        {
            _outputQueue.TryAdd((text, newLine));
        }

        /// <summary>
        /// Agent 模式：首条消息检测协议，然后委托给 IAgentProtocol
        /// </summary>
        private void AgentReadLoop()
        {
            string firstLine = Console.ReadLine();
            if (firstLine == null) return;

            bool isMcp = firstLine.Contains("\"jsonrpc\"");
            _agentProtocol = isMcp
                ? new AgentMcpProtocol(console, window, () => _stopped)
                : new AgentJsonlProtocol(console, window, () => _stopped);

            _agentProtocol.Run(firstLine);

            // stdin closed or protocol exited — close game window to terminate process
            if (!_stopped)
                window.BeginInvoke(new Action(() => window.Close()));
        }

        private void ReadLoop()
        {
            while (!_stopped)
            {
                ConsoleKeyInfo key;
                try
                {
                    key = Console.ReadKey(true);
                }
                catch (ThreadInterruptedException)
                {
                    break;
                }

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

        /// <summary>
        /// 运行在 UI 线程——处理按键并手动回显(避免游戏再次回显输入)，输入完成后 Dispatch
        /// </summary>
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

        private void DispatchInput(string input)
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
