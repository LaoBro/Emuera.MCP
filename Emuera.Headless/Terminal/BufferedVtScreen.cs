using System;
using System.Text;

namespace MinorShift.Emuera.GameView
{
    /// <summary>
    /// VT sink 内存缓冲实现（Phase 3-0 / Q9）。
    /// 捕获所有 VT 转义序列到 StringBuilder，供 Phase 4 双路径比对逐字节比较。
    /// WindowWidth/WindowHeight 由构造函数指定（不读真实终端），GetCurrentRow 跟踪虚拟光标行。
    /// </summary>
    internal sealed class BufferedVtScreen : IAgentCliVtScreen
    {
        private readonly StringBuilder _buf = new();
        private readonly int _windowWidth;
        private readonly int _windowHeight;
        private bool _inAltScreen;
        private bool _disposed;
        private int _currentRow;

        internal BufferedVtScreen(int windowWidth = 80, int windowHeight = 25)
        {
            _windowWidth = windowWidth;
            _windowHeight = windowHeight;
        }

        /// <summary>累积的 VT 字节序列（escape sequences + text）。</summary>
        internal string GetOutput() => _buf.ToString();

        /// <summary>清空缓冲（重用实例时调用）。</summary>
        internal void Clear() => _buf.Clear();

        public void EnterAlternateScreen()
        {
            _buf.Append("\x1b[?1049h");
            _inAltScreen = true;
        }

        public void LeaveAlternateScreen()
        {
            if (!_inAltScreen) return;
            _buf.Append("\x1b[?1049l");
            _inAltScreen = false;
        }

        public void ClearScreen()
        {
            _buf.Append("\x1b[2J\x1b[H");
            _currentRow = 0;
        }

        public void SetCursor(int row, int col)
        {
            _buf.Append($"\x1b[{row + 1};{col + 1}H");
            _currentRow = row;
        }

        public void ClearLine(int row)
        {
            _buf.Append($"\x1b[{row + 1};1H\x1b[2K");
            _currentRow = row;
        }

        public void ClearLineToEnd()
        {
            _buf.Append("\x1b[K");
        }

        public void WriteAt(int row, int col, string text)
        {
            SetCursor(row, col);
            _buf.Append(text);
        }

        public void WriteLineAt(int row, string text)
        {
            SetCursor(row, 0);
            _buf.Append(text);
            _buf.Append("\x1b[K");
        }

        public int WindowWidth => _windowWidth;
        public int WindowHeight => _windowHeight;
        public int GetCurrentRow() => _currentRow;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            LeaveAlternateScreen();
        }
    }
}
