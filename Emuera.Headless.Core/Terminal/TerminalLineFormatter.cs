using System;
using System.Text;
using MinorShift.Emuera.Primitives;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Runtime.Utils.EvilMask;
using MinorShift.Emuera.UI.Game;

namespace MinorShift.Emuera.GameView;

internal static class TerminalLineFormatter
{
    internal static string FormatLineForTerminal(
        ConsoleDisplayLine line,
        ConsoleButtonString? selectingButton,
        TerminalCharWidthConfig charWidthConfig,
        bool ansiEnabled)
    {
        string text = BuildTerminalLine(line, out int textWidth);
        if (textWidth == 0 && text.Length == 0)
            return "";
        int gameWidth = GetGameColumnWidth();
        string styledText = ansiEnabled ? FormatLineWithAnsi(line, selectingButton) : text;
        string output;
        switch (line.Align)
        {
            case DisplayLineAlignment.CENTER:
                int padC = Math.Max((gameWidth - textWidth) / 2, 0);
                output = new string(' ', padC) + styledText;
                break;
            case DisplayLineAlignment.RIGHT:
                int padR = Math.Max(gameWidth - textWidth, 0);
                output = new string(' ', padR) + styledText;
                break;
            default:
                output = styledText;
                break;
        }
        return TerminalDisplayWidth.ReplaceForTerminal(output, charWidthConfig);
    }

    internal static string BuildTerminalLine(ConsoleDisplayLine line, out int displayWidth)
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
                        width += TerminalDisplayWidth.GetDisplayWidth(txt);
                        break;
                    case ConsoleSpacePart:
                        int spaceCount = Math.Max(node.Width / charWidth, 0);
                        sb.Append(new string(' ', spaceCount));
                        width += spaceCount;
                        break;
                    case ConsoleImagePart:
                    case ConsoleRectangleShapePart:
                        // 图片/矩形按流宽度补空格占位（与 ConsoleSpacePart 同公式），
                        // 保持行内文字/按钮列位置与 winforms 布局一致（issue 2026-08-07）。
                        int visualSpaces = Math.Max(node.Width / charWidth, 0);
                        sb.Append(new string(' ', visualSpaces));
                        width += visualSpaces;
                        break;
                    case ConsoleDivPart div:
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
                        string fallback = node.Text ?? "";
                        sb.Append(fallback);
                        width += TerminalDisplayWidth.GetDisplayWidth(fallback);
                        break;
                }
            }
        }
        displayWidth = width;
        return sb.ToString();
    }

    internal static string FormatLineWithAnsi(ConsoleDisplayLine line, ConsoleButtonString? selectingButton)
    {
        var sb = new StringBuilder();
        EmuColor? lastColor = null;
        EmuFontStyle lastFontStyle = EmuFontStyle.Regular;
        int charWidth = Math.Max(Config.FontSize / 2, 1);
        foreach (var button in line.Buttons)
        {
            bool isSelected = selectingButton == button;
            if (isSelected) sb.Append("\x1b[7m");
            foreach (var node in button.StrArray)
            {
                switch (node)
                {
                    case ConsoleStyledString css:
                        var style = css.StringStyle;
                        if (lastColor != style.Color || lastFontStyle != style.FontStyle)
                        {
                            if (lastColor != null || lastFontStyle != EmuFontStyle.Regular)
                            {
                                sb.Append("\x1b[0m");
                                if (isSelected) sb.Append("\x1b[7m");
                            }
                            sb.Append($"\x1b[38;2;{style.Color.R};{style.Color.G};{style.Color.B}m");
                            if ((style.FontStyle & EmuFontStyle.Bold) != 0) sb.Append("\x1b[1m");
                            if ((style.FontStyle & EmuFontStyle.Italic) != 0) sb.Append("\x1b[3m");
                            if ((style.FontStyle & EmuFontStyle.Underline) != 0) sb.Append("\x1b[4m");
                            if ((style.FontStyle & EmuFontStyle.Strikeout) != 0) sb.Append("\x1b[9m");
                            lastColor = style.Color;
                            lastFontStyle = style.FontStyle;
                        }
                        sb.Append(node.Text ?? "");
                        break;
                    case ConsoleSpacePart:
                        int spaceCount = Math.Max(node.Width / charWidth, 0);
                        sb.Append(new string(' ', spaceCount));
                        break;
                    case ConsoleImagePart:
                    case ConsoleRectangleShapePart:
                        // 与 BuildTerminalLine 一致：图片/矩形补空格占位（纯空格，无样式）。
                        // 有活动样式时先 reset，避免占位空格继承前段装饰线（4m/9m）；
                        // 同时清空 last 镜像——否则后续样式段与 last 相同会被误判为
                        // "未变化"而不重发样式码，装饰线将静默丢失。
                        if (lastColor != null || lastFontStyle != EmuFontStyle.Regular)
                        {
                            sb.Append("\x1b[0m");
                            if (isSelected) sb.Append("\x1b[7m");
                            lastColor = null;
                            lastFontStyle = EmuFontStyle.Regular;
                        }
                        int visualSpaces = Math.Max(node.Width / charWidth, 0);
                        sb.Append(new string(' ', visualSpaces));
                        break;
                    case ConsoleDivPart div:
                        if (div.Children != null)
                        {
                            foreach (var child in div.Children)
                                sb.Append(FormatLineWithAnsi(child, selectingButton));
                        }
                        break;
                    default:
                        sb.Append(node.Text ?? "");
                        break;
                }
            }
            if (isSelected) sb.Append("\x1b[27m");
        }
        if (lastColor != null || lastFontStyle != EmuFontStyle.Regular)
            sb.Append("\x1b[0m");
        return sb.ToString();
    }

    internal static int GetGameColumnWidth()
    {
        int charWidth = Math.Max(Config.FontSize / 2, 1);
        return Config.DrawableWidth / charWidth;
    }
}
