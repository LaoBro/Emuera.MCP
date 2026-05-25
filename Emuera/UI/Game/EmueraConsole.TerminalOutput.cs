using System;
using MinorShift.Emuera.Runtime;
using MinorShift.Emuera.UI.Game;

namespace MinorShift.Emuera.GameView
{
    internal sealed partial class EmueraConsole
    {
        internal ConsoleState State => state;
        internal InputRequest CurrentRequest => inputReq;
 
        private void WriteAlignedLine(ConsoleDisplayLine line)
        {
            try
            {
                string text = line.ToString();
                if (string.IsNullOrEmpty(text))
                {
                    Console.WriteLine();
                    return;
                }

                int textWidth = GetDisplayWidth(text);
                int consoleWidth = Console.WindowWidth;

                switch (line.Align)
                {
                    case DisplayLineAlignment.CENTER:
                        {
                            int pad = Math.Max((consoleWidth - textWidth) / 2, 0);
                            Console.WriteLine(new string(' ', pad) + text);
                            break;
                        }
                    case DisplayLineAlignment.RIGHT:
                        {
                            int pad = Math.Max(consoleWidth - textWidth, 0);
                            Console.WriteLine(new string(' ', pad) + text);
                            break;
                        }
                    default:
                        Console.WriteLine(text);
                        break;
                }
            }
            catch (System.IO.IOException)
            {
                // 无控制台时静默降级
            }
        }
 
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