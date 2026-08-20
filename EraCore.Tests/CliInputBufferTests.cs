using System.Collections.Generic;
using MinorShift.Emuera.GameView;
using Xunit;

namespace Emuera.Headless.Tests;

public class CliInputBufferTests
{
    // 收集回调命中的 spy，用于断言 dispatch / echo 行为。
    private sealed class Spy
    {
        public readonly List<string> Dispatched = [];
        public readonly List<string> Echoed = [];
    }

    /// <summary>整行擦除回显：回车/退格/ESC 前按缓冲区长度输出 "\\r + 空格 + \\r"。</summary>
    private static string EraseEcho(int bufLength) => "\r" + new string(' ', bufLength) + "\r";

    private static (CliInputBuffer buffer, Spy spy) NewBuffer()
    {
        var spy = new Spy();
        var buffer = new CliInputBuffer(
            s => spy.Dispatched.Add(s),
            s => spy.Echoed.Add(s));
        return (buffer, spy);
    }

    [Fact]
    public void ProcessChar_accumulates_printable_chars_and_echoes_each()
    {
        var (buffer, spy) = NewBuffer();

        buffer.ProcessChar('a');
        buffer.ProcessChar('b');
        buffer.ProcessChar('c');

        Assert.Equal(["a", "b", "c"], spy.Echoed);
        Assert.Empty(spy.Dispatched);
    }

    [Fact]
    public void ProcessChar_enter_dispatches_full_line_and_clears_buffer()
    {
        var (buffer, spy) = NewBuffer();

        buffer.ProcessChar('h');
        buffer.ProcessChar('i');
        buffer.ProcessChar('\r');

        // 提交前擦除整行（长度=2），随后 dispatch "hi"
        Assert.Equal(["h", "i", EraseEcho(2)], spy.Echoed);
        Assert.Equal(["hi"], spy.Dispatched);

        // 提交后缓冲区已清空，再次输入不会带出旧内容
        buffer.ProcessChar('x');
        buffer.ProcessChar('\n');
        Assert.Equal(["hi", "x"], spy.Dispatched);
        Assert.Equal(["h", "i", EraseEcho(2), "x", EraseEcho(1)], spy.Echoed);
    }

    [Fact]
    public void ProcessChar_backspace_removes_last_char_and_erases_display()
    {
        var (buffer, spy) = NewBuffer();

        buffer.ProcessChar('x');
        buffer.ProcessChar('y');
        buffer.ProcessChar('\b');
        buffer.ProcessChar('\r');

        // 退格只擦一个字符 → [x]；回车擦整行（长度=1）后提交 "x"
        Assert.Equal(["x", "y", "\b \b", EraseEcho(1)], spy.Echoed);
        Assert.Equal(["x"], spy.Dispatched);
    }

    [Fact]
    public void ProcessChar_escape_clears_buffer_without_leak()
    {
        var (buffer, spy) = NewBuffer();

        buffer.ProcessChar('a');
        buffer.ProcessChar('b');
        buffer.ProcessChar((char)27);
        buffer.ProcessChar('\r');

        // ESC 擦整行后置空；回车提交空串，不带出被清空的内容
        Assert.Equal(["a", "b", EraseEcho(2), EraseEcho(0)], spy.Echoed);
        Assert.Equal([""], spy.Dispatched);
    }

    [Fact]
    public void ProcessChar_control_chars_are_ignored()
    {
        var (buffer, spy) = NewBuffer();

        buffer.ProcessChar('\0');
        buffer.ProcessChar('\x01');
        buffer.ProcessChar('a');
        buffer.ProcessChar('\r');

        Assert.Equal(["a", EraseEcho(1)], spy.Echoed);
        Assert.Equal(["a"], spy.Dispatched);
    }

    [Fact]
    public void Clear_erases_and_empties_buffer_without_dispatch()
    {
        var (buffer, spy) = NewBuffer();

        buffer.ProcessChar('z');
        buffer.Clear();
        buffer.ProcessChar('\r');

        Assert.Equal([""], spy.Dispatched);
    }

    [Fact]
    public void Clear_on_empty_buffer_is_noop()
    {
        var (buffer, spy) = NewBuffer();

        buffer.Clear();

        Assert.Empty(spy.Echoed);
        Assert.Empty(spy.Dispatched);
    }
}