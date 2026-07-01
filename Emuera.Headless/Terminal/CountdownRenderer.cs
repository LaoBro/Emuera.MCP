using System;
using MinorShift.Emuera.UI.Game;

namespace MinorShift.Emuera.GameView
{
    /// <summary>
    /// 倒计时行渲染：在终端固定行覆盖写入倒计时文本，支持 VT 绝对定位与非 VT 光标定位。
    /// 从 AgentCliProtocol 拆分以隔离倒计时显示状态。
    /// </summary>
    internal sealed class CountdownRenderer
    {
        private readonly EmueraConsole _console;
        private readonly Func<AgentCliVtScreen?> _getScreen;
        private readonly TerminalCursor _cursor;
        private readonly bool _ansiEnabled;

        // 倒计时行状态（viewport row，备用屏下与 buffer row 等价）
        private int _countdownLineRow = -1;
        private string _lastCountdownText = "";
        private int _lastCountdownWidth;

        public CountdownRenderer(
            EmueraConsole console,
            Func<AgentCliVtScreen?> getScreen,
            TerminalCursor cursor,
            bool ansiEnabled)
        {
            _console = console;
            _getScreen = getScreen;
            _cursor = cursor;
            _ansiEnabled = ansiEnabled;
        }

        /// <summary>检测倒计时状态变化并刷新显示；倒计时结束时重置。</summary>
        internal void Update()
        {
            if (_console.IsDisplayTimeActive)
            {
                string currentText = _console.BuildCountdownText();
                if (currentText != _lastCountdownText)
                {
                    if (_countdownLineRow < 0)
                    {
                        try { _countdownLineRow = Console.CursorTop - 1; }
                        catch (Exception) { /* CursorTop 探测失败，禁用行覆盖 */ _countdownLineRow = -1; }
                    }
                    Overwrite(currentText);
                }
            }
            else if (_countdownLineRow >= 0)
            {
                Reset();
            }
        }

        /// <summary>用超时消息覆盖倒计时行。</summary>
        internal void Overwrite(string newText)
        {
            if (_countdownLineRow < 0) return;

            string padded = PadToWidth(newText, _lastCountdownWidth, out int newWidth);

            var screen = _getScreen();
            if (screen != null)
            {
                // VT 模式：绝对定位 + 清行尾
                screen.WriteLineAt(_countdownLineRow, padded);
            }
            else
            {
                _cursor.Save(out int left, out int top);
                _cursor.Set(0, _countdownLineRow);
                if (_ansiEnabled)
                    TerminalCursor.TryWrite($"\x1b[2K{padded}");
                else
                    TerminalCursor.TryWrite(padded);
                _cursor.Set(left, top);
            }

            _lastCountdownText = newText;
            _lastCountdownWidth = Math.Max(newWidth, _lastCountdownWidth);
        }

        /// <summary>重置倒计时行状态。</summary>
        internal void Reset()
        {
            _countdownLineRow = -1;
            _lastCountdownText = "";
            _lastCountdownWidth = 0;
        }

        private static int GetDisplayWidth(string str) => TerminalDisplayWidth.GetDisplayWidth(str);

        private static string PadToWidth(string text, int minWidth, out int actualWidth)
        {
            actualWidth = GetDisplayWidth(text);
            if (actualWidth < minWidth)
                return text + new string(' ', minWidth - actualWidth);
            return text;
        }
    }
}
