using System;

namespace MinorShift.Emuera.GameView;

/// <summary>
/// VT 终端生命周期清理：注册进程级退出钩子（Ctrl+C / UnhandledException / ProcessExit）并在多钩子下幂等执行一次性清理。
/// 从 AgentCliProtocol 拆分以隔离 VT 资源恢复的编排职责（钩子注册 + 幂等标志 + 多钩子保障）；
/// 具体 teardown（dispose screen/input/scrollStatusBar）由调用方注入的 <paramref name="cleanup"/> 委托负责，
/// 退出请求由 <paramref name="requestExit"/> 委托负责（避免本类反向依赖协议类型）。
/// </summary>
internal sealed class VtTerminalLifecycle
{
    private readonly Action _requestExit;
    private readonly Action _cleanup;
    private bool _hooksRegistered;
    private bool _cleanupDone;

    internal VtTerminalLifecycle(Action requestExit, Action cleanup)
    {
        _requestExit = requestExit;
        _cleanup = cleanup;
    }

    /// <summary>注册异常/进程退出钩子，确保终端恢复。多钩子保障，注册幂等。</summary>
    internal void Register()
    {
        if (_hooksRegistered) return;
        _hooksRegistered = true;

        try
        {
            Console.CancelKeyPress += OnCancelKeyPress;
            AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
            AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
        }
        catch (Exception ex) { AgentLog.Instance.Write("vt cleanup hook registration failed: " + ex); }
    }

    /// <summary>幂等执行一次清理。</summary>
    internal void CleanupNow()
    {
        if (_cleanupDone) return;
        _cleanupDone = true;
        _cleanup();
    }

    private void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs e)
    {
        // raw mode 下 Ctrl+C 主要靠 0x03 检测，此处作为安全网
        e.Cancel = true;
        _requestExit();
        CleanupNow();
    }

    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e) => CleanupNow();

    private void OnProcessExit(object? sender, EventArgs e) => CleanupNow();
}