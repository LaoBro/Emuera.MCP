using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json;
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
        private readonly SessionIO _io;

        private int VisibleLineCount => Math.Max(1, ui.ClientHeight / Config.LineHeight);
        private string? _pendingRejectReason;

        public AgentJsonlProtocol(EmueraConsole console, IConsoleUI ui)
            : this(console, ui, ConsoleOutIO.Instance) { }

        public AgentJsonlProtocol(EmueraConsole console, IConsoleUI ui, SessionIO io)
            : base(console, ui)
        {
            _io = io;
        }

        internal override async Task<string?> GetInitialTurnAsync()
        {
            if (!await WaitForInputAsync())
                return null;

            return BuildTurn();
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
                return JsonSerializer.Serialize(new
                {
                    text = "",
                    state = console.State.ToString(),
                    inputType = (string?)null,
                    needValue = false,
                    buttons = Array.Empty<object>(),
                    error = ex.Message
                });
            }
        }

        /// <summary>
        /// TINPUT 超时专用路径，调用 EmueraConsole.SubmitTimeout() 并返回下一 turn。
        /// 仅由 server 模式的 Session 轮询线程调用；JSONL 管道模式不检查 InputTimeoutMs，
        /// 不会自动触发超时，客户端不发 input 时进程永久阻塞等待。
        /// </summary>
        internal override string? SubmitTimeout()
        {
            console.SubmitTimeout();
            return BuildTurn();
        }

        /// <summary>
        /// 生成最终 turn（Quit/Error 状态），由 Session.GameLoop 结束时调用一次，
        /// 写入 SessionIO 供 GET /turn 取走。复用 BuildTurn 逻辑，保持 turn 结构一致。
        /// </summary>
        internal string? BuildFinalTurn() => BuildTurn();

        /// <summary>执行超时处理并将 turn 写入 IO，返回 true。</summary>
        private bool HandleTimeoutAndWrite()
        {
            var turn = SubmitTimeout();
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
                        HandleTimeoutAndWrite();
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
                            HandleTimeoutAndWrite();
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
                catch { continue; }

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

        private string BuildTurn()
        {
            var text = console.TakeAgentBuffer();
            var req = console.CurrentRequest;
            string? error = _pendingRejectReason;
            _pendingRejectReason = null;
            return JsonSerializer.Serialize(new
            {
                text,
                state = console.State.ToString(),
                inputType = req?.InputType.ToString(),
                needValue = req?.NeedValue ?? false,
                buttons = CollectVisibleButtons(),
                error
            });
        }

        /// <summary>
        /// 收集窗口默认可见区域中所有按钮的标签与对应输入值。
        /// 只采集当前轮次有效的按钮（Generation == LastButtonGeneration），
        /// 排除历史轮次残留的过期按钮。
        /// </summary>
        private List<object> CollectVisibleButtons()
        {
            var buttons = new List<object>();
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
                    buttons.Add(new
                    {
                        label = btn.ToString(),
                        value = btn.IsInteger ? (object)btn.Input : (object)btn.Inputs
                    });
                }
            }
            return buttons;
        }

        #endregion
    }
}
