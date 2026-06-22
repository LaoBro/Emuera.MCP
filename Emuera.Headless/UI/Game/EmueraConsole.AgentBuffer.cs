using System.Text;

namespace MinorShift.Emuera.GameView;

internal sealed partial class EmueraConsole
{
    internal readonly StringBuilder _agentBuffer = new();

    /// <summary>
    /// _agentBuffer 中尚未刷新到终端的行数。
    /// 由 WriteToAgentBuffer() 递增，由 TakeAgentBuffer() 重置，由 RemoveLastLineFromAgentBuffer() 递减。
    /// </summary>
    internal int _agentBufferLineCount;

    internal void WriteToAgentBuffer(string text)
    {
        _agentBuffer.AppendLine(text);
        _agentBufferLineCount++;
    }

    /// <summary>
    /// 不换行写入（对应 IsLineEnd=false 的行，如 PRINT 不换行）。
    /// 后续行合并时通过 RemoveLastLineFromAgentBuffer() 移除并重写。
    /// </summary>
    internal void WriteToAgentBufferNoNewline(string text)
    {
        _agentBuffer.Append(text);
        _agentBufferLineCount++;
    }

    internal string TakeAgentBuffer()
    {
        var text = _agentBuffer.ToString();
        _agentBuffer.Clear();
        _agentBufferLineCount = 0;
        return text;
    }

    /// <summary>
    /// 从 _agentBuffer 中移除最后一行（对应 CLEARLINE 删除的行仍在缓冲区中的情况）。
    /// 返回 true 表示成功移除，false 表示缓冲区为空。
    /// </summary>
    internal bool RemoveLastLineFromAgentBuffer()
    {
        if (_agentBufferLineCount <= 0) return false;

        string content = _agentBuffer.ToString();

        if (content.Length <= 1)
        {
            // 空缓冲区或仅含单字符（单行），直接清空
            _agentBuffer.Clear();
            _agentBufferLineCount--;
            return true;
        }

        // 找到最后一个换行符的位置（去掉末尾换行后找最后一个换行）
        int lastNewline = content.LastIndexOf('\n', content.Length - 2, content.Length - 1);
        if (lastNewline < 0)
        {
            // 缓冲区只有一行
            _agentBuffer.Clear();
        }
        else
        {
            _agentBuffer.Remove(lastNewline, _agentBuffer.Length - lastNewline);
        }
        _agentBufferLineCount--;
        return true;
    }
}
