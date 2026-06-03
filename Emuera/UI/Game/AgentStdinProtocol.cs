using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json;
using System.Threading;
using MinorShift.Emuera.Forms;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.UI.Game;

namespace MinorShift.Emuera.GameView
{
    internal abstract class AgentStdinProtocol : AgentProtocolBase
    {
        /// <summary>默认窗口高度内可显示的行数。用于采集可见按钮。</summary>
        private readonly int _visibleLineCount;

        protected AgentStdinProtocol(EmueraConsole console, MainWindow window)
            : base(console, window)
        {
            // 在 UI 线程中缓存可见行数（构造时安全访问 window）
            int clientHeight = window.MainPicBox?.Height ?? Config.WindowY;
            _visibleLineCount = Math.Max(1, clientHeight / Config.LineHeight);
        }

        protected virtual void OnStart() { }

        protected abstract void HandleMessage(string line);

        public override void Run(string firstLine)
        {
            _thread = new Thread(() =>
            {
                OnStart();
                HandleMessage(firstLine);
                ReadStdinLoop(HandleMessage);
            })
            {
                IsBackground = true,
                Name = "TerminalAgent"
            };
            _thread.Start();
        }

        protected bool WaitForInput()
        {
            var sw = Stopwatch.StartNew();
            while (!IsStopped())
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

        protected string BuildTurn()
        {
            var text = console.ReadAgentBuffer();
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
        /// TODO: 部分按钮可能跨多行显示（较少见），当前按行独立采集，
        ///       未来可基于 Generation 合并跨行按钮的文本片段。
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

        /// <summary>
        /// Submit input and advance one game turn. Returns next-turn JSON, or null on timeout.
        /// </summary>
        protected string SubmitAndGetTurn(string value)
        {
            if (!WaitForInput()) return null;
            if (console.State != ConsoleState.WaitInput) return null;

            console.TakeAgentBuffer();
            window.Invoke(new Action(() =>
            {
                if (console.State == ConsoleState.WaitInput)
                    console.PressEnterKey(false, value, false);
            }));

            return BuildTurn();
        }

        /// <summary>
        /// Wait for game to be ready and return current turn JSON, or null on timeout.
        /// </summary>
        protected string GetTurn()
        {
            if (!WaitForInput()) return null;
            return BuildTurn();
        }

        private void ReadStdinLoop(Action<string> onLine)
        {
            while (!IsStopped())
            {
                string line;
                try { line = Console.ReadLine(); }
                catch (ThreadInterruptedException) { break; }
                if (line == null) break;
                onLine(line);
            }
            if (!IsStopped())
                window.BeginInvoke(new Action(() => window.Close()));
        }
    }
}