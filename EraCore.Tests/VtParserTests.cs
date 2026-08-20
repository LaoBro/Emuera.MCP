using System;
using System.Collections.Generic;
using MinorShift.Emuera.GameView;
using Xunit;

namespace Emuera.Headless.Tests;

/// <summary>
/// VtParser SGR/CSI 解析单元测试（ADR-0010 C1）。
/// 用 FakeEventSink 直接断言解析回调参数——不需 fake host / EmueraConsole / VtInputHandler。
/// 覆盖：Ctrl+C、UTF-8、ESC、CSI 方向键、CSI~ PgUp/PgDn、SGR mouse 点击/滚轮。
/// </summary>
public class VtParserTests
{
    // ---------- Ctrl+C ----------

    [Fact]
    public void Ctrl_C_dispatches_key_event_with_ctrl_c_char()
    {
        var sink = new FakeEventSink();
        var parser = new VtParser(sink);

        parser.Feed(0x03);

        Assert.Single(sink.KeyEvents);
        Assert.Equal('\x03', sink.KeyEvents[0].ch);
    }

    // ---------- ASCII ----------

    [Fact]
    public void ASCII_byte_dispatches_as_single_key_event()
    {
        var sink = new FakeEventSink();
        var parser = new VtParser(sink);

        parser.Feed((byte)'A');

        Assert.Single(sink.KeyEvents);
        Assert.Equal('A', sink.KeyEvents[0].ch);
        Assert.Equal((ConsoleKey)0, sink.KeyEvents[0].key);
    }

    // ---------- UTF-8 ----------

    [Fact]
    public void UTF8_two_byte_sequence_dispatches_single_char()
    {
        var sink = new FakeEventSink();
        var parser = new VtParser(sink);

        // 'é' = 0xC3 0xA9
        parser.Feed(0xC3);
        parser.Feed(0xA9);

        Assert.Single(sink.KeyEvents);
        Assert.Equal('é', sink.KeyEvents[0].ch);
    }

    [Fact]
    public void UTF8_three_byte_sequence_dispatches_single_char()
    {
        var sink = new FakeEventSink();
        var parser = new VtParser(sink);

        // '中' = 0xE4 0xB8 0xAD
        parser.Feed(0xE4);
        parser.Feed(0xB8);
        parser.Feed(0xAD);

        Assert.Single(sink.KeyEvents);
        Assert.Equal('中', sink.KeyEvents[0].ch);
    }

    // ---------- ESC ----------

    [Fact]
    public void ESC_alone_dispatches_escape_key()
    {
        var sink = new FakeEventSink();
        var parser = new VtParser(sink);

        parser.Feed(0x1B);
        parser.Feed((byte)'A'); // ESC 后跟非 [ 字节 → ESC 键 + 重新处理 A

        Assert.Equal(2, sink.KeyEvents.Count);
        Assert.Equal(ConsoleKey.Escape, sink.KeyEvents[0].key);
        Assert.Equal('A', sink.KeyEvents[1].ch);
    }

    // ---------- CSI 方向键 ----------

    [Fact]
    public void CSI_A_dispatches_up_arrow()
    {
        var sink = new FakeEventSink();
        var parser = new VtParser(sink);

        FeedString(parser, "\x1b[A");

        Assert.Single(sink.KeyEvents);
        Assert.Equal(ConsoleKey.UpArrow, sink.KeyEvents[0].key);
    }

    [Fact]
    public void CSI_B_dispatches_down_arrow()
    {
        var sink = new FakeEventSink();
        var parser = new VtParser(sink);

        FeedString(parser, "\x1b[B");

        Assert.Single(sink.KeyEvents);
        Assert.Equal(ConsoleKey.DownArrow, sink.KeyEvents[0].key);
    }

    [Fact]
    public void CSI_C_dispatches_right_arrow()
    {
        var sink = new FakeEventSink();
        var parser = new VtParser(sink);

        FeedString(parser, "\x1b[C");

        Assert.Single(sink.KeyEvents);
        Assert.Equal(ConsoleKey.RightArrow, sink.KeyEvents[0].key);
    }

    [Fact]
    public void CSI_D_dispatches_left_arrow()
    {
        var sink = new FakeEventSink();
        var parser = new VtParser(sink);

        FeedString(parser, "\x1b[D");

        Assert.Single(sink.KeyEvents);
        Assert.Equal(ConsoleKey.LeftArrow, sink.KeyEvents[0].key);
    }

    [Fact]
    public void CSI_H_dispatches_home()
    {
        var sink = new FakeEventSink();
        var parser = new VtParser(sink);

        FeedString(parser, "\x1b[H");

        Assert.Single(sink.KeyEvents);
        Assert.Equal(ConsoleKey.Home, sink.KeyEvents[0].key);
    }

    [Fact]
    public void CSI_F_dispatches_end()
    {
        var sink = new FakeEventSink();
        var parser = new VtParser(sink);

        FeedString(parser, "\x1b[F");

        Assert.Single(sink.KeyEvents);
        Assert.Equal(ConsoleKey.End, sink.KeyEvents[0].key);
    }

    // ---------- CSI~ PgUp/PgDn ----------

    [Fact]
    public void CSI_5_tilde_dispatches_page_up()
    {
        var sink = new FakeEventSink();
        var parser = new VtParser(sink);

        FeedString(parser, "\x1b[5~");

        Assert.Single(sink.KeyEvents);
        Assert.Equal(ConsoleKey.PageUp, sink.KeyEvents[0].key);
    }

    [Fact]
    public void CSI_6_tilde_dispatches_page_down()
    {
        var sink = new FakeEventSink();
        var parser = new VtParser(sink);

        FeedString(parser, "\x1b[6~");

        Assert.Single(sink.KeyEvents);
        Assert.Equal(ConsoleKey.PageDown, sink.KeyEvents[0].key);
    }

    // ---------- SGR Mouse ----------

    [Fact]
    public void SGR_mouse_press_dispatches_mouse_event_with_isPress_true()
    {
        var sink = new FakeEventSink();
        var parser = new VtParser(sink);

        // ESC[<0;10;5M = 左键按下，col=10，row=5
        FeedString(parser, "\x1b[<0;10;5M");

        Assert.Single(sink.MouseEvents);
        Assert.Equal(4, sink.MouseEvents[0].row); // 1-based → 0-based
        Assert.Equal(9, sink.MouseEvents[0].col); // 1-based → 0-based
        Assert.Equal(0, sink.MouseEvents[0].buttonCode);
        Assert.True(sink.MouseEvents[0].isPress);
    }

    [Fact]
    public void SGR_mouse_release_dispatches_mouse_event_with_isPress_false()
    {
        var sink = new FakeEventSink();
        var parser = new VtParser(sink);

        // ESC[<0;10;5m = 释放
        FeedString(parser, "\x1b[<0;10;5m");

        Assert.Single(sink.MouseEvents);
        Assert.False(sink.MouseEvents[0].isPress);
    }

    [Fact]
    public void SGR_mouse_wheel_up_cb64_dispatches_buttonCode_64()
    {
        var sink = new FakeEventSink();
        var parser = new VtParser(sink);

        // ESC[<64;1;1M = 滚轮上
        FeedString(parser, "\x1b[<64;1;1M");

        Assert.Single(sink.MouseEvents);
        Assert.Equal(64, sink.MouseEvents[0].buttonCode);
        Assert.True(sink.MouseEvents[0].isPress);
    }

    [Fact]
    public void SGR_mouse_wheel_down_cb65_dispatches_buttonCode_65()
    {
        var sink = new FakeEventSink();
        var parser = new VtParser(sink);

        FeedString(parser, "\x1b[<65;1;1M");

        Assert.Single(sink.MouseEvents);
        Assert.Equal(65, sink.MouseEvents[0].buttonCode);
    }

    [Fact]
    public void SGR_mouse_invalid_params_no_dispatch()
    {
        var sink = new FakeEventSink();
        var parser = new VtParser(sink);

        // 无效参数（非数字）
        FeedString(parser, "\x1b[<abc;1;1M");

        Assert.Empty(sink.MouseEvents);
    }

    // ---------- 多字节序列连续 Feed ----------

    [Fact]
    public void Multiple_CSI_sequences_dispatch_independently()
    {
        var sink = new FakeEventSink();
        var parser = new VtParser(sink);

        FeedString(parser, "\x1b[A\x1b[B\x1b[C");

        Assert.Equal(3, sink.KeyEvents.Count);
        Assert.Equal(ConsoleKey.UpArrow, sink.KeyEvents[0].key);
        Assert.Equal(ConsoleKey.DownArrow, sink.KeyEvents[1].key);
        Assert.Equal(ConsoleKey.RightArrow, sink.KeyEvents[2].key);
    }

    [Fact]
    public void CSI_sequence_followed_by_ascii_dispatches_both()
    {
        var sink = new FakeEventSink();
        var parser = new VtParser(sink);

        FeedString(parser, "\x1b[AX");

        Assert.Equal(2, sink.KeyEvents.Count);
        Assert.Equal(ConsoleKey.UpArrow, sink.KeyEvents[0].key);
        Assert.Equal('X', sink.KeyEvents[1].ch);
    }

    // ---------- DA1 响应忽略 ----------

    [Fact]
    public void CSI_c_DA1_response_ignored_no_dispatch()
    {
        var sink = new FakeEventSink();
        var parser = new VtParser(sink);

        // ESC[?6c = DA1 响应
        FeedString(parser, "\x1b[?6c");

        Assert.Empty(sink.KeyEvents);
        Assert.Empty(sink.MouseEvents);
    }

    // ---------- Helpers ----------

    private static void FeedString(VtParser parser, string s)
    {
        foreach (char c in s)
            parser.Feed((byte)c);
    }

    private sealed class FakeEventSink : IVtEventSink
    {
        public readonly List<(ConsoleKey key, char ch)> KeyEvents = new();
        public readonly List<(int row, int col, int buttonCode, bool isPress)> MouseEvents = new();

        public void OnKeyEvent(ConsoleKey key, char ch)
            => KeyEvents.Add((key, ch));

        public void OnMouseEvent(int row, int col, int buttonCode, bool isPress)
            => MouseEvents.Add((row, col, buttonCode, isPress));
    }
}
