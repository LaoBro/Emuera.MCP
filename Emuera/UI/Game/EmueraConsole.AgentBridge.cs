using System;
using MinorShift.Emuera.Runtime;
using MinorShift.Emuera.UI.Game;

namespace MinorShift.Emuera.GameView
{
    internal sealed partial class EmueraConsole
    {
        internal ConsoleState State => state;
        internal InputRequest CurrentRequest => inputReq;

        /// <summary>
        /// HEADLESS 下标记终端需要全量刷新（清屏 + 重绘 displayLineList）。
        /// 由 ClearDisplay() 等设置，由 AgentCliProtocol 轮询检查并消费。
        /// </summary>
        internal bool _needFullRefresh;
 
#if HEADLESS
        private void WriteAlignedLine(ConsoleDisplayLine line)
        {
            string text = line.ToString();
            if (string.IsNullOrEmpty(text))
            {
                WriteToAgentBuffer("");
                return;
            }

            int textWidth = GetDisplayWidth(text);
            int consoleWidth;
            try { consoleWidth = Console.WindowWidth; }
            catch { consoleWidth = 80; }

            string output;
            switch (line.Align)
            {
                case DisplayLineAlignment.CENTER:
                    {
                        int pad = Math.Max((consoleWidth - textWidth) / 2, 0);
                        output = new string(' ', pad) + text;
                        break;
                    }
                case DisplayLineAlignment.RIGHT:
                    {
                        int pad = Math.Max(consoleWidth - textWidth, 0);
                        output = new string(' ', pad) + text;
                        break;
                    }
                default:
                    output = text;
                    break;
            }

            WriteToAgentBuffer(output);
        }

        private void WriteToAgentBuffer(string text)
        {
            _agentBuffer.AppendLine(text);
        }

        /// <summary>
        /// 将 ConsoleDisplayLine 格式化为终端对齐文本，复用 WriteAlignedLine 的对齐逻辑。
        /// 供 AgentCliProtocol 全量刷新时使用。
        /// </summary>
        internal string FormatLineForTerminal(ConsoleDisplayLine line)
        {
            string text = line.ToString();
            if (string.IsNullOrEmpty(text))
                return "";

            int textWidth = GetDisplayWidth(text);
            int consoleWidth;
            try { consoleWidth = Console.WindowWidth; }
            catch { consoleWidth = 80; }

            switch (line.Align)
            {
                case DisplayLineAlignment.CENTER:
                    {
                        int pad = Math.Max((consoleWidth - textWidth) / 2, 0);
                        return new string(' ', pad) + text;
                    }
                case DisplayLineAlignment.RIGHT:
                    {
                        int pad = Math.Max(consoleWidth - textWidth, 0);
                        return new string(' ', pad) + text;
                    }
                default:
                    return text;
            }
        }
#else
        private void WriteAlignedLine(ConsoleDisplayLine line)
        {
            // WinForms 模式不采集 agent buffer。
        }
#endif
 
        /// <summary>
        /// 计算字符串在终端中的显示宽度（中日韩字符占2列，其余占1列）
        /// </summary>
        private static int GetDisplayWidth(string str)
        {
            int width = 0;
            foreach (char c in str)
            {
                width += IsWideChar(c) ? 2 : 1;
            }
            return width;
        }
 
        private static bool IsWideChar(char c)
        {
            // CJK Unified Ideographs + Hiragana + Katakana + Fullwidth forms
            return (c >= 0x2E80 && c <= 0x9FFF)
                || (c >= 0xAC00 && c <= 0xD7AF)   // Hangul
                || (c >= 0xF900 && c <= 0xFAFF)   // CJK Compatibility
                || (c >= 0xFF01 && c <= 0xFF60)    // Fullwidth forms
                || (c >= 0xFFE0 && c <= 0xFFE6);
        }
    }
}
