using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;
using MinorShift.Emuera.Runtime;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Runtime.Utils.EvilMask;
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

        // ----- IConsoleStateView 实现：封装"读后清零"消费语义 -----

        /// <summary>
        /// 消费"需要全量刷新"标记。返回 true 表示需要刷新，并自动清零。
        /// </summary>
        public bool ConsumeNeedFullRefresh()
        {
            bool v = _needFullRefresh;
            _needFullRefresh = false;
            return v;
        }

        /// <summary>
        /// 消费"待擦除行数"。返回当前待擦除行数，并自动清零计数。
        /// </summary>
        public int ConsumePendingEraseRows()
        {
            int rows = _pendingEraseRows;
            _pendingEraseRows = 0;
            return rows;
        }

        /// <summary>
        /// 追加文本到 agent 缓冲区（不追踪行数，用于输入回显等非显示行）。
        /// </summary>
        public void AppendToAgentBuffer(string text, bool newLine)
        {
            if (newLine)
                _agentBuffer.AppendLine(text);
            else
                _agentBuffer.Append(text);
        }

        /// <summary>
        /// 当前终端的字符宽度配置，由 Program.cs 在启动时探测/设置。
        /// 供 FormatLineForTerminal / WriteAlignedLine 在输出时补偿终端渲染差异。
        /// </summary>
        internal TerminalCharWidthConfig CharWidthConfig = TerminalCharWidthConfig.Default;

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

        /// <summary>
        /// 将 ConsoleDisplayLine 逐节点构建为终端友好的纯文本，同时计算正确的显示宽度。
        /// 非文本节点按降级规则处理：图片/矩形→跳过，Space→空格，Div→递归子行。
        /// </summary>
        private static string BuildTerminalLine(ConsoleDisplayLine line, out int displayWidth)
        {
            var sb = new StringBuilder();
            int width = 0;
            int charWidth = Math.Max(Config.FontSize / 2, 1);

            foreach (var button in line.Buttons)
            {
                foreach (var node in button.StrArray)
                {
                    switch (node)
                    {
                        case ConsoleStyledString:
                            string txt = node.Text ?? "";
                            sb.Append(txt);
                            width += GetDisplayWidth(txt);
                            break;
                        case ConsoleSpacePart:
                            // 像素宽度 → 字符数（整数截断，与 GetGameColumnWidth 一致）
                            int spaceCount = Math.Max(node.Width / charWidth, 0);
                            sb.Append(new string(' ', spaceCount));
                            width += spaceCount;
                            break;
                        case ConsoleImagePart:
                        case ConsoleRectangleShapePart:
                            // 终端无法显示，跳过
                            break;
                        case ConsoleDivPart div:
                            // 递归输出子行文本
                            if (div.Children != null)
                            {
                                foreach (var child in div.Children)
                                {
                                    string childText = BuildTerminalLine(child, out int childWidth);
                                    sb.Append(childText);
                                    width += childWidth;
                                }
                            }
                            break;
                        default:
                            // ConsoleErrorShapePart 等，输出原文本
                            string fallback = node.Text ?? "";
                            sb.Append(fallback);
                            width += GetDisplayWidth(fallback);
                            break;
                    }
                }
            }
            displayWidth = width;
            return sb.ToString();
        }

        private void WriteAlignedLine(ConsoleDisplayLine line)
        {
            string text = BuildTerminalLine(line, out int textWidth);
            if (textWidth == 0 && text.Length == 0)
            {
                if (line.IsLineEnd)
                    WriteToAgentBuffer("");
                else
                    WriteToAgentBufferNoNewline("");
                return;
            }

            int gameWidth = GetGameColumnWidth();

            // 带 ANSI 样式的文本（若终端不支持 ANSI 则回退到纯文本）
            string styledText = IsAnsiEnabled() ? FormatLineWithAnsi(line) : text;

            string output;
            switch (line.Align)
            {
                case DisplayLineAlignment.CENTER:
                    {
                        int pad = Math.Max((gameWidth - textWidth) / 2, 0);
                        output = new string(' ', pad) + styledText;
                        break;
                    }
                case DisplayLineAlignment.RIGHT:
                    {
                        int pad = Math.Max(gameWidth - textWidth, 0);
                        output = new string(' ', pad) + styledText;
                        break;
                    }
                default:
                    output = styledText;
                    break;
            }

            // 输出前替换终端不兼容字符（░▒▓ → 半角等价字符）
            output = TerminalDisplayWidth.ReplaceForTerminal(output, CharWidthConfig);

            if (line.IsLineEnd)
                WriteToAgentBuffer(output);
            else
                WriteToAgentBufferNoNewline(output);
        }

        /// <summary>
        /// 将 ConsoleDisplayLine 格式化为带 ANSI 转义序列的终端文本。
        /// 逐段提取 ConsoleStyledString 的颜色与字体样式，生成对应的 ANSI 转义码。
        /// 非文本节点按降级规则处理：图片/矩形→跳过，Space→空格，Div→递归子行。
        /// 宽度计算与对齐由调用方基于纯文本完成，本方法仅负责样式输出。
        /// </summary>
        private string FormatLineWithAnsi(ConsoleDisplayLine line)
        {
            var sb = new StringBuilder();
            Color? lastColor = null;
            FontStyle lastFontStyle = FontStyle.Regular;
            int charWidth = Math.Max(Config.FontSize / 2, 1);

            foreach (var button in line.Buttons)
            {
                bool isSelected = ButtonIsSelected(button);
                if (isSelected) sb.Append("\x1b[7m");

                foreach (var node in button.StrArray)
                {
                    switch (node)
                    {
                        case ConsoleStyledString css:
                            {
                                var style = css.StringStyle;
                                // 仅在样式发生变化时输出 ANSI 转义，减少冗余
                                if (lastColor != style.Color || lastFontStyle != style.FontStyle)
                                {
                                    // 若之前已有样式，先重置
                                    if (lastColor != null || lastFontStyle != FontStyle.Regular)
                                    {
                                        sb.Append("\x1b[0m");
                                        // 反色会被 \x1b[0m 取消，需重新追加
                                        if (isSelected) sb.Append("\x1b[7m");
                                    }

                                    // 前景色: \x1b[38;2;R;G;Bm（真彩色）
                                    sb.Append($"\x1b[38;2;{style.Color.R};{style.Color.G};{style.Color.B}m");

                                    // 粗体: \x1b[1m
                                    if ((style.FontStyle & FontStyle.Bold) != 0)
                                        sb.Append("\x1b[1m");
                                    // 斜体: \x1b[3m
                                    if ((style.FontStyle & FontStyle.Italic) != 0)
                                        sb.Append("\x1b[3m");

                                    lastColor = style.Color;
                                    lastFontStyle = style.FontStyle;
                                }
                                sb.Append(node.Text ?? "");
                            }
                            break;
                        case ConsoleSpacePart:
                            int spaceCount = Math.Max(node.Width / charWidth, 0);
                            sb.Append(new string(' ', spaceCount));
                            break;
                        case ConsoleImagePart:
                        case ConsoleRectangleShapePart:
                            // 终端无法显示，跳过
                            break;
                        case ConsoleDivPart div:
                            // 递归输出子行（带 ANSI 样式）
                            if (div.Children != null)
                            {
                                foreach (var child in div.Children)
                                {
                                    string childStyled = FormatLineWithAnsi(child);
                                    sb.Append(childStyled);
                                }
                            }
                            break;
                        default:
                            // ConsoleErrorShapePart 等，输出原文本
                            sb.Append(node.Text ?? "");
                            break;
                    }
                }

                // 取消反色，防止泄漏到下一个按钮
                if (isSelected) sb.Append("\x1b[27m");
            }

            // 行尾重置样式，防止泄漏到下一行
            if (lastColor != null || lastFontStyle != FontStyle.Regular)
                sb.Append("\x1b[0m");

            return sb.ToString();
        }

        /// <summary>
        /// 将 ConsoleDisplayLine 格式化为终端对齐文本，复用 WriteAlignedLine 的对齐逻辑。
        /// 供 AgentCliProtocol 全量刷新时使用。
        /// </summary>
        internal string FormatLineForTerminal(ConsoleDisplayLine line)
        {
            string text = BuildTerminalLine(line, out int textWidth);
            if (textWidth == 0 && text.Length == 0)
                return "";

            int gameWidth = GetGameColumnWidth();

            // 带 ANSI 样式的文本（若终端不支持 ANSI 则回退到纯文本）
            string styledText = IsAnsiEnabled() ? FormatLineWithAnsi(line) : text;

            string output;
            switch (line.Align)
            {
                case DisplayLineAlignment.CENTER:
                    {
                        int pad = Math.Max((gameWidth - textWidth) / 2, 0);
                        output = new string(' ', pad) + styledText;
                        break;
                    }
                case DisplayLineAlignment.RIGHT:
                    {
                        int pad = Math.Max(gameWidth - textWidth, 0);
                        output = new string(' ', pad) + styledText;
                        break;
                    }
                default:
                    output = styledText;
                    break;
            }

            // 输出前替换终端不兼容字符
            return TerminalDisplayWidth.ReplaceForTerminal(output, CharWidthConfig);
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

        /// <summary>
        /// 设置当前选中按钮，供 headless 按钮导航模式驱动高亮显示。
        /// </summary>
        internal void SetSelectingButton(ConsoleButtonString button)
        {
            selectingButton = button;
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

        /// <summary>
        /// 判断当前终端是否支持 ANSI 转义序列。
        /// 与 AgentCliProtocol 中的判定逻辑一致：Program.AnsiEnabled 或非 Windows 平台。
        /// </summary>
        private static bool IsAnsiEnabled() => Program.AnsiEnabled || !OperatingSystem.IsWindows();

    }
}
