using System;
using System.Collections.Generic;
using MinorShift.Emuera.Runtime;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.UI.Game;
using trsl = MinorShift.Emuera.Runtime.Utils.EvilMask.Lang.SystemLine;

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

        /// <summary>
        /// HEADLESS 下标记需要从终端擦除的行数。
        /// 由 deleteLine() 设置，由 AgentCliProtocol.EraseTerminalRows() 消费。
        /// 当被删行已刷新到终端时递增；当被删行仍在 _agentBuffer 中时直接从缓冲区移除。
        /// </summary>
        internal int _pendingEraseRows;

        /// <summary>
        /// 当前 TINPUT 请求是否带 DisplayTime（倒计时显示）。
        /// </summary>
        internal bool IsDisplayTimeActive =>
            state == ConsoleState.WaitInput && inputReq != null && inputReq.DisplayTime && inputReq.Timelimit > 0;

        /// <summary>
        /// 当前 TINPUT 请求的 TimeUpMes，无请求时返回 null。
        /// </summary>
        internal string? TimeUpMessage => inputReq?.TimeUpMes;

        /// <summary>
        /// 构建倒计时显示文本，格式与 WinForms presetTimer/tickTimer 一致。
        /// </summary>
        internal string BuildCountdownText()
        {
            if (inputReq == null) return "";
            var remainingMs = inputReq.Timelimit - _genericTimerStopwatch.ElapsedMilliseconds;
            return trsl.Remaining.Text + $"{remainingMs / 1000.0f:0.0}";
        }

        private void WriteAlignedLine(ConsoleDisplayLine line)
        {
            string text = line.ToString();
            if (string.IsNullOrEmpty(text))
            {
                if (line.IsLineEnd)
                    WriteToAgentBuffer("");
                else
                    WriteToAgentBufferNoNewline("");
                return;
            }

            // 宽度计算基于原始文本（与游戏内部一致）
            int textWidth = GetDisplayWidth(text);
            int gameWidth = GetGameColumnWidth();

            string output;
            switch (line.Align)
            {
                case DisplayLineAlignment.CENTER:
                    {
                        int pad = Math.Max((gameWidth - textWidth) / 2, 0);
                        output = new string(' ', pad) + text;
                        break;
                    }
                case DisplayLineAlignment.RIGHT:
                    {
                        int pad = Math.Max(gameWidth - textWidth, 0);
                        output = new string(' ', pad) + text;
                        break;
                    }
                default:
                    output = text;
                    break;
            }

            // 输出前替换终端不兼容字符（░▒▓ → 半角等价字符）
            output = TerminalDisplayWidth.ReplaceForTerminal(output);

            if (line.IsLineEnd)
                WriteToAgentBuffer(output);
            else
                WriteToAgentBufferNoNewline(output);
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

            // 宽度计算基于原始文本
            int textWidth = GetDisplayWidth(text);
            int gameWidth = GetGameColumnWidth();

            string output;
            switch (line.Align)
            {
                case DisplayLineAlignment.CENTER:
                    {
                        int pad = Math.Max((gameWidth - textWidth) / 2, 0);
                        output = new string(' ', pad) + text;
                        break;
                    }
                case DisplayLineAlignment.RIGHT:
                    {
                        int pad = Math.Max(gameWidth - textWidth, 0);
                        output = new string(' ', pad) + text;
                        break;
                    }
                default:
                    output = text;
                    break;
            }

            // 输出前替换终端不兼容字符
            return TerminalDisplayWidth.ReplaceForTerminal(output);
        }

        /// <summary>
        /// 收集当前轮次有效的按钮列表（Generation == LastButtonGeneration）。
        /// 供 CLI 按钮选择模式使用。
        /// </summary>
        internal List<ConsoleButtonString> CollectCurrentButtons()
        {
            var result = new List<ConsoleButtonString>();
            var lines = displayLineList;
            if (lines == null || lines.Count == 0)
                return result;

            long currentGen = LastButtonGeneration;
            foreach (var line in lines)
            {
                if (line?.Buttons == null)
                    continue;
                foreach (var btn in line.Buttons)
                {
                    if (btn == null || !btn.IsButton)
                        continue;
                    if (btn.Generation != currentGen)
                        continue;
                    result.Add(btn);
                }
            }
            return result;
        }

        private static int GetDisplayWidth(string str) => TerminalDisplayWidth.GetDisplayWidth(str);

        /// <summary>
        /// 根据游戏配置的像素宽度计算终端字符列数。
        /// 游戏窗口宽度由配置文件决定（WindowX/DrawableWidth），与终端宽度无关。
        /// </summary>
        internal static int GetGameColumnWidth()
        {
            // DrawableWidth 是像素宽度，半角字符宽度 = FontSize / 2 像素
            // 所以字符列数 = DrawableWidth / (FontSize / 2)
            int charWidth = Math.Max(Config.FontSize / 2, 1);
            return Config.DrawableWidth / charWidth;
        }
    }
}
