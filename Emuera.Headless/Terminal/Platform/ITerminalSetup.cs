using System;

namespace MinorShift.Emuera.Terminal.Platform;

internal interface ITerminalSetup
{
    bool TryEnableAnsi();
    bool IsAnsiEnabled { get; }
    bool TrySetConsoleSize(int cols, int rows);
    string? DetectFont();
    bool TryProbeDa1();
}
