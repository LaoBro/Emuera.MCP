using System;

namespace MinorShift.Emuera.GameView
{
    /// <summary>
    /// VT 解析器的事件输出接口（ADR-0010）。
    /// VtParser 通过此接口回调解析结果，不直接依赖 VtInputHandler 具体类。
    /// 测试用 fake IVtEventSink 直接断言解析回调参数（不需 fake host / EmueraConsole）。
    /// </summary>
    internal interface IVtEventSink
    {
        /// <summary>键盘事件回调（key=0 表示非特殊键，ch 为字符）。</summary>
        void OnKeyEvent(ConsoleKey key, char ch);

        /// <summary>鼠标事件回调（row/col 为 0-based viewport 坐标，buttonCode 为 SGR cb，isPress=true 表示按下）。</summary>
        void OnMouseEvent(int row, int col, int buttonCode, bool isPress);
    }
}
