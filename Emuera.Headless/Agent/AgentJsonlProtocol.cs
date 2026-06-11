using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json;
using System.Threading;
using MinorShift.Emuera.Runtime;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Server;
using MinorShift.Emuera.UI.Game;

namespace MinorShift.Emuera.GameView
{
    internal class AgentJsonlProtocol : AgentProtocolBase
    {
        /// <summary>默认窗口高度内可显示的行数。用于采集可见按钮。</summary>
        private readonly int _visibleLineCount;
        private readonly SessionIO _io;

        public AgentJsonlProtocol(EmueraConsole console, IConsoleUI ui)
            : this(console, ui, ConsoleOutIO.Instance) { }

        public AgentJsonlProtocol(EmueraConsole console, IConsoleUI ui, SessionIO io)
            : base(console, ui)
        {
            _io = io;
            int clientHeight = ui.ClientHeight;
            _visibleLineCount = Math.Max(1, clientHeight / Config.LineHeight);
        }

        internal override string? GetInitialTurn()
        {
            if (!WaitForInput())
                return null;

            return BuildTurn();
        }

        internal override string? Step(string input)
        {
            if (IsStopped)
                return null;

            if (!WaitForInput())
                return null;

            if (console.State != ConsoleState.WaitInput)
                return null;

            try
            {
                ui.Invoke(() =>
                {
                    if (console.State == ConsoleState.WaitInput)
                        console.PressEnterKey(false, input, false);
                });

                return BuildTurn();
            }
            catch (Exception ex)
            {
                return JsonSerializer.Serialize(new
                {
                    error = ex.Message,
                    state = console.State.ToString()
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
#if HEADLESS
            console.SubmitTimeout();
            return BuildTurn();
#else
            throw new NotSupportedException("TINPUT timeout is only supported in HEADLESS builds.");
#endif
        }

        #region Turn helpers

        private bool WaitForInput()
        {
            var sw = Stopwatch.StartNew();
            while (!IsStopped)
            {
                var state = console.State;
                if (state == ConsoleState.WaitInput || state == ConsoleState.Quit || state == ConsoleState.Error)
                    return true;
                if (sw.ElapsedMilliseconds > TurnTimeoutMs)
                    return false;
                Thread.Sleep(PollIntervalMs);
            }
            return false;
        }

        private string BuildTurn()
        {
            var text = console.TakeAgentBuffer();
            var req = console.CurrentRequest;
            return JsonSerializer.Serialize(new
            {
                text,
                state = console.State.ToString(),
                inputType = req?.InputType.ToString(),
                needValue = req?.NeedValue ?? false,
                buttons = CollectVisibleButtons()
            });
        }

        /// <summary>
        /// 收集窗口默认可见区域中所有按钮的标签与对应输入值。
        /// </summary>
        private List<object> CollectVisibleButtons()
        {
            var buttons = new List<object>();
            var lines = console.DisplayLineList;
            if (lines == null || lines.Count == 0)
                return buttons;

            int start = Math.Max(0, lines.Count - _visibleLineCount);
            for (int i = start; i < lines.Count; i++)
            {
                var line = lines[i];
                if (line?.Buttons == null)
                    continue;
                foreach (var btn in line.Buttons)
                {
                    if (btn == null || !btn.IsButton)
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

internal record JsonlCommand(string type, string value);
