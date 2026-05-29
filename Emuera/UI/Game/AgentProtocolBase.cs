using System;
using System.Diagnostics;
using System.Text.Json;
using System.Threading;
using MinorShift.Emuera.Forms;
using MinorShift.Emuera.Runtime;

namespace MinorShift.Emuera.GameView
{
    internal abstract class AgentProtocolBase
    {
        protected readonly EmueraConsole console;
        protected readonly MainWindow window;
        protected readonly Func<bool> isStopped;
        protected const int TurnTimeoutMs = 30000;
        protected const int PollIntervalMs = 50;

        protected AgentProtocolBase(EmueraConsole console, MainWindow window, Func<bool> isStopped)
        {
            this.console = console;
            this.window = window;
            this.isStopped = isStopped;
        }

        public abstract void Run(string firstLine);

        public virtual void WriteOutput(string text, bool newLine = true) { }
        public virtual void Stop() { }

        protected string BuildTurn()
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

        protected bool WaitForTurn(out string turnJson)
        {
            var sw = Stopwatch.StartNew();
            while (!isStopped())
            {
                var state = console.State;
                if (state == ConsoleState.WaitInput || state == ConsoleState.Quit || state == ConsoleState.Error)
                {
                    turnJson = BuildTurn();
                    return true;
                }
                if (sw.ElapsedMilliseconds > TurnTimeoutMs)
                    break;
                Thread.Sleep(PollIntervalMs);
            }
            turnJson = null;
            return false;
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

        protected void ReadStdinLoop(Action<string> onLine)
        {
            while (!isStopped())
            {
                string line;
                try { line = Console.ReadLine(); }
                catch (ThreadInterruptedException) { break; }
                if (line == null) break;
                onLine(line);
            }
        }
    }
}
