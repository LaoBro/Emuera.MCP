using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using MinorShift.Emuera.Runtime;
using MinorShift.Emuera.UI.Game;

namespace MinorShift.Emuera.GameView
{
    internal sealed class AgentCliProtocol : AgentProtocolBase
    {
        private readonly StringBuilder _buf = new();

        // 倒计时行状态
        private int _countdownLineTop = -1;
        private string _lastCountdownText = "";
        private int _lastCountdownWidth;

        // 按钮选择模式状态
        private bool _buttonMode;
        private List<ConsoleButtonString> _currentButtons = [];
        private int _selectedButtonIndex = -1;
        private int _buttonPromptWidth;

        private readonly TerminalCursor _cursor;
        private readonly bool _ansiEnabled;
        private long _lastRegionGeneration = -1;
        private long _lastButtonSyncGen = -1;

        public AgentCliProtocol(EmueraConsole console, IConsoleUI ui)
            : base(console, ui)
        {
            _ansiEnabled = Program.AnsiEnabled || !OperatingSystem.IsWindows();
            _cursor = new TerminalCursor(_ansiEnabled);
        }

        internal override string? GetInitialTurn() => null;
        internal override string? Step(string input) => null;

        internal void RunCliLoop()
        {
            FlushBuffer();
            if (Console.IsInputRedirected)
                RunPipeCliLoop(Console.In);
            else
                RunConsoleKeyLoop();
            FlushBuffer();
        }

        #region Main loops

        private void RunConsoleKeyLoop()
        {
            using var mouseInput = AgentCliMouseInput.TryCreate(this);
            bool mouseEnabled = mouseInput != null && mouseInput.IsEnabled;
            var token = StopToken;

            while (!token.IsCancellationRequested)
            {
                if (console._needFullRefresh)
                {
                    console._needFullRefresh = false;
                    FlushBuffer();
                    FullRefresh();
                    ResetCountdown();
                    SyncButtonState();
                    if (mouseEnabled) RefreshButtonRegions(mouseInput!, force: true);
                    continue;
                }

                var timeoutMs = console.InputTimeoutMs;
                if (timeoutMs.HasValue && timeoutMs.Value <= 0)
                {
                    OverwriteCountdownLine(console.TimeUpMessage ?? "");
                    ResetCountdown();
                    ClearInputBuffer();
                    console.SubmitTimeout();
                    FlushBuffer();
                    SyncButtonState();
                    if (mouseEnabled) RefreshButtonRegions(mouseInput!);
                    continue;
                }

                UpdateCountdown();

                if (mouseEnabled)
                    mouseInput!.PollEvents();
                else if (Console.KeyAvailable)
                    ProcessKey(Console.ReadKey(true));

                FlushBuffer();
                SyncButtonState();
                if (mouseEnabled) RefreshButtonRegions(mouseInput!);

                token.WaitHandle.WaitOne(PollIntervalMs);
            }
        }

        private void RunPipeCliLoop(TextReader input)
        {
            var token = StopToken;
            while (!token.IsCancellationRequested)
            {
                if (console._needFullRefresh)
                {
                    console._needFullRefresh = false;
                    FlushBuffer();
                    FullRefresh();
                }

                string? line = input.ReadLine();
                if (line == null) break;

                foreach (char ch in line)
                    ProcessChar(ch);
                ProcessChar('\r');
                FlushBuffer();
            }
        }

        #endregion

        #region Countdown

        private void UpdateCountdown()
        {
            if (console.IsDisplayTimeActive)
            {
                string currentText = console.BuildCountdownText();
                if (currentText != _lastCountdownText)
                {
                    if (_countdownLineTop < 0)
                    {
                        try { _countdownLineTop = Console.CursorTop - 1; }
                        catch { _countdownLineTop = -1; }
                    }
                    OverwriteCountdownLine(currentText);
                }
            }
            else if (_countdownLineTop >= 0)
            {
                ResetCountdown();
            }
        }

        private void OverwriteCountdownLine(string newText)
        {
            if (_countdownLineTop < 0) return;

            string padded = PadToWidth(newText, _lastCountdownWidth, out int newWidth);

            _cursor.Save(out int left, out int top);
            _cursor.Set(0, _countdownLineTop);
            if (_ansiEnabled)
                TerminalCursor.TryWrite($"\x1b[2K{padded}");
            else
                TerminalCursor.TryWrite(padded);
            _cursor.Set(left, top);

            _lastCountdownText = newText;
            _lastCountdownWidth = Math.Max(newWidth, _lastCountdownWidth);
        }

        private void ResetCountdown()
        {
            _countdownLineTop = -1;
            _lastCountdownText = "";
            _lastCountdownWidth = 0;
        }

        #endregion

        #region Terminal output

        private void FlushBuffer()
        {
            EraseTerminalRows();
            string text = console.TakeAgentBuffer();
            if (text.Length > 0)
                Console.Write(text);
        }

        private void FullRefresh()
        {
            _cursor.ClearScreen();

            var lines = console.DisplayLineList;
            if (lines.Count == 0) return;

            int consoleHeight = TerminalCursor.TryGetWindowHeight();
            int visibleLines = Math.Max(consoleHeight - 1, 1);
            int startLine = Math.Max(0, lines.Count - visibleLines);

            for (int i = startLine; i < lines.Count; i++)
            {
                string formatted = console.FormatLineForTerminal(lines[i]);
                Console.WriteLine(formatted.Length > 0 ? formatted : "");
            }
        }

        private void EraseTerminalRows()
        {
            int rows = console._pendingEraseRows;
            if (rows <= 0) return;
            console._pendingEraseRows = 0;

            _cursor.Save(out int savedLeft, out int savedTop);
            int consoleWidth = TerminalCursor.TryGetWindowWidth();

            for (int i = 0; i < rows; i++)
            {
                int targetTop = savedTop - 1 - i;
                if (targetTop < 0) break;
                _cursor.Set(0, targetTop);
                TerminalCursor.TryWrite(new string(' ', consoleWidth));
            }

            _cursor.Set(0, Math.Max(savedTop - rows, 0));
        }

        #endregion

        #region Keyboard / character input

        internal void ProcessChar(char ch)
        {
            if (ch == '\r' || ch == '\n')
            {
                WriteOutput("\r" + new string(' ', _buf.Length) + "\r", false);
                string input = _buf.ToString();
                _buf.Clear();
                DispatchInput(input);
            }
            else if (ch == '\b')
            {
                if (_buf.Length > 0)
                {
                    _buf.Remove(_buf.Length - 1, 1);
                    WriteOutput("\b \b", false);
                }
            }
            else if (ch == 27)
            {
                WriteOutput("\r" + new string(' ', _buf.Length) + "\r", false);
                _buf.Clear();
            }
            else if (!char.IsControl(ch))
            {
                _buf.Append(ch);
                WriteOutput(ch.ToString(), false);
            }
        }

        private void ProcessKey(ConsoleKeyInfo key)
        {
            if (_buttonMode)
            {
                ProcessButtonModeKey(key);
                return;
            }

            if (key.Key == ConsoleKey.Enter)
                ProcessChar('\r');
            else if (key.Key == ConsoleKey.Backspace)
                ProcessChar('\b');
            else if (key.Key == ConsoleKey.Escape)
                ProcessChar((char)27);
            else if (!char.IsControl(key.KeyChar))
                ProcessChar(key.KeyChar);
        }

        internal void ProcessKeyFromMouseInput(ConsoleKeyInfo key) => ProcessKey(key);

        #endregion

        #region Mouse dispatch

        internal void DispatchMouseClick(ConsoleButtonString btn)
        {
            if (console.State != ConsoleState.WaitInput) return;

            if (!_buttonMode)
            {
                DispatchInput("");
                return;
            }

            string input = btn.IsInteger ? btn.Input.ToString() : btn.Inputs;
            ExitButtonMode();
            DispatchInput(input);
        }

        internal void DispatchMouseMiss()
        {
            if (console.State != ConsoleState.WaitInput) return;
            if (_buttonMode) return;
            DispatchInput("");
        }

        #endregion

        #region Button mode

        private void ProcessButtonModeKey(ConsoleKeyInfo key)
        {
            if (_currentButtons.Count == 0) return;

            switch (key.Key)
            {
                case ConsoleKey.UpArrow:
                    _selectedButtonIndex = (_selectedButtonIndex - 1 + _currentButtons.Count) % _currentButtons.Count;
                    RenderButtonPrompt();
                    break;
                case ConsoleKey.DownArrow:
                    _selectedButtonIndex = (_selectedButtonIndex + 1) % _currentButtons.Count;
                    RenderButtonPrompt();
                    break;
                case ConsoleKey.Enter:
                    ConfirmButton();
                    break;
            }
        }

        private void ConfirmButton()
        {
            if (_selectedButtonIndex < 0 || _selectedButtonIndex >= _currentButtons.Count) return;

            var btn = _currentButtons[_selectedButtonIndex];
            string input = btn.IsInteger ? btn.Input.ToString() : btn.Inputs;
            ExitButtonMode();
            DispatchInput(input);
        }

        private void RenderButtonPrompt()
        {
            if (_selectedButtonIndex < 0 || _selectedButtonIndex >= _currentButtons.Count) return;

            var btn = _currentButtons[_selectedButtonIndex];
            string prompt = $"> [{_selectedButtonIndex + 1}/{_currentButtons.Count}] {btn} | [Up/Dn] Switch  [Enter] OK";

            string padded = PadToWidth(prompt, _buttonPromptWidth, out int newWidth);
            WriteOutput("\r" + padded, false);
            _buttonPromptWidth = Math.Max(newWidth, _buttonPromptWidth);
        }

        private void ClearButtonPrompt()
        {
            _cursor.ClearLine();
            _buttonPromptWidth = 0;
        }

        private void ExitButtonMode()
        {
            ClearButtonPrompt();
            _buttonMode = false;
            _currentButtons = [];
            _selectedButtonIndex = -1;
            _buttonPromptWidth = 0;
        }

        private void ClearInputBuffer()
        {
            if (_buf.Length > 0)
            {
                WriteOutput("\r" + new string(' ', _buf.Length) + "\r", false);
                _buf.Clear();
            }
        }

        private void SyncButtonState()
        {
            bool shouldHaveButtons = false;
            List<ConsoleButtonString> newButtons = [];

            if (console.State == ConsoleState.WaitInput)
            {
                var req = console.CurrentRequest;
                if (req != null && req.InputType != InputType.EnterKey && req.InputType != InputType.AnyKey)
                {
                    long currentGen = console.LastButtonGeneration;
                    if (_buttonMode && currentGen == _lastButtonSyncGen)
                    {
                        return;
                    }

                    newButtons = console.CollectCurrentButtons();
                    _lastButtonSyncGen = currentGen;
                    if (newButtons.Count > 0)
                        shouldHaveButtons = true;
                }
            }
            else
            {
                _lastButtonSyncGen = -1;
            }

            if (!shouldHaveButtons)
            {
                if (_buttonMode) ExitButtonMode();
            }
            else if (!_buttonMode)
            {
                _buttonMode = true;
                _currentButtons = newButtons;
                _selectedButtonIndex = 0;
                ClearInputBuffer();
                RenderButtonPrompt();
            }
            else if (!ButtonListEquals(_currentButtons, newButtons))
            {
                _currentButtons = newButtons;
                _selectedButtonIndex = Math.Clamp(_selectedButtonIndex, 0, newButtons.Count - 1);
                RenderButtonPrompt();
            }
        }

        private void RefreshButtonRegions(AgentCliMouseInput mouseInput, bool force = false)
        {
            long currentGen = console.LastButtonGeneration;
            if (!force && currentGen == _lastRegionGeneration) return;

            _lastRegionGeneration = currentGen;
            mouseInput.ClearRegions();

            var lines = console.DisplayLineList;
            if (lines == null || lines.Count == 0) return;

            int windowTop, windowHeight;
            try
            {
                windowTop = Console.WindowTop;
                windowHeight = Console.WindowHeight;
            }
            catch { return; }

            int visibleLines = Math.Min(windowHeight - 1, lines.Count);
            int startLine = Math.Max(0, lines.Count - visibleLines);

            for (int i = 0; i < visibleLines; i++)
            {
                int lineIndex = startLine + i;
                var line = lines[lineIndex];
                if (line?.Buttons == null || line.Buttons.Length == 0) continue;

                string formatted = console.FormatLineForTerminal(line);
                mouseInput.RecordLineRegions(formatted, windowTop + i, line.Buttons, currentGen);
            }
        }

        private static bool ButtonListEquals(List<ConsoleButtonString> a, List<ConsoleButtonString> b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
            {
                if (a[i].Generation != b[i].Generation) return false;
                if (a[i].IsInteger ? a[i].Input != b[i].Input : a[i].Inputs != b[i].Inputs)
                    return false;
            }
            return true;
        }

        #endregion

        #region Terminal helpers

        private static int GetDisplayWidth(string str) => TerminalDisplayWidth.GetDisplayWidth(str);

        private static string PadToWidth(string text, int minWidth, out int actualWidth)
        {
            actualWidth = GetDisplayWidth(text);
            if (actualWidth < minWidth)
                return text + new string(' ', minWidth - actualWidth);
            return text;
        }

        #endregion

        protected override void OnInputRejected(string reason)
        {
            WriteOutput($"[终端] {reason}");
        }
    }
}
