using System;

namespace MinorShift.Emuera.GameView
{
    /// <summary>
    /// VT 渲染后端抽象（Phase 3-0 / Q9）。
    /// 生产实现 <see cref="AgentCliVtScreen"/> 直写真实终端；
    /// 测试/双路径比对实现 <see cref="BufferedVtScreen"/> 捕获 VT 字节到内存缓冲。
    /// Phase 4 双路径比对（--strict）经此抽象同时写两个 sink 并比较字节序列。
    /// </summary>
    internal interface IAgentCliVtScreen : IDisposable
    {
        void EnterAlternateScreen();
        void LeaveAlternateScreen();
        void ClearScreen();
        void SetCursor(int row, int col);
        void ClearLine(int row);
        void ClearLineToEnd();
        void WriteAt(int row, int col, string text);
        void WriteLineAt(int row, string text);
        int WindowWidth { get; }
        int WindowHeight { get; }
        int GetCurrentRow();
    }

    /// <summary>
    /// VT 渲染后端：备用屏幕缓冲区生命周期 + 绝对定位渲染。
    /// 仅在 DA1 探测通过后使用，封装所有 VT 渲染序列。
    /// 坐标全部为 viewport 坐标（0-based），备用屏下 WindowTop 恒为 0。
    /// ADR-0009：scroll 算术已移至 ScrollController，本类只保留 VT I/O 职责。
    /// Phase 3-0：实现 IAgentCliVtScreen 接口，允许 BufferedVtScreen 替换注入。
    /// </summary>
    internal sealed class AgentCliVtScreen : IAgentCliVtScreen, IWindowSize
    {
        private bool _inAltScreen;
        private bool _disposed;

        /// <summary>进入备用屏幕缓冲区（ESC[?1049h）。主屏内容暂存，退出后恢复。</summary>
        public void EnterAlternateScreen()
        {
            TerminalCursor.TryWrite("\x1b[?1049h");
            _inAltScreen = true;
        }

        /// <summary>退出备用屏幕缓冲区（ESC[?1049l）。主屏恢复进入前内容。</summary>
        public void LeaveAlternateScreen()
        {
            if (!_inAltScreen) return;
            TerminalCursor.TryWrite("\x1b[?1049l");
            _inAltScreen = false;
        }

        /// <summary>全屏清屏并归位光标。</summary>
        public void ClearScreen()
        {
            TerminalCursor.TryWrite("\x1b[2J\x1b[H");
        }

        /// <summary>绝对定位光标到指定 viewport 坐标。</summary>
        public void SetCursor(int row, int col)
        {
            TerminalCursor.TryWrite($"\x1b[{row + 1};{col + 1}H");
        }

        /// <summary>清除指定行的全部内容并归位到行首。</summary>
        public void ClearLine(int row)
        {
            TerminalCursor.TryWrite($"\x1b[{row + 1};1H\x1b[2K");
        }

        /// <summary>从光标当前位置清除到行尾。</summary>
        public void ClearLineToEnd()
        {
            TerminalCursor.TryWrite("\x1b[K");
        }

        /// <summary>在指定坐标写入文本（不清行尾）。</summary>
        public void WriteAt(int row, int col, string text)
        {
            SetCursor(row, col);
            TerminalCursor.TryWrite(text);
        }

        /// <summary>在指定行行首写入文本并清除行尾（= SetCursor(row,0) + Write + ClearLineToEnd）。</summary>
        public void WriteLineAt(int row, string text)
        {
            SetCursor(row, 0);
            TerminalCursor.TryWrite(text);
            TerminalCursor.TryWrite("\x1b[K");
        }

        /// <summary>终端可见列数。</summary>
        public int WindowWidth => TerminalCursor.TryGetWindowWidth();

        /// <summary>终端可见行数。</summary>
        public int WindowHeight => TerminalCursor.TryGetWindowHeight();

        /// <summary>获取当前光标所在 viewport 行号。</summary>
        public int GetCurrentRow()
        {
            try { return Console.CursorTop; }
            catch (Exception) { /* redirected console，默认 0 */ return 0; }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            LeaveAlternateScreen();
        }
    }
}
