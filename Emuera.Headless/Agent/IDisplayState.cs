namespace MinorShift.Emuera.GameView;

internal interface IDisplayState
{
    bool TryUpdate();
    DisplaySnapshot Current { get; }
}
