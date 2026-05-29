using System;
using System.Diagnostics;
using System.Text.Json;
using System.Threading;
using MinorShift.Emuera.Forms;
using MinorShift.Emuera.Runtime;

namespace MinorShift.Emuera.GameView
{
    internal sealed class AgentJsonlProtocol : AgentProtocolBase
    {
        public AgentJsonlProtocol(EmueraConsole console, MainWindow window, Func<bool> isStopped)
            : base(console, window, isStopped) { }

        public override void Run(string firstLine)
        {
            if (console.State == ConsoleState.WaitInput)
            {
                Console.WriteLine(BuildTurn());
                console.TakeAgentBuffer();
            }

            HandleMessage(firstLine);
            ReadStdinLoop(HandleMessage);
        }

        private void HandleMessage(string line)
        {
            JsonlCommand cmd;
            try { cmd = JsonSerializer.Deserialize<JsonlCommand>(line); }
            catch { return; }

            if (cmd?.type == "input")
            {
                string value = cmd.value ?? "";

                var sw = Stopwatch.StartNew();
                while (!isStopped() && console.State != ConsoleState.WaitInput)
                {
                    if (sw.ElapsedMilliseconds > TurnTimeoutMs)
                        return;
                    Thread.Sleep(PollIntervalMs);
                }

                if (isStopped() || console.State != ConsoleState.WaitInput)
                    return;

                console.TakeAgentBuffer();

                window.Invoke(new Action(() => DispatchInput(value)));

                Console.WriteLine(BuildTurn());
            }
        }

        private record JsonlCommand(string type, string value);
    }
}
