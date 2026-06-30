using System;

namespace MinorShift.Emuera;

/// <summary>
/// 脚本 QUIT/EXIT 触发的正常退出信号，用于中断游戏循环并走 finally 清理（I-11）。
/// 替代 HeadlessConsole 旧的 Environment.Exit(0)，避免绕过 Dispose/Reset 链。
/// </summary>
internal sealed class GameExitException : Exception
{
    public GameExitException() : base("script requested exit (QUIT/EXIT)") { }
}
