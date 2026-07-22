using System;
using System.Text.Json;
using Emuera.Maui.JsBridge;
using MinorShift.Emuera;
using MinorShift.Emuera.GameView;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Server;
using MinorShift.Emuera.Terminal.Platform;

namespace Emuera.Maui;

/// <summary>
/// MAUI 桥接编排器——issue 07 / spec ID8。
/// <para>
/// 持有 <see cref="MauiBridgeIO"/> + <see cref="IJsBridge"/>，在游戏循环线程与 UI 线程之间转发 turn / input。
/// 构造时创建 <see cref="MauiBridgeIO"/>（<c>_onTurn</c> 回调内 <c>Dispatcher.Dispatch</c> 切 UI 线程投递给 WebView），
/// 订阅 <see cref="IJsBridge.InputReceived"/> 接收 Vue 端 postMessage。
/// </para>
/// <para>
/// <b>issue 07 范围</b>：仅识别 Vue <c>{"type":"ready"}</c> 信号并写日志。
/// 游戏循环启动、input 消息入队、致命错误处理、Dispose fire-and-forget 均由 issue 08 完成。
/// </para>
/// <remarks>
/// 生命周期：在 <c>MainPage</c> 构造时 new（不启动游戏循环），在 <c>MainPage.OnDisappearing</c> 时 <see cref="Dispose"/>。
/// <see cref="ConfigData"/> / <see cref="ITerminalSetup"/> 单例由 <c>MauiProgram</c> DI 注入，<c>MainPage</c> 重建时不重新初始化运行时。
/// </remarks>
internal sealed class BridgeHost : IDisposable
{
    private readonly IDispatcher _dispatcher;
    private readonly ConfigData _configData;
    private readonly ITerminalSetup _terminalSetup;
    private readonly IJsBridge _jsBridge;
    private readonly MauiBridgeIO _bridgeIO;
    private bool _disposed;
    private bool _readyReceived;

    /// <summary>
    /// 构造桥接宿主——不启动游戏循环（spec ID7 延迟启动）。
    /// <para>
    /// <b>Spec 偏离说明</b>：spec ID8 伪代码签名含 <c>webView</c> 参数，实现省略——
    /// <c>IJsBridge.Attach(webView)</c> 由 <see cref="MainPage"/> 在 <c>HandlerChanged</c> UI 事件内直接调
    /// （WebView 平台原生视图就绪的最早可靠时机，UI 线程约束），<see cref="BridgeHost"/> 仅编排 turn/input 流转，
    /// 不持 <c>webView</c> 引用避免跨线程访问风险。
    /// </para>
    /// <para>
    /// <b>可访问性</b>：ctor 标 <c>internal</c>——参数类型 <see cref="ConfigData"/> / <see cref="ITerminalSetup"/>
    /// 在 <c>Emuera.Headless.Core</c> 内为 <c>internal</c>，C# 规则要求方法可访问性不得高于参数类型
    /// （与 <see cref="MainPage"/> ctor 一致）。
    /// </para>
    /// </summary>
    /// <param name="dispatcher">MAUI <see cref="IDispatcher"/>——用于把游戏循环线程的 turn 回调切到 UI 线程调 <see cref="IJsBridge.PostTurn"/>。</param>
    /// <param name="configData">已加载的 <see cref="ConfigData"/> 单例（<c>MauiProgram</c> 初始化时加载）。</param>
    /// <param name="terminalSetup">已启用 ANSI 的 <see cref="ITerminalSetup"/> 单例。</param>
    /// <param name="jsBridge">平台 <see cref="IJsBridge"/> 实现（Windows / Android）。</param>
    internal BridgeHost(IDispatcher dispatcher, ConfigData configData, ITerminalSetup terminalSetup, IJsBridge jsBridge)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _configData = configData ?? throw new ArgumentNullException(nameof(configData));
        _terminalSetup = terminalSetup ?? throw new ArgumentNullException(nameof(terminalSetup));
        _jsBridge = jsBridge ?? throw new ArgumentNullException(nameof(jsBridge));

        // MauiBridgeIO 的 _onTurn 回调在游戏循环线程执行——Dispatcher.Dispatch 切 UI 线程投递给 WebView。
        // issue 08 接入游戏循环后此回调被触发；issue 07 仅注册不触发。
        _bridgeIO = new MauiBridgeIO(OnTurnFromGame);
        _jsBridge.InputReceived += OnInputFromJs;
    }

    /// <summary>
    /// 游戏循环线程的 turn 回调——<see cref="MauiBridgeIO.WriteLine"/> 调用。
    /// <para>
    /// <see cref="IJsBridge.PostTurn"/> 内部调 <c>EvaluateJavaScriptAsync</c> / <c>EvaluateJavaScript</c>，
    /// 必须在 UI 线程执行（CoreWebView2 / Android.Webkit.WebView 要求）。
    /// <see cref="IDispatcher.Dispatch"/> 是 fire-and-forget——不阻塞游戏循环线程。
    /// </para>
    /// </summary>
    private void OnTurnFromGame(string turnJson)
    {
        _dispatcher.Dispatch(() => _jsBridge.PostTurn(turnJson));
    }

    /// <summary>
    /// JS → C# 消息处理——<see cref="IJsBridge.InputReceived"/> 触发。
    /// <para>
    /// <b>issue 07 范围</b>：仅识别 <c>{"type":"ready"}</c> 消息并写日志。
    /// 其他消息（input / anyEvent）由 issue 08 接入游戏循环后处理（<c>_bridgeIO.EnqueueInput</c>）。
    /// </para>
    /// </summary>
    private void OnInputFromJs(string message)
    {
        try
        {
            using var doc = JsonDocument.Parse(message);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("type", out var typeEl)
                && typeEl.ValueKind == JsonValueKind.String
                && typeEl.GetString() == "ready")
            {
                if (!_readyReceived)
                {
                    _readyReceived = true;
                    // 双通道日志：AgentLog 持久化文件（issue 07 验收项"C# 日志确认收到 ready 信号"），
                    // Debug.WriteLine 即时输出 Visual Studio 调试器 Output 窗口便于开发期调试。
                    AgentLog.Instance.Write("[bridge] Vue ready signal received");
                    System.Diagnostics.Debug.WriteLine("[bridge] Vue ready signal received");
                    // issue 08：此处调 Start() 启动 GameLoopComposer.RunAsync（Task.Run）
                }
                return;
            }
        }
        catch (JsonException ex)
        {
            AgentLog.Instance.Write($"[bridge] non-JSON input received (ignored in T07): {ex.Message}");
        }

        // issue 07：非 ready 消息暂不处理——issue 08 接入 _bridgeIO.EnqueueInput 后由游戏循环消费
        AgentLog.Instance.Write($"[bridge] input received (deferred to T08): {message}");
    }

    /// <summary>
    /// 释放桥接资源——issue 07 仅关闭 IO + 取消事件订阅。
    /// <para>
    /// issue 08 将扩展为 fire-and-forget 取消 CTS + 等待游戏循环 Task（spec ID9）。
    /// </para>
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _jsBridge.InputReceived -= OnInputFromJs;
        _bridgeIO.Close();
    }
}
