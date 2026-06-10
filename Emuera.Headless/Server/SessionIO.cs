using System;

namespace MinorShift.Emuera.Server;

/// <summary>
/// 替换 Console.ReadLine/WriteLine 的抽象，使 AgentJsonlProtocol 不依赖全局 stdin/stdout。
/// </summary>
internal abstract class SessionIO
{
    public abstract string? ReadLine();
    /// <summary>
    /// 带超时的读取行。
    /// < 0：无限等待，等价于 ReadLine()。
    /// = 0：尽力非阻塞；实现无法非阻塞时可阻塞。
    /// > 0：等待指定毫秒；具体支持范围取决于实现。
    /// </summary>
    public abstract string? ReadLine(int timeoutMs);
    public abstract void WriteLine(string text);
    public abstract void Close();
    public abstract bool IsConnected { get; }
}
