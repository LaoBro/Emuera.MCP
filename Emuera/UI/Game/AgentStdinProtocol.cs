using System;
using System.Diagnostics;
using System.Text.Json;
using System.Threading;
using MinorShift.Emuera.Forms;

namespace MinorShift.Emuera.GameView
{
    internal abstract class AgentStdinProtocol : AgentProtocolBase
    {
        protected AgentStdinProtocol(EmueraConsole console, MainWindow window)
            : base(console, window) { }

        protected virtual void OnStart() { }

        protected abstract void HandleMessage(string line);

        public override void Run(string firstLine)
        {
            _thread = new Thread(() =>
            {
                OnStart();
                HandleMessage(firstLine);
                ReadStdinLoop(HandleMessage);
            })
            {
                IsBackground = true,
                Name = "TerminalAgent"
            };
            _thread.Start();
        }

        protected bool WaitForInput()
        {
            var sw = Stopwatch.StartNew();
            while (!IsStopped())
            {
                var state = console.State;
                if (state == ConsoleState.WaitInput || state == ConsoleState.Quit || state == ConsoleState.Error)
                    return true;
                if (sw.ElapsedMilliseconds > TurnTimeoutMs)
                    return false;
                Thread.Sleep(PollIntervalMs);
            }
            return false;
        }

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

        private void ReadStdinLoop(Action<string> onLine)
        {
            while (!IsStopped())
            {
                string line;
                try { line = Console.ReadLine(); }
                catch (ThreadInterruptedException) { break; }
                if (line == null) break;
                onLine(line);
            }
            if (!IsStopped())
                window.BeginInvoke(new Action(() => window.Close()));
        }
    }
}