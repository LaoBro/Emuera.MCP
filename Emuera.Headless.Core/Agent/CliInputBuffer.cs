using System;
using System.Text;

namespace MinorShift.Emuera.GameView;

/// <summary>
/// CLI 键盘输入行缓冲：accumulate 字符、退格/ESC 编辑、回车提交与行擦除。
/// 从 AgentCliProtocol 拆分以隔离输入行状态（_buf）与编辑逻辑（ProcessChar / Clear / EraseInputLine）。
/// 依赖经构造注入：提交回调 <paramref name="dispatch"/>、输入回显回调 <paramref name="echo"/>——
/// 不含对 EmueraConsole / 渲染层的直接依赖。按钮优先分支（ProcessKey）留在协调方，避免循环依赖。
/// </summary>
internal sealed class CliInputBuffer
{
    private readonly StringBuilder _buf = new();

    /// <summary>提交整行输入（由 AgentCliProtocol.DispatchInput 注入）。</summary>
    private readonly Action<string> _dispatch;

    /// <summary>输入回显（由 AgentCliProtocol.Echo 注入，写 VT 备用屏）。</summary>
    private readonly Action<string> _echo;

    internal CliInputBuffer(Action<string> dispatch, Action<string> echo)
    {
        _dispatch = dispatch;
        _echo = echo;
    }

    /// <summary>单个字符处理：回车提交整行、退格删除、ESC 清空、可打印字符追加并回显。</summary>
    internal void ProcessChar(char ch)
    {
        if (ch == '\r' || ch == '\n')
        {
            EraseInputLine();
            string input = _buf.ToString();
            _buf.Clear();
            _dispatch(input);
        }
        else if (ch == '\b')
        {
            if (_buf.Length > 0)
            {
                _buf.Remove(_buf.Length - 1, 1);
                _echo("\b \b");
            }
        }
        else if (ch == 27)
        {
            EraseInputLine();
            _buf.Clear();
        }
        else if (!char.IsControl(ch))
        {
            _buf.Append(ch);
            _echo(ch.ToString());
        }
    }

    /// <summary>清空输入行（含终端上的显示）。</summary>
    internal void Clear()
    {
        if (_buf.Length == 0) return;
        EraseInputLine();
        _buf.Clear();
    }

    /// <summary>擦除终端上当前输入行的显示内容（不含缓冲区清除）。</summary>
    private void EraseInputLine()
    {
        _echo("\r" + new string(' ', _buf.Length) + "\r");
    }
}