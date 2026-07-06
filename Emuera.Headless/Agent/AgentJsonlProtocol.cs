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
        private static readonly JsonSerializerOptions TurnJsonOptions = new()
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        private readonly SessionIO _io;

        private int VisibleLineCount => Math.Max(1, ui.ClientHeight / Config.LineHeight);
        private string? _pendingRejectReason;

        private const int CurrentProtocolVersion = 1;

        public AgentJsonlProtocol(EmueraConsole console, IConsoleUI ui, SessionIO io)
            : base(console, ui)
        {
            _io = io;
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
                AgentLog.Instance.Write("step fatal: " + ex);
                var errorTurn = JsonSerializer.Serialize(new TurnRecord(
                    text: "",
                    state: console.State.ToString(),
                    inputType: null,
                    needValue: false,
                    buttons: new List<ButtonEntry>(),
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
        /// </summary>
        internal override async Task<string?> SubmitTimeoutAsync()
        {
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
            var text = console.TakeAgentBuffer();
            var req = console.CurrentRequest;
            string? error = _pendingRejectReason;
            _pendingRejectReason = null;
            return JsonSerializer.Serialize(new TurnRecord(
                text: text,
                state: console.State.ToString(),
                inputType: req?.InputType.ToString(),
                needValue: req?.NeedValue ?? false,
                buttons: CollectVisibleButtons(),
                error: error,
                protocolVersion: isInitial ? CurrentProtocolVersion : null
            ), TurnJsonOptions);
        }

        /// <summary>
        /// 收集窗口默认可见区域中所有按钮的标签与对应输入值。
        /// 只采集当前轮次有效的按钮（Generation == LastButtonGeneration），
        /// 排除历史轮次残留的过期按钮。
        /// </summary>
        private List<ButtonEntry> CollectVisibleButtons()
        {
            var buttons = new List<ButtonEntry>();
            var lines = console.DisplayLineList;
            if (lines == null || lines.Count == 0)
                return buttons;

            long currentGen = console.LastButtonGeneration;
            int start = Math.Max(0, lines.Count - VisibleLineCount);
            for (int i = start; i < lines.Count; i++)
            {
                var line = lines[i];
                if (line?.Buttons == null)
                    continue;
                foreach (var btn in line.Buttons)
                {
                    if (btn == null || !btn.IsButton)
                        continue;
                    // 只采集当前轮次的按钮（Generation == LastButtonGeneration）
                    // Generation=0 的非按钮元素已被 IsButton 过滤；Generation=0 的按钮在旧轮次也是过期的
                    if (btn.Generation != currentGen)
                        continue;
                    buttons.Add(new ButtonEntry(label: btn.ToString(), value: btn.IsInteger ? (object)btn.Input : (object)btn.Inputs));
                }
            }
            return buttons;
        }

        #endregion
    }
}
