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
