using System;
using MinorShift.Emuera.UI.Game;

namespace MinorShift.Emuera.GameView
{
    /// <summary>
    /// VtInputHandler 的依赖倒置接口（ADR-0009）。
    /// AgentCliProtocol 实现它，内部把 scroll 成员（ScrollOffset/DispatchWheel/DispatchScroll）委托给 ScrollController。
    /// IsWaitingPrimitive 门卫通过接口暴露，让 fake host 能绕过 primitive 路径（不需真实 EmueraConsole）。
    /// 测试用 fake IVtHost 隔离 VtInputHandler 路由测试。
    /// </summary>
    internal interface IVtHost
    {
        /// <summary>请求退出（Ctrl+C / CancelKeyPress）。</summary>
        void RequestExit();

        /// <summary>游戏控制台（用于调 PressPrimitiveKey / InputMouseKey，仅 primitive 路径）。</summary>
        EmueraConsole GameConsole { get; }

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
    }
}
