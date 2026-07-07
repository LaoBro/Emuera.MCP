using System;

namespace MinorShift.Emuera;

/// <summary>
/// CLI 协议层遇到不可恢复的环境问题（如 VT 初始化失败、stdin 被重定向）时抛出。
/// 由 <see cref="HeadlessRunner.RunAsync"/> 捕获，输出 stderr 提示 + 写 AgentLog，
/// 然后非零退出。与 <see cref="GameExitException"/>（脚本请求正常退出）不同，
/// 此异常表示用户环境/配置错误，必须以非零退出码终止进程。
/// </summary>
internal sealed class HeadlessFatalException : Exception
{
    public HeadlessFatalException(string message) : base(message) { }
    public HeadlessFatalException(string message, Exception innerException)
        : base(message, innerException) { }
}
