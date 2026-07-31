using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MinorShift.Emuera;
using MinorShift.Emuera.GameView;
using MinorShift.Emuera.Runtime;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Server;
using MinorShift.Emuera.Terminal.Platform;
using MinorShift.Emuera.UI.Game;
using Xunit;

namespace Emuera.Headless.Tests;

/// <summary>
/// ADR-0016 落地单测：验证 AgentJsonlProtocol.BuildTurn 填充 v6 timer 字段。
/// - TINPUT 期间 turn JSON 含 timeLimit / displayTime / timeUpMessage / timedOut=false
/// - 非 TINPUT 期间三 nullable 字段 WhenWritingNull 不出现，timedOut=false 仍出现
/// - SubmitTimeoutAsync 路径置 _pendingTimeoutFlag=true → turn.timedOut=true
///
/// 不走真实游戏脚本——直接操纵 _console._state 的 State/inputReq 字段，
/// 让 SubmitTimeout 在条件不满足时直接 return（避免触发 EndTimerCore 的 RunEmueraProgram）。
/// BuildTurn 经 GetInitialTurnAsync 或 SubmitTimeoutAsync 间接调用。
/// </summary>
public class AgentJsonlProtocolTests : IDisposable
{
    private readonly IDisposable _scope;
    private readonly EmueraConsole _console;
    private readonly DisplayState _displayState;
    private readonly AgentJsonlProtocol _protocol;
    private readonly FakeSessionIO _io;

    public AgentJsonlProtocolTests()
    {
        _scope = GlobalStatic.OpenScope(new ConfigData());
        var ui = new HeadlessConsole();
        _console = new EmueraConsole(ui, new NullTerminalSetup());
        _displayState = new DisplayState(_console, "MS Gothic");
        _io = new FakeSessionIO();
        _protocol = new AgentJsonlProtocol(_console, ui, _io, _displayState);
    }

    public void Dispose() => _scope.Dispose();

    /// <summary>设置 _console._state 的 State 与 inputReq，绕过 Process 脚本路径。</summary>
    private void SetWaitInput(InputRequest? req)
    {
        _console._state.State = ConsoleState.WaitInput;
        _console._state.inputReq = req;
    }

    private static InputRequest TinputReq(long timelimit, bool displayTime, string? timeUpMes = null) =>
        new()
        {
            InputType = InputType.EnterKey,
            Timelimit = timelimit,
            DisplayTime = displayTime,
            TimeUpMes = timeUpMes ?? "",
        };

    // ---------- T_buildturn_tinput_populates_timer_fields ----------

    [Fact]
    public async Task T_buildturn_tinput_populates_timer_fields()
    {
        SetWaitInput(TinputReq(timelimit: 5000, displayTime: true, timeUpMes: "时间到"));

        var json = await _protocol.GetInitialTurnAsync();

        Assert.NotNull(json);
        using var doc = JsonDocument.Parse(json!);
        var root = doc.RootElement;

        Assert.Equal("WaitInput", root.GetProperty("state").GetString());
        Assert.Equal("EnterKey", root.GetProperty("inputType").GetString());
        Assert.Equal(7, root.GetProperty("protocolVersion").GetInt32());

        Assert.Equal(5000L, root.GetProperty("timeLimit").GetInt64());
        Assert.True(root.GetProperty("displayTime").GetBoolean());
        Assert.Equal("时间到", root.GetProperty("timeUpMessage").GetString());
        Assert.False(root.GetProperty("timedOut").GetBoolean());
    }

    // ---------- T_buildturn_non_tinput_omits_nullable_timer_fields ----------

    [Fact]
    public async Task T_buildturn_non_tinput_omits_nullable_timer_fields()
    {
        // inputReq=null：非 TINPUT 期间，三 nullable 字段不应出现在 JSON 中
        SetWaitInput(req: null);

        var json = await _protocol.GetInitialTurnAsync();

        Assert.NotNull(json);
        using var doc = JsonDocument.Parse(json!);
        var root = doc.RootElement;

        Assert.False(root.TryGetProperty("timeLimit", out _),
            "非 TINPUT 期间 timeLimit 不应出现（WhenWritingNull）");
        Assert.False(root.TryGetProperty("displayTime", out _),
            "非 TINPUT 期间 displayTime 不应出现（WhenWritingNull）");
        Assert.False(root.TryGetProperty("timeUpMessage", out _),
            "非 TINPUT 期间 timeUpMessage 不应出现（WhenWritingNull）");

        // timedOut 为非 nullable bool，默认 false，永远出现
        Assert.True(root.TryGetProperty("timedOut", out var timedOutEl));
        Assert.False(timedOutEl.GetBoolean());
    }

    // ---------- T_buildturn_tinput_without_displaytime_omits_displaytime ----------

    [Fact]
    public async Task T_buildturn_tinput_without_displaytime_omits_displaytime_field()
    {
        // Timelimit>0 但 DisplayTime=false：ERB 脚本要求前端不显示倒计时
        // timeLimit 应出现，displayTime 不出现（WhenWritingNull，false→null）
        // TimeUpMes 空字符串 → timeUpMessage 不出现
        SetWaitInput(TinputReq(timelimit: 3000, displayTime: false, timeUpMes: ""));

        var json = await _protocol.GetInitialTurnAsync();

        Assert.NotNull(json);
        using var doc = JsonDocument.Parse(json!);
        var root = doc.RootElement;

        Assert.Equal(3000L, root.GetProperty("timeLimit").GetInt64());
        Assert.False(root.TryGetProperty("displayTime", out _),
            "DisplayTime=false 时 displayTime 字段应为 null（WhenWritingNull 不出现）");
        Assert.False(root.TryGetProperty("timeUpMessage", out _),
            "TimeUpMes 空字符串时 timeUpMessage 字段应不出现");
        Assert.False(root.GetProperty("timedOut").GetBoolean());
    }

    // ---------- T_buildturn_negative_timelimit_omits_timer_fields ----------

    [Fact]
    public async Task T_buildturn_negative_timelimit_omits_timer_fields()
    {
        // Timelimit=-1（InputRequest 默认值）：等同于"无 TINPUT"
        SetWaitInput(TinputReq(timelimit: -1, displayTime: true, timeUpMes: "x"));

        var json = await _protocol.GetInitialTurnAsync();

        Assert.NotNull(json);
        using var doc = JsonDocument.Parse(json!);
        var root = doc.RootElement;

        Assert.False(root.TryGetProperty("timeLimit", out _),
            "Timelimit=-1 时 timeLimit 不应出现");
        Assert.False(root.TryGetProperty("displayTime", out _),
            "Timelimit=-1 时 displayTime 不应出现");
        Assert.False(root.TryGetProperty("timeUpMessage", out _),
            "Timelimit=-1 时 timeUpMessage 不应出现");
    }

    // ---------- T_submit_timeout_sets_timed_out_true ----------

    [Fact]
    public async Task T_submit_timeout_sets_timed_out_true()
    {
        // SubmitTimeoutAsync 路径置 _pendingTimeoutFlag=true，下一帧 BuildTurn 写出 timedOut=true。
        // 测试绕开 EndTimerCore（会 RunEmueraProgram 卡住）：
        //   - State=Quit → WaitForInputAsync 立即返回 true
        //   - inputReq=null → SubmitTimeout 内部条件 guard 直接 return（不调 EndTimerCore）
        // 验证目标：_pendingTimeoutFlag → turn.timedOut 的传递，不是 EndTimerCore 行为
        _console._state.State = ConsoleState.Quit;
        _console._state.inputReq = null;

        var json = await _protocol.SubmitTimeoutAsync();

        Assert.NotNull(json);
        using var doc = JsonDocument.Parse(json!);
        var root = doc.RootElement;

        Assert.True(root.GetProperty("timedOut").GetBoolean(),
            "SubmitTimeoutAsync 路径必须置 timedOut=true");

        // flag 一次性：再调 GetInitialTurnAsync 不应残留 timedOut=true（已清空）
        _console._state.State = ConsoleState.WaitInput;
        var json2 = await _protocol.GetInitialTurnAsync();
        Assert.NotNull(json2);
        using var doc2 = JsonDocument.Parse(json2!);
        Assert.False(doc2.RootElement.GetProperty("timedOut").GetBoolean(),
            "_pendingTimeoutFlag 必须一次性——读出后清空，下一帧 timedOut=false");
    }

    // ---------- T_buildturn_initial_includes_protocol_version ----------

    [Fact]
    public async Task T_buildturn_initial_includes_protocol_version()
    {
        SetWaitInput(req: null);

        var json = await _protocol.GetInitialTurnAsync();

        Assert.NotNull(json);
        using var doc = JsonDocument.Parse(json!);
        Assert.Equal(7, doc.RootElement.GetProperty("protocolVersion").GetInt32());
    }

    [Fact]
    public async Task T_buildturn_non_initial_omits_protocol_version()
    {
        // StepAsync 调用走 BuildTurn(isInitial=false)，protocolVersion 应为 null（WhenWritingNull 不出现）
        // StepAsync 走 DispatchInput + WaitForInputAsync + BuildTurn——
        // 为绕开 DispatchInput 的 InputType 处理，先调 GetInitialTurnAsync 拿首帧，
        // 再调 StepAsync("")（EnterKey 允许空输入）
        SetWaitInput(new InputRequest
        {
            InputType = InputType.EnterKey,
            Timelimit = -1,
        });

        var initial = await _protocol.GetInitialTurnAsync();
        Assert.NotNull(initial);

        // State 仍是 WaitInput（DispatchInput 对 EnterKey + "" 不改 state）
        var stepJson = await _protocol.StepAsync("");
        Assert.NotNull(stepJson);
        using var doc = JsonDocument.Parse(stepJson!);
        Assert.False(doc.RootElement.TryGetProperty("protocolVersion", out _),
            "非初始帧 protocolVersion 应为 null（WhenWritingNull 不出现）");
    }

    // ---------- AskInfiniteLoopDecision（无限循环检测确认）----------

    [Fact]
    public void AskInfiniteLoopDecision_nonInteractive_returns_exit()
    {
        // FakeSessionIO.SupportsInteractivePrompt = false（基类默认）——非交互会话结束，延续旧 HEADLESS 行为
        var result = _protocol.AskInfiniteLoopDecision("script too long");
        Assert.True(result, "non-interactive session must exit on infinite loop (preserve old behavior)");
    }

    [Fact]
    public void AskInfiniteLoopDecision_interactive_continue_response_resumes()
    {
        var interactive = new InteractiveSessionIO();
        var protocol = new AgentJsonlProtocol(_console, new HeadlessConsole(), interactive, _displayState);
        interactive.QueueResponse("continue");

        var result = protocol.AskInfiniteLoopDecision("script too long");

        Assert.False(result, "continue response must resume the script");
        Assert.NotNull(interactive.PushedMessage);
        Assert.Contains("infiniteLoopPrompt", interactive.PushedMessage);
    }

    [Fact]
    public void AskInfiniteLoopDecision_interactive_exit_response_exits()
    {
        var interactive = new InteractiveSessionIO();
        var protocol = new AgentJsonlProtocol(_console, new HeadlessConsole(), interactive, _displayState);
        interactive.QueueResponse("exit");

        var result = protocol.AskInfiniteLoopDecision("script too long");

        Assert.True(result, "exit response must end the game");
    }

    /// <summary>
    /// 交互式测试 SessionIO：SupportsInteractivePrompt=true，WriteMessage 记录推送，
    /// ReadLineAsync 返回预置的 infiniteLoopResponse。
    /// </summary>
    private sealed class InteractiveSessionIO : SessionIO
    {
        private readonly Queue<string?> _responses = new();
        public string? PushedMessage { get; private set; }

        public override bool SupportsInteractivePrompt => true;
        public void QueueResponse(string action)
            => _responses.Enqueue($"{{\"type\":\"infiniteLoopResponse\",\"action\":\"{action}\"}}");

        public override Task<string?> ReadLineAsync(CancellationToken ct)
            => Task.FromResult(_responses.Count > 0 ? _responses.Dequeue() : null);
        public override void WriteLine(string text) { }
        public override void WriteMessage(string json) => PushedMessage = json;
        public override void Close() { }
        public override bool IsConnected => true;
    }

    /// <summary>
    /// 测试用 SessionIO：空实现，IsConnected 永远 true，
    /// 不实际读/写——AgentJsonlProtocolTests 不依赖 IO 层交互。
    /// </summary>
    private sealed class FakeSessionIO : SessionIO
    {
        public override Task<string?> ReadLineAsync(CancellationToken ct)
            => Task.FromResult<string?>(null);
        public override void WriteLine(string text) { }
        public override void Close() { }
        public override bool IsConnected => true;
    }

    private sealed class NullTerminalSetup : ITerminalSetup
    {
        public bool IsAnsiEnabled => false;
        public bool TryEnableAnsi() => false;
        public bool TrySetConsoleSize(int cols, int rows) => false;
        public string? DetectFont() => null;
        public bool TryPrepareVtInput() => false;
    }
}
