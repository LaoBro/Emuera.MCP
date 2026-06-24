using System;
using System.Text;

namespace MinorShift.Emuera.GameView
{
    /// <summary>
    /// VT 输入解析状态机：接收 raw bytes，输出 KeyEvent / MouseEvent。
    /// 状态：GROUND → ESC → CSI → SGR_MOUSE。
    /// DA1 响应在探测阶段消费，主循环中若偶发到达则按 CSI 序列忽略。
    /// </summary>
    internal sealed class VtParser
    {
        private readonly AgentCliVtInput _owner;

        private enum State { Ground, Esc, Csi, SgrMouse }

        private State _state = State.Ground;
        private readonly StringBuilder _params = new();
        private readonly byte[] _utf8Buffer = new byte[4];
        private int _utf8Len;

        internal VtParser(AgentCliVtInput owner)
        {
            _owner = owner;
        }

        internal void Feed(byte b)
        {
            switch (_state)
            {
                case State.Ground: HandleGround(b); break;
                case State.Esc: HandleEsc(b); break;
                case State.Csi: HandleCsi(b); break;
                case State.SgrMouse: HandleSgrMouse(b); break;
            }
        }

        private void HandleGround(byte b)
        {
            if (b == 0x1B) // ESC
            {
                _state = State.Esc;
                return;
            }

            if (b == 0x03) // Ctrl+C
            {
                _owner.OnKeyEvent((ConsoleKey)0, '\x03');
                return;
            }

            // UTF-8 缓冲
            if (_utf8Len == 0)
            {
                int expected = ExpectedUtf8Length(b);
                if (expected < 0)
                {
                    // 无效 UTF-8 首字节，作为单字节 Latin-1 派发
                    _owner.OnKeyEvent((ConsoleKey)0, (char)b);
                    return;
                }
                _utf8Buffer[0] = b;
                _utf8Len = 1;
                if (expected == 1)
                {
                    DispatchUtf8();
                }
                return;
            }

            // 收集后续字节（0x80-0xBF）
            if (b >= 0x80 && b <= 0xBF)
            {
                _utf8Buffer[_utf8Len++] = b;
                int expected = ExpectedUtf8Length(_utf8Buffer[0]);
                if (_utf8Len >= expected)
                    DispatchUtf8();
                return;
            }

            // 后续字节不是 continuation byte，先派发已缓冲的（可能不完整），再处理当前字节
            DispatchUtf8();
            Feed(b);
        }

        private void HandleEsc(byte b)
        {
            if (b == (byte)'[')
            {
                _state = State.Csi;
                _params.Clear();
                return;
            }

            // ESC 单独作为按键
            _owner.OnKeyEvent(ConsoleKey.Escape, '\x1b');
            _state = State.Ground;

            // 重新处理当前字节
            HandleGround(b);
        }

        private void HandleCsi(byte b)
        {
            if (b == (byte)'<')
            {
                _state = State.SgrMouse;
                _params.Clear();
                return;
            }

            // 终结字节（0x40-0x7E）
            if (b >= 0x40 && b <= 0x7E)
            {
                HandleCsiTerminator(b);
                _state = State.Ground;
                return;
            }

            // 收集参数
            _params.Append((char)b);
        }

        private void HandleCsiTerminator(byte b)
        {
            switch (b)
            {
                case (byte)'A': // ↑
                    _owner.OnKeyEvent(ConsoleKey.UpArrow, '\0');
                    break;
                case (byte)'B': // ↓
                    _owner.OnKeyEvent(ConsoleKey.DownArrow, '\0');
                    break;
                case (byte)'C': // →
                    _owner.OnKeyEvent(ConsoleKey.RightArrow, '\0');
                    break;
                case (byte)'D': // ←
                    _owner.OnKeyEvent(ConsoleKey.LeftArrow, '\0');
                    break;
                case (byte)'H': // Home
                    _owner.OnKeyEvent(ConsoleKey.Home, '\0');
                    break;
                case (byte)'F': // End
                    _owner.OnKeyEvent(ConsoleKey.End, '\0');
                    break;
                // 'c' = DA1 响应，忽略；其他 CSI 序列忽略
            }
        }

        private void HandleSgrMouse(byte b)
        {
            if (b == (byte)'M' || b == (byte)'m')
            {
                // 解析 SGR mouse: ESC[<Cb;Cx;Cy M/m
                string[] parts = _params.ToString().Split(';');
                if (parts.Length == 3
                    && int.TryParse(parts[0], out int cb)
                    && int.TryParse(parts[1], out int cx)
                    && int.TryParse(parts[2], out int cy))
                {
                    // 1-based → 0-based viewport 坐标
                    int row = cy - 1;
                    int col = cx - 1;
                    bool isPress = b == (byte)'M';

                    // 只报告左键（cb=0 按下, cb=2 释放）
                    if (cb == 0 || cb == 2)
                        _owner.OnMouseEvent(row, col, isPress);
                }
                _state = State.Ground;
                return;
            }

            _params.Append((char)b);
        }

        private void DispatchUtf8()
        {
            if (_utf8Len == 0) return;
            try
            {
                string s = Encoding.UTF8.GetString(_utf8Buffer, 0, _utf8Len);
                if (s.Length > 0)
                    _owner.OnKeyEvent((ConsoleKey)0, s[0]);
            }
            catch
            {
                // 解码失败，丢弃
            }
            _utf8Len = 0;
        }

        private static int ExpectedUtf8Length(byte first)
        {
            if (first < 0x80) return 1;
            if ((first & 0xE0) == 0xC0) return 2;
            if ((first & 0xF0) == 0xE0) return 3;
            if ((first & 0xF8) == 0xF0) return 4;
            return -1; // 无效首字节
        }
    }
}
