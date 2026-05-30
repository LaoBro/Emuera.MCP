using System;
using System.Text.Json;
using MinorShift.Emuera.Forms;
using MinorShift.Emuera.Runtime;

namespace MinorShift.Emuera.GameView
{
    internal class AgentJsonlProtocol : AgentStdinProtocol
    {
        public AgentJsonlProtocol(EmueraConsole console, MainWindow window)
            : base(console, window) { }

        protected override void OnStart()
        {
            if (console.State == ConsoleState.WaitInput)
            {
                Console.WriteLine(BuildTurn());
                console.TakeAgentBuffer();
            }
        }

        protected override void HandleMessage(string line)
        {
            JsonlCommand cmd;
            try { cmd = JsonSerializer.Deserialize<JsonlCommand>(line); }
            catch { return; }

            if (cmd?.type == "input")
            {
                string turn = SubmitAndGetTurn(cmd.value ?? "");
                if (turn != null)
                    Console.WriteLine(turn);
            }
        }

        private record JsonlCommand(string type, string value);
    }
}
