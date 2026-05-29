using System;
using System.Diagnostics;
using System.Text.Json;
using System.Threading;
using MinorShift.Emuera.Forms;
using MinorShift.Emuera.Runtime;

namespace MinorShift.Emuera.GameView
{
    internal sealed class AgentJsonlProtocol : IAgentProtocol
    {
        private readonly EmueraConsole console;
        private readonly MainWindow window;
        private readonly Func<bool> isStopped;
        private const int TurnTimeoutMs = 30000;
        private const int PollIntervalMs = 50;

        public AgentJsonlProtocol(EmueraConsole console, MainWindow window, Func<bool> isStopped)
        {
            this.console = console;
            this.window = window;
            this.isStopped = isStopped;
        }

        public void Run(string firstLine)
        {
            // Send the pending turn (accumulated before agent connected)
            if (console.State == ConsoleState.WaitInput)
            {
                Console.WriteLine(BuildTurn());
                console.TakeAgentBuffer();
            }

            HandleMessage(firstLine);

            while (!isStopped())
            {
                string line;
                try { line = Console.ReadLine(); }
                catch (ThreadInterruptedException) { break; }
                if (line == null) break;
                HandleMessage(line);
            }
        }

        private string BuildTurn()
        {
            var text = console.ReadAgentBuffer();
            var req = console.CurrentRequest;
            return JsonSerializer.Serialize(new
            {
                text,
                state = console.State.ToString(),
                inputType = req?.InputType.ToString(),
                needValue = req?.NeedValue ?? false
            });
        }

        private void HandleMessage(string line)
        {
            JsonlCommand cmd;
            try { cmd = JsonSerializer.Deserialize<JsonlCommand>(line); }
            catch { return; }

            if (cmd?.type == "input")
            {
                string value = cmd.value ?? "";

                // Wait for game to reach WaitInput before submitting
                var sw = Stopwatch.StartNew();
                while (!isStopped() && console.State != ConsoleState.WaitInput)
                {
                    if (sw.ElapsedMilliseconds > TurnTimeoutMs)
                        return;
                    Thread.Sleep(PollIntervalMs);
                }

                if (isStopped() || console.State != ConsoleState.WaitInput)
                    return;

                console.TakeAgentBuffer(); // discard previous turn output

                // Synchronous Invoke — blocks agent thread until UI processes input
                window.Invoke(new Action(() => DispatchInput(value)));

                Console.WriteLine(BuildTurn());
            }
        }

        private void DispatchInput(string input)
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

        private record JsonlCommand(string type, string value);
    }
}
