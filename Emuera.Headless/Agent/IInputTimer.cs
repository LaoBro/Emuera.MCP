namespace MinorShift.Emuera.GameView;

internal interface IInputTimer
{
    long InputTimelimit { get; }
    bool IsWaitingInput { get; }
    string? TimeUpMessage { get; }
    void SubmitTimeout();
    void ClearInputBuffer();
}
