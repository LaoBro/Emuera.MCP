using System;

namespace MinorShift.Emuera.Server;

/// <summary>
/// 替换 Console.ReadLine/WriteLine 的抽象，使 AgentJsonlProtocol 不依赖全局 stdin/stdout。
/// </summary>
internal abstract class SessionIO
{
    public abstract string? ReadLine();
    public abstract void WriteLine(string text);
    public abstract void Close();
    public abstract bool IsConnected { get; }
}
