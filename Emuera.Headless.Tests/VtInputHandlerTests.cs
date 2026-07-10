using System;
using System.Collections.Generic;
using MinorShift.Emuera.GameView;
using MinorShift.Emuera.Terminal.Platform;
using MinorShift.Emuera.UI.Game;
using Xunit;

namespace Emuera.Headless.Tests;

/// <summary>
/// VtInputHandler 路由单元测试（ADR-0009 C4/C7b）。
/// 用 FakeVtHost 隔离 VtInputHandler，Feed SGR mouse / 键盘热键字节，
/// 断言 host 收到预期的 DispatchWheel/DispatchScroll/ProcessKeyFromVt 调用。
/// 不依赖真实 EmueraConsole / TerminalCursor / VT I/O。
/// </summary>
public class VtInputHandlerTests
{
    // ---------- C4：Scroll Mode 只读门卫 ----------

    [Fact]
    public void C4_scroll_mode_key_is_dropped()
    {
        var host = new FakeVtHost(scrollOffset: 5);
        var handler = new VtInputHandler(host, new FakeTerminalInput());

        // Scroll Mode（offset>0）下普通键应被丢弃
        handler.OnKeyEvent(ConsoleKey.A, 'a');

        Assert.Equal(0, host.ProcessKeyFromVtCalls);
        Assert.Equal(0, host.DispatchMouseClickCalls);
    }

    [Fact]
    public void C4_scroll_mode_scroll_hotkeys_still_route()
    {
        var host = new FakeVtHost(scrollOffset: 5);
        var handler = new VtInputHandler(host, new FakeTerminalInput());

        // 滚动热键在 Scroll Mode 下仍路由
        handler.OnKeyEvent(ConsoleKey.PageUp, '\0');
        handler.OnKeyEvent(ConsoleKey.PageDown, '\0');
        handler.OnKeyEvent(ConsoleKey.Home, '\0');
        handler.OnKeyEvent(ConsoleKey.End, '\0');

        Assert.Equal(4, host.DispatchScrollCalls.Count);
        Assert.Equal(ScrollAction.PageUp, host.DispatchScrollCalls[0]);
        Assert.Equal(ScrollAction.PageDown, host.DispatchScrollCalls[1]);
        Assert.Equal(ScrollAction.Home, host.DispatchScrollCalls[2]);
        Assert.Equal(ScrollAction.End, host.DispatchScrollCalls[3]);
    }

    [Fact]
    public void C4_normal_mode_key_routes_to_ProcessKeyFromVt()
    {
        var host = new FakeVtHost(scrollOffset: 0);
        var handler = new VtInputHandler(host, new FakeTerminalInput());

        handler.OnKeyEvent(ConsoleKey.A, 'a');

        Assert.Equal(1, host.ProcessKeyFromVtCalls);
        Assert.Equal('a', host.LastKey.KeyChar);
        Assert.Equal(ConsoleKey.A, host.LastKey.Key);
    }

    [Fact]
    public void C4_scroll_mode_mouse_click_ignored()
    {
        var host = new FakeVtHost(scrollOffset: 5);
        var handler = new VtInputHandler(host, new FakeTerminalInput());

        // Scroll Mode 下鼠标左键点击应被忽略
        handler.OnMouseEvent(row: 0, col: 0, buttonCode: 0, isPress: true);

        Assert.Equal(0, host.DispatchMouseClickCalls);
        Assert.Equal(0, host.DispatchMouseMissCalls);
    }

    // ---------- C7b：滚轮事件路由 ----------

    [Fact]
    public void C7b_wheel_up_routes_to_DispatchWheel_positive_delta()
    {
        var host = new FakeVtHost(scrollOffset: 0);
        var handler = new VtInputHandler(host, new FakeTerminalInput());

        // cb=64 → 滚轮上 → delta=+3
        handler.OnMouseEvent(row: 5, col: 10, buttonCode: 64, isPress: false);

        Assert.Equal(1, host.DispatchWheelCalls.Count);
        Assert.Equal(3, host.DispatchWheelCalls[0]);
    }

    [Fact]
    public void C7b_wheel_down_routes_to_DispatchWheel_negative_delta()
    {
        var host = new FakeVtHost(scrollOffset: 0);
        var handler = new VtInputHandler(host, new FakeTerminalInput());

        // cb=65 → 滚轮下 → delta=-3
        handler.OnMouseEvent(row: 5, col: 10, buttonCode: 65, isPress: false);

        Assert.Equal(1, host.DispatchWheelCalls.Count);
        Assert.Equal(-3, host.DispatchWheelCalls[0]);
    }

    [Fact]
    public void C7b_wheel_routes_even_in_scroll_mode()
    {
        var host = new FakeVtHost(scrollOffset: 10);
        var handler = new VtInputHandler(host, new FakeTerminalInput());

        // 滚轮事件在 Scroll Mode 下仍路由（cb=64/65 始终处理）
        handler.OnMouseEvent(row: 5, col: 10, buttonCode: 64, isPress: false);
        handler.OnMouseEvent(row: 5, col: 10, buttonCode: 65, isPress: false);

        Assert.Equal(2, host.DispatchWheelCalls.Count);
        Assert.Equal(3, host.DispatchWheelCalls[0]);
        Assert.Equal(-3, host.DispatchWheelCalls[1]);
    }

    [Fact]
    public void C7b_wheel_ignores_isPress_flag()
    {
        var host = new FakeVtHost(scrollOffset: 0);
        var handler = new VtInputHandler(host, new FakeTerminalInput());

        // 滚轮事件不依赖 isPress（SGR mouse 滚轮无 press/release 语义）
        handler.OnMouseEvent(row: 0, col: 0, buttonCode: 64, isPress: false);
        handler.OnMouseEvent(row: 0, col: 0, buttonCode: 64, isPress: true);

        Assert.Equal(2, host.DispatchWheelCalls.Count);
    }

    // ---------- Ctrl+C 退出 ----------

    [Fact]
    public void Ctrl_C_requests_exit()
    {
        var host = new FakeVtHost(scrollOffset: 0);
        var handler = new VtInputHandler(host, new FakeTerminalInput());

        handler.OnKeyEvent(ConsoleKey.C, '\x03');

        Assert.Equal(1, host.RequestExitCalls);
        Assert.Equal(0, host.ProcessKeyFromVtCalls);
    }

    // ---------- 键盘热键在正常模式下的行为 ----------

    [Fact]
    public void PageUp_in_normal_mode_routes_to_DispatchScroll()
    {
        var host = new FakeVtHost(scrollOffset: 0);
        var handler = new VtInputHandler(host, new FakeTerminalInput());

        handler.OnKeyEvent(ConsoleKey.PageUp, '\0');

        Assert.Equal(1, host.DispatchScrollCalls.Count);
        Assert.Equal(ScrollAction.PageUp, host.DispatchScrollCalls[0]);
        Assert.Equal(0, host.ProcessKeyFromVtCalls);
    }

    [Fact]
    public void Enter_key_routes_to_ProcessKeyFromVt()
    {
        var host = new FakeVtHost(scrollOffset: 0);
        var handler = new VtInputHandler(host, new FakeTerminalInput());

        handler.OnKeyEvent(ConsoleKey.Enter, '\r');

        Assert.Equal(1, host.ProcessKeyFromVtCalls);
        Assert.Equal(ConsoleKey.Enter, host.LastKey.Key);
    }

    // ---------- Primitive 模式（C8，第三阶段） ----------
    // C8 primitive 路径测试需要真实 EmueraConsole（IsWaitingPrimitive / PressPrimitiveKey /
    // InputMouseKey 是实例方法，EmueraConsole sealed 无法 mock）。留第三阶段 IEmueraConsole 抽象后补。

    // ---------- Fakes ----------

    private sealed class FakeVtHost : IVtHost
    {
        private readonly int _scrollOffset;

        public FakeVtHost(int scrollOffset = 0)
        {
            _scrollOffset = scrollOffset;
        }

        public int RequestExitCalls;
        public int ProcessKeyFromVtCalls;
        public int DispatchMouseClickCalls;
        public int DispatchMouseMissCalls;
        public ConsoleKeyInfo LastKey;
        public readonly List<int> DispatchWheelCalls = new();
        public readonly List<ScrollAction> DispatchScrollCalls = new();

        public EmueraConsole GameConsole => throw new NotImplementedException(
            "FakeVtHost 不提供真实 EmueraConsole；primitive 路径测试留第三阶段");

        // 门卫返回 false——绕过 primitive 路径，不需真实 EmueraConsole
        public bool IsWaitingPrimitive => false;

        public int ScrollOffset => _scrollOffset;

        public void RequestExit() => RequestExitCalls++;

        public void DispatchWheel(int delta) => DispatchWheelCalls.Add(delta);

        public void DispatchScroll(ScrollAction action) => DispatchScrollCalls.Add(action);

        public void ProcessKeyFromVt(ConsoleKeyInfo key)
        {
            ProcessKeyFromVtCalls++;
            LastKey = key;
        }

        public void DispatchMouseClick(ConsoleButtonString btn) => DispatchMouseClickCalls++;

        public void DispatchMouseMiss() => DispatchMouseMissCalls++;
    }

    private sealed class FakeTerminalInput : ITerminalInput
    {
        public bool HasInputAvailable() => false;
        public int ReadByte() => -1;
        public void EnableSgrMouse() { }
        public void DisableSgrMouse() { }
        public void Dispose() { }
    }
}
