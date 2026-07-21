using MinorShift.Emuera.GameView;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Terminal.Platform;
using MinorShift.Emuera.UI;
using MinorShift.Emuera.UI.Game;
using System;
using System.Threading.Tasks;

namespace MinorShift.Emuera;

internal enum GameLoopResult
{
    Completed,
    Aborted,
}

internal static class GameLoopComposer
{
    internal static async Task<GameLoopResult> RunAsync(
        ConfigData configData,
        ITerminalSetup terminalSetup,
        Func<EmueraConsole, IConsoleUI, ITerminalSetup, AgentProtocolBase?> buildProtocol,
        Func<AgentProtocolBase, Task> runLoop)
    {
        using (var scope = GlobalStatic.OpenScope(configData))
        {
            var ui = new HeadlessConsole();
            var console = new EmueraConsole(ui, terminalSetup);

            var protocol = buildProtocol(console, ui, terminalSetup);
            if (protocol == null)
                return GameLoopResult.Aborted;

            console.SetAgentBridge(protocol);

            Program.LoadFonts();
            await console.Initialize();
            await runLoop(protocol);
        }

        return GameLoopResult.Completed;
    }
}
