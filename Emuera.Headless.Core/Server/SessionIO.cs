using System;
using System.Threading;
using System.Threading.Tasks;

namespace MinorShift.Emuera.Server;

/// <summary>
/// 替换 Console.ReadLine/WriteLine 的抽象，使 AgentJsonlProtocol 不依赖全局 stdin/stdout。
/// </summary>
internal abstract class SessionIO
{
    /// <summary>
    /// 异步读取一行输入。返回 null 表示 EOF/关闭/取消。
    /// 实现应支持 CancellationToken 取消（TINPUT timeout 通过 CancelAfter 实现）。
    /// </summary>
    public abstract Task<string?> ReadLineAsync(CancellationToken ct);
    public abstract void WriteLine(string text);
    public abstract void Close();
    public abstract bool IsConnected { get; }

    /// <summary>
    /// 是否支持交互式提示（如无限循环检测的确认弹窗）。
    /// MAUI 桥接为 true（有玩家可弹窗）；HTTP/管道为 false（自动化场景无人确认）。
    /// </summary>
    public virtual bool SupportsInteractivePrompt => false;

    /// <summary>推送非 turn 消息给交互端（无限循环提示等）。无交互通道时静默 no-op。</summary>
    public virtual void WriteMessage(string json) { }
}
