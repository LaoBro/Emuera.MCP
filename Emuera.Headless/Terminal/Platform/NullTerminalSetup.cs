namespace MinorShift.Emuera.Terminal.Platform;

internal sealed class NullTerminalSetup : ITerminalSetup
{
    public bool IsAnsiEnabled => false;
    public bool TryEnableAnsi() => false;
    public bool TrySetConsoleSize(int cols, int rows) => true;
    public string? DetectFont() => null;
    public bool TryProbeDa1() => false;
}
