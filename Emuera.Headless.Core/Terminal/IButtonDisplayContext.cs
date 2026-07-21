using MinorShift.Emuera.UI.Game;

namespace MinorShift.Emuera.GameView;

internal interface IButtonDisplayContext
{
    ConsoleButtonString? SelectingButton { get; }
    TerminalCharWidthConfig CharWidthConfig { get; }
}
