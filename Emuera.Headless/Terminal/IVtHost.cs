using System;
using MinorShift.Emuera.UI.Game;

namespace MinorShift.Emuera.GameView
{
    /// <summary>
    /// VtInputHandler 的依赖倒置接口（ADR-0009 / ADR-0010）。
    /// AgentCliProtocol 实现它，内部把 scroll 成员（ScrollOffset/DispatchWheel/DispatchScroll）委托给 ScrollController。
    /// ADR-0010：移除 GameConsole 属性，改为直接暴露 PressPrimitiveKey/InputMouseKey 原语分发方法，
    /// 让 VtInputHandler 不再依赖 EmueraConsole 类型，fake host 可用 spy 计数器测试 primitive 路径。
    /// </summary>
    internal interface IVtHost
    {
        /// <summary>请求退出（Ctrl+C / CancelKeyPress）。</summary>
        void RequestExit();

        /// <summary>是否在 primitive 输入等待状态（TINPUT #xxx）。门卫读取，false 时绕过 primitive 路径。</summary>
        bool IsWaitingPrimitive { get; }

        /// <summary>当前 Scroll Offset（VtInputHandler 门卫读取）。</summary>
        int ScrollOffset { get; }

        /// <summary>滚轮事件分发（cb=64/65 → delta=±3）。</summary>
        void DispatchWheel(int delta);

        /// <summary>键盘滚动热键分发（PgUp/PgDn/Home/End）。</summary>
        void DispatchScroll(ScrollAction action);

        /// <summary>VT 解析器输出的 ConsoleKeyInfo 入口。</summary>
        void ProcessKeyFromVt(ConsoleKeyInfo key);

        /// <summary>鼠标点击命中按钮的分发。</summary>
        void DispatchMouseClick(ConsoleButtonString btn);

        /// <summary>鼠标点击未命中的分发（推进 AnyKey/EnterKey）。</summary>
        void DispatchMouseMiss();

        /// <summary>primitive 键盘输入分发（IsWaitingPrimitive=true 时由 OnKeyEvent 调用）。</summary>
        void PressPrimitiveKey(int keycode, int keydata, int keymod);

        /// <summary>primitive 鼠标输入分发（IsWaitingPrimitive=true 时由 OnMouseEvent 调用）。</summary>
        void InputMouseKey(int type, int result1, int result2, int result3, int result4, long result5);
    }
}
