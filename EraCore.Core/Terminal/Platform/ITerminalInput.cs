using System;

namespace MinorShift.Emuera.Terminal.Platform;

internal interface ITerminalInput : IDisposable
{
    bool HasInputAvailable();
    int ReadByte();
    void EnableSgrMouse();
    void DisableSgrMouse();
}
