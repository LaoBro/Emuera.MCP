using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using MinorShift.Emuera.Runtime;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Server;
using MinorShift.Emuera.UI.Game;

namespace MinorShift.Emuera.GameView
{
    internal class AgentJsonlProtocol : AgentProtocolBase
    {
        // Issue 08：internal（原 private）—— BridgeHost.ShowFatalError 序列化 error turn 时复用，
        // 保证与 AgentJsonlProtocol.StepAsync 的 error turn 完全一致（同 converters + ignore condition）。
        // MAUI 项目经 InternalsVisibleTo("Emuera.Maui") 可访问。
        internal static readonly JsonSerializerOptions TurnJsonOptions = new()
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Converters = { new TurnOpConverter(), new LineOpConverter() },
        };

        private readonly SessionIO _io;
        private readonly DisplayState _displayState;

        private int VisibleLineCount => Math.Max(1, ui.ClientHeight / Config.LineHeight);
        private string? _pendingRejectReason;
        /// <summary>
        /// ADR-0016：TINPUT 超时 flag——SubmitTimeoutAsync 路径置 true，
        /// 下次 BuildTurn 写出后清空。复用 _pendingRejectReason 同样的"一次性旗标"模式。
        /// RunLoopAsync 的 OperationCanceledException 分支调 HandleTimeoutAndWriteAsync
        /// → SubmitTimeoutAsync → 此处置 true → BuildTurn 读出写入 turn.timedOut → 清空。
        /// </summary>
        private bool _pendingTimeoutFlag;

        public AgentJsonlProtocol(EmueraConsole console, IConsoleUI ui, SessionIO io, DisplayState displayState)
            : base(console, ui)
        {
            _io = io;
            _displayState = displayState;
        }

        internal override async Task<string?> GetInitialTurnAsync()
        {
            if (!await WaitForInputAsync())
                return null;

            return BuildTurn(isInitial: true);
        }

        internal override async Task<string?> StepAsync(string input)
        {
            if (IsStopped)
                return null;

            if (!await WaitForInputAsync())
                return null;

            if (console.State != ConsoleState.WaitInput)
                return null;

            try
            {
                ui.Invoke(() => DispatchInput(input));

                return BuildTurn();
            }
            catch (Exception ex)
            {
                // I-08：脚本运行期异常一律 fatal。Process 内部状态（指令指针/栈帧/变量表）
                // 已被破坏，恢复无意义。Stop 让 RunLoopAsync 下一轮退出，由 Session
                // 走 finally 路径（BuildFinalTurn + GlobalStatic.Reset）。
                //
                // 注意：ERB THROW / 除零等脚本级异常被 Process.DoScript() 内部捕获并处理
                // （Process.cs:345 catch → handleException），不会传播到此 catch 块。
                // 此 catch 是防御性兜底，仅捕获 Process 未预料的 C# 异常（如 NRE）。
                AgentLog.Instance.Write("step fatal: " + ex);
                var errorTurn = JsonSerializer.Serialize(new TurnRecord(
                    state: console.State.ToString(),
                    inputType: null,
                    needValue: false,
                    diff: null,
                    error: ex.Message
                ), TurnJsonOptions);
                Stop();
                return errorTurn;
            }
        }

        /// <summary>
        /// TINPUT 超时专用路径，调用 EmueraConsole.SubmitTimeout() 后等待游戏进入
        /// WaitInput/Quit/Error 稳定状态再 BuildTurn，避免返回半运行态 turn。
        /// 仅由 server 模式的 Session 轮询线程调用；JSONL 管道模式不检查 InputTimeoutMs，
        /// 不会自动触发超时，客户端不发 input 时进程永久阻塞等待。
        /// I-08：原先同步 BuildTurn 拿快照，绕过 WaitForInputAsync 的 30s 超时与取消保护；
        /// 若 RunEmueraProgram 卡死整个 server 线程被阻塞。
        /// ADR-0016：进入此路径前置 _pendingTimeoutFlag=true，让下一帧 BuildTurn 写出
        /// turn.timedOut=true，前端据此清空启发式检测（issue 04 已删，改消费 timedOut 旗标）。
        /// </summary>
        internal override async Task<string?> SubmitTimeoutAsync()
        {
            _pendingTimeoutFlag = true;
            console.SubmitTimeout();
            if (!await WaitForInputAsync())
                return null;
            return BuildTurn();
        }

        /// <summary>
        /// 生成最终 turn（Quit/Error 状态），由 Session.GameLoop 结束时调用一次，
        /// 写入 SessionIO 供 GET /turn 取走。复用 BuildTurn 逻辑，保持 turn 结构一致。
        /// </summary>
        internal string? BuildFinalTurn() => BuildTurn();

        /// <summary>执行超时处理并将 turn 写入 IO，返回 true。</summary>
        private async Task<bool> HandleTimeoutAndWriteAsync()
        {
            var turn = await SubmitTimeoutAsync();
            if (turn != null)
                _io.WriteLine(turn);
            return true;
        }

        /// <summary>
        /// JSONL 协议主循环（async）：读 input → Step → 写 turn，可选 TINPUT 超时处理。
        /// 由 server 模式的 Session（Task.Run(GameLoopAsync)）与 JSONL 管道模式
        /// （Program.RunHeadless，.GetAwaiter().GetResult()）共享。
        /// enableTimeout=true 启用 TINPUT 超时分支：每轮 linked CTS + CancelAfter，
        /// 超时抛 OperationCanceledException 后调 SubmitTimeout。
        /// enableTimeout=false 纯阻塞读取，TINPUT 不触发（管道 stdin 无可靠 timeout）。
        /// </summary>
        internal async Task RunLoopAsync(bool enableTimeout, CancellationToken externalCt)
        {
            var initialTurn = await GetInitialTurnAsync();
            if (initialTurn != null)
                _io.WriteLine(initialTurn);

            while (!externalCt.IsCancellationRequested && !IsStopped && _io.IsConnected)
            {
                string? line = null;
                if (enableTimeout)
                {
                    long? timeoutMs = console.InputTimeoutMs;

                    if (timeoutMs.HasValue && timeoutMs.Value <= 0)
                    {
                        await HandleTimeoutAndWriteAsync();
                        continue;
                    }

                    if (timeoutMs.HasValue)
                    {
                        // 每轮 linked CTS + CancelAfter，超时取消 ReadLineAsync
                        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(externalCt, StopToken);
                        linkedCts.CancelAfter((int)timeoutMs.Value);
                        try
                        {
                            line = await _io.ReadLineAsync(linkedCts.Token);
                        }
                        catch (OperationCanceledException)
                        {
                            // 外部取消或 Stop：退出循环
                            if (externalCt.IsCancellationRequested || IsStopped)
                                break;
                            // timeout 触发：调 SubmitTimeout 并继续
                            await HandleTimeoutAndWriteAsync();
                            continue;
                        }
                    }
                    else
                    {
                        line = await _io.ReadLineAsync(externalCt);
                    }

                    if (line == null)
                    {
                        // ReadLineAsync 返回 null = EOF/Channel 关闭
                        break;
                    }
                }
                else
                {
                    line = await _io.ReadLineAsync(externalCt);
                    if (line == null)
                        break;
                }

                JsonlCommand? cmd;
                try { cmd = JsonSerializer.Deserialize<JsonlCommand>(line); }
                catch (Exception ex) { AgentLog.Instance.Write("invalid jsonl input ignored: " + ex.Message); continue; }

                if (cmd?.type != "input")
                    continue;

                var stepTurn = await StepAsync(cmd.value ?? "");
                if (stepTurn != null)
                    _io.WriteLine(stepTurn);
                else
                    break;
            }
        }

        protected override void OnInputRejected(string reason)
        {
            _pendingRejectReason = reason;
        }

        #region Turn helpers

        private async Task<bool> WaitForInputAsync()
        {
            var sw = Stopwatch.StartNew();
            var token = StopToken;
            while (!token.IsCancellationRequested)
            {
                var state = console.State;
                if (state == ConsoleState.WaitInput || state == ConsoleState.Quit || state == ConsoleState.Error)
                    return true;
                if (sw.ElapsedMilliseconds > TurnTimeoutMs)
                    return false;
                try { await Task.Delay(PollIntervalMs, token); }
                catch (OperationCanceledException) { return false; }
            }
            return false;
        }

        private string BuildTurn(bool isInitial = false)
        {
            // plan C（v5）：ComputeDiff 原子化刷新 _current 并 drain 分类权威清空信号，
            // 不再前置 TryUpdate（避免跨调用窗口 + 清空信号错配，见 DisplayState.ComputeDiff）。
            // ComputeDiff 与 _previous 比对产出 DisplayDiff；二者同处 _gate 锁，
            // 但 BuildTurn 是单线程（游戏循环）调用，无重入风险。
            var diff = _displayState.ComputeDiff();
            var req = console.CurrentRequest;
            string? error = _pendingRejectReason;
            _pendingRejectReason = null;

            // ADR-0016：读出超时 flag 并清空（一次性旗标），与 _pendingRejectReason 同模式
            bool timedOut = _pendingTimeoutFlag;
            _pendingTimeoutFlag = false;

            // ADR-0016：TINPUT timer 字段——仅 TINPUT 期间（Timelimit > 0）填充。
            // displayTime 取 InputRequest.DisplayTime（ERB 可设 false 让前端不显示倒计时）。
            // timeUpMessage 仅在非空时携带。
            long? timeLimit = req is { Timelimit: > 0 } ? req.Timelimit : null;
            bool? displayTime = req is { Timelimit: > 0, DisplayTime: true } ? true : null;
            string? timeUpMessage = req is { Timelimit: > 0 } reqWithMes
                && !string.IsNullOrEmpty(reqWithMes.TimeUpMes)
                ? reqWithMes.TimeUpMes
                : null;

            return JsonSerializer.Serialize(new TurnRecord(
                state: console.State.ToString(),
                inputType: req?.InputType.ToString(),
                needValue: req?.NeedValue ?? false,
                diff: diff,
                error: error,
                protocolVersion: isInitial ? TurnRecord.CurrentProtocolVersion : null,
                timeLimit: timeLimit,
                displayTime: displayTime,
                timeUpMessage: timeUpMessage,
                timedOut: timedOut,
                generation: console.LastButtonGeneration
            ), TurnJsonOptions);
        }

        #endregion
    }
}
