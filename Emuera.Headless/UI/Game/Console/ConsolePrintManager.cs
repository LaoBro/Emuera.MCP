using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using MinorShift.Emuera.Runtime;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Runtime.Script.Data;
using MinorShift.Emuera.Runtime.Script.Statements;
using MinorShift.Emuera.Runtime.Utils;
using MinorShift.Emuera.Runtime.Utils.EvilMask;
using static MinorShift.Emuera.Runtime.Utils.EvilMask.Utils;
using MinorShift.Emuera.UI.Game;
using trerror = MinorShift.Emuera.Runtime.Utils.EvilMask.Lang.Error;
using trmb = MinorShift.Emuera.Runtime.Utils.EvilMask.Lang.MessageBox;
using trsl = MinorShift.Emuera.Runtime.Utils.EvilMask.Lang.SystemLine;

namespace MinorShift.Emuera.GameView;

/// <summary>
/// Print, style, line management, log output.
/// Extracted from EmueraConsole.Print.cs.
/// </summary>
internal sealed class ConsolePrintManager
{
    private readonly ConsoleStateData _state;
    private readonly EmueraConsole _console;
    private readonly IConsoleUI _ui;

    private int printCWidth = -1;
    private int printCWidthL = -1;
    private int printCWidthL2 = -1;

    private const string ErrorButtonsText = "__openFileWithDebug__";

    internal ConsolePrintManager(ConsoleStateData state, EmueraConsole console, IConsoleUI ui)
    {
        _state = state;
        _console = console;
        _ui = ui;
    }

    // Computed style property matching original
    private StringStyle Style
    {
        get
        {
            if (!_state.UseUserStyle)
                return _state.defaultStyle;
            if (_state.UseSetColorStyle)
                return _state.userStyle;
            if (_state.userStyle.Color == _state.defaultStyle.Color)
                return _state.userStyle;
            return new StringStyle(_state.defaultStyle.Color, _state.userStyle.FontStyle, _state.userStyle.Fontname);
        }
    }

    private bool Enabled => _ui.Created;

    // --- Line management ---

    public void ClearDisplay()
    {
        _state.CBProc?.ClearScreen();
        _state.displayLineList.Clear();
        _state._htmlElementList.Clear();
        ConsoleEscapedParts.Clear();
        _state.logicalLineCount = 0;
        _state.deletedLines = 0;
        _state.lineNo = 0;
        _state.lastDrawnLineNo = -1;
        _state._needFullRefresh = true;
        _console.VerticalScrollBarUpdate();
        _ui.Refresh();
    }

    public bool LastLineIsTemporary =>
        _state.displayLineList.Count != 0 && _state.displayLineList[^1].IsTemporary;

    public bool LastLineIsEmpty =>
        _state.displayLineList.Count != 0 && string.IsNullOrEmpty(_state.displayLineList[^1].ToString().Trim());

    internal void AddRangeDisplayLine(ConsoleDisplayLine[] lineList)
    {
        for (int i = 0; i < lineList.Length; i++)
            AddDisplayLine(lineList[i], false);
    }

    internal void AddDisplayLine(ConsoleDisplayLine line, bool force_LEFT)
    {
        if (Config.CBUseClipboard)
            _state.CBProc.AddLine(line, force_LEFT);
        if (LastLineIsTemporary)
            DeleteLine(1);

        AConsoleDisplayNode errorStr = null;
        foreach (ConsoleButtonString button in line.Buttons)
        {
            foreach (AConsoleDisplayNode css in button.StrArray)
            {
                if (css.Error)
                {
                    errorStr = css;
                    goto ScanBreak;
                }
            }
            if (Config.TextDrawingMode != TextDrawingMode.WINAPI)
            {
                button.FilterEscaped();
                if (button.EscapedParts != null)
                {
                    foreach (var p in button.EscapedParts)
                    {
                        p.Parent = button;
                        ConsoleEscapedParts.Add(p, _state.lineNo, p.Depth,
                            (int)Math.Ceiling((float)p.Top / Config.LineHeight) + _state.lineNo,
                            (int)Math.Floor((float)Math.Max(0, p.Bottom - 1) / Config.LineHeight) + _state.lineNo);
                    }
                }
            }
        }
    ScanBreak:
        if (errorStr != null)
        {
            Dialog.Show(trmb.IllegalFontError.Text, trmb.IllegalFontError.Text);
            _console.Quit();
            return;
        }
        if (force_LEFT)
            line.SetAlignment(DisplayLineAlignment.LEFT);
        else
            line.SetAlignment(_state.alignment);
        line.LineNo = _state.lineNo;
        line.bitmapCacheEnabled = _state.bitmapCacheEnabledForNextLine;

        if (_state.displayLineList.Count != 0 && !_state.displayLineList[^1].IsLineEnd)
        {
            var lastline = _state.displayLineList[^1];
            DeleteLine(1);
            line.ShiftPositionX(lastline.Buttons[^1].PointX + lastline.Buttons[^1].Width);
            line.ChangeStr([.. lastline.Buttons, .. line.Buttons]);
        }
        _state.displayLineList.Add(line);

        _console.WriteAlignedLine(line);

        _state.lineNo++;
        if (line.IsLogicalLine && _state.displayLineList[^1].IsLineEnd)
            _state.logicalLineCount++;
        if (_state.lineNo == int.MaxValue)
        {
            _state.lastDrawnLineNo = -1;
            _state.lineNo = 0;
        }
        if (_state.logicalLineCount == long.MaxValue)
            _state.logicalLineCount = 0;

        if (_state.displayLineList.Count > Config.MaxLog)
        {
            if (Config.TextDrawingMode != TextDrawingMode.WINAPI)
                ConsoleEscapedParts.RemoveAt(_state.displayLineList[0].LineNo);
            _state.displayLineList.RemoveAt(0);
            _state.deletedLines++;
        }
    }

    public void DeleteLine(int argNum)
    {
        if (Config.CBUseClipboard)
            _state.CBProc.DelLine(Math.Min(argNum, _state.displayLineList.Count));
        int delNum = 0;
        int num = argNum;
        bool deleted = false;
        while (delNum < num)
        {
            if (_state.displayLineList.Count == 0)
                break;
            ConsoleDisplayLine line = _state.displayLineList[^1];
            deleted = true;
            _state.displayLineList.RemoveAt(_state.displayLineList.Count - 1);
            _state.lineNo--;
            if (line.IsLogicalLine)
            {
                delNum++;
                if (line.IsLineEnd)
                    _state.logicalLineCount--;
            }
            if (_state.displayLineList.Count == Config.MaxLog - 1)
                _state.deletedLines++;
            if (_state.displayLineList.Count == Config.MaxLog - 2 && _state.lineNo > _state.displayLineList.Count)
            {
                ConsoleDisplayLine dummyline = BufferToSingleLine(true, false);
                _state.displayLineList.Insert(0, dummyline);
            }
        }
        if (delNum < num)
        {
            _state.lineNo = 0;
            _state.logicalLineCount -= num - delNum;
        }
        if (deleted && Config.TextDrawingMode != TextDrawingMode.WINAPI)
            ConsoleEscapedParts.Remove(_state.lineNo);
        if (_state.lineNo < 0)
            _state.lineNo += int.MaxValue;
        _state.lastDrawnLineNo = -1;
        if (_state.displayLineList.Count == Config.MaxLog)
            _state.displayLineList.RemoveAt(0);
        _state.deletedLines -= num;

        if (!_console.RemoveLastLineFromAgentBuffer())
            _state._pendingEraseRows++;
    }

    public ConsoleDisplayLine BufferToSingleLine(bool force, bool temporary)
    {
        if (!Enabled)
            return null;
        if (!force && _console.printBuffer.IsEmpty)
            return null;
        if (force && _console.printBuffer.IsEmpty)
            _console.printBuffer.Append(" ", Style);
        ConsoleDisplayLine dispLine = _console.printBuffer.FlushSingleLine(_console.stringMeasure, temporary | _state.force_temporary);
        return dispLine;
    }

    public void ClearText() => _ui.ClearRichText();

    // --- Print methods ---

    public void Print(string str, bool lineEnd = true)
    {
        if (string.IsNullOrEmpty(str))
            return;
        var lineEndIndex = str.IndexOf('\n', StringComparison.Ordinal);
        if (lineEndIndex != -1)
        {
            string upper = str[..lineEndIndex];
            _console.printBuffer.Append(upper, Style);
            _console.NewLine();
            if (lineEndIndex < str.Length - 1)
            {
                string lower = str[(lineEndIndex + 1)..];
                Print(lower);
            }
            return;
        }
        _console.printBuffer.Append(str, Style, lineEnd: lineEnd);
    }

    public void PrintSingleLine(string str) => PrintSingleLine(str, false);

    public void PrintSingleLine(string str, bool temporary)
    {
        if (string.IsNullOrEmpty(str))
            return;
        PrintFlush(false);
        _console.printBuffer.Append(str, Style);
        ConsoleDisplayLine dispLine = BufferToSingleLine(true, temporary);
        if (dispLine == null)
            return;
        AddDisplayLine(dispLine, false);
        _console.RefreshStrings(false);
    }

    public void PrintC(string str, bool alignmentRight)
    {
        if (string.IsNullOrEmpty(str))
            return;
        _console.printBuffer.Append(CreateTypeCString(str, alignmentRight), Style, true);
    }

    public void PrintFlush(bool force)
    {
        if (!Enabled)
            return;
        if (!force && _console.printBuffer.IsEmpty)
            return;
        if (force && _console.printBuffer.IsEmpty)
            _console.printBuffer.Append(" ", Style);
        ConsoleDisplayLine[] dispList = _console.printBuffer.Flush(_console.stringMeasure, _state.force_temporary);
        AddRangeDisplayLine(dispList);
    }

    public void PrintSystemLine(string str)
    {
        PrintFlush(false);
        _state.UseUserStyle = false;
        PrintSingleLine(str, false);
    }

    public void PrintError(string str)
    {
        if (string.IsNullOrEmpty(str))
            return;
        if (Program.DebugMode)
        {
            _console.DebugPrint(str);
            _console.DebugNewLine();
        }
        PrintFlush(false);
        _state.UseUserStyle = false;
        ConsoleDisplayLine dispLine = PrintPlainwithSingleLine(str);
        if (dispLine == null)
            return;
        AddDisplayLine(dispLine, true);
        _console.RefreshStrings(false);
    }

    internal void PrintErrorButton(string str, ScriptPosition? pos, int level = 0)
    {
        if (string.IsNullOrEmpty(str))
            return;
        if (Program.DebugMode)
        {
            _console.DebugPrint(str);
            _console.DebugNewLine();
        }
        _state.UseUserStyle = false;
        var errColor = Color.FromArgb(255, 255, 255, 160);
        var errerStyle = Style;
        errerStyle.Color = level switch
        {
            0 => errColor,
            1 => errColor,
            2 => errColor,
            3 => Color.Red,
            _ => Color.Red
        };
        ConsoleDisplayLine dispLine = _console.printBuffer.AppendAndFlushErrButton(str, errerStyle, ErrorButtonsText, pos, _console.stringMeasure);
        if (dispLine == null)
            return;
        AddDisplayLine(dispLine, true);
        _console.RefreshStrings(false);
    }

    public void PrintWarning(string str, ScriptPosition? position, int level)
    {
        if (level < Config.DisplayWarningLevel && !Program.AnalysisMode)
            return;
        bool b = _state.force_temporary;
        _state.force_temporary = false;
        if (position != null)
        {
            if (position.Value.LineNo >= 0)
            {
                PrintErrorButton(string.Format(trerror.Warning1.Text, level, position.Value.Filename, position.Value.LineNo, str), position, level);
                GlobalStatic.Process.printRawLine(position);
            }
            else
                PrintErrorButton(string.Format(trerror.Warning2.Text, level, position.Value.Filename, str), position, level);
        }
        else
            PrintError(string.Format(trerror.Warning3.Text, level, str));
        _state.force_temporary = b;
    }

    public void PrintTemporaryLine(string str) => PrintSingleLine(str, true);

    public void PrintBar()
    {
        StringStyle ss = _state.userStyle;
        _state.userStyle.FontStyle = FontStyle.Regular;
        Print(_state.stBar);
        _state.userStyle = ss;
    }

    public void PrintCustomBar(string barStr, bool isConst)
    {
        if (string.IsNullOrEmpty(barStr))
            throw new CodeEE(trerror.EmptyDrawline.Text);
        StringStyle ss = _state.userStyle;
        _state.userStyle.FontStyle = FontStyle.Regular;
        if (isConst)
            Print(barStr);
        else
            Print(GetStBar(barStr));
        _state.userStyle = ss;
    }

    public void PrintButton(string str, string p)
    {
        if (string.IsNullOrEmpty(str)) return;
        _console.printBuffer.AppendButton(str, Style, p);
    }

    public void PrintButton(string str, long p)
    {
        if (string.IsNullOrEmpty(str)) return;
        _console.printBuffer.AppendButton(str, Style, p);
    }

    public void PrintButtonC(string str, string p, bool isRight)
    {
        if (string.IsNullOrEmpty(str)) return;
        _console.printBuffer.AppendButton(CreateTypeCString(str, isRight), Style, p);
    }

    public void PrintButtonC(string str, long p, bool isRight)
    {
        if (string.IsNullOrEmpty(str)) return;
        _console.printBuffer.AppendButton(CreateTypeCString(str, isRight), Style, p);
    }

    public void PrintPlain(string str)
    {
        if (string.IsNullOrEmpty(str)) return;
        _console.printBuffer.AppendPlainText(str, Style);
    }

    public void PrintHtml(string str, bool toPrintBuffer)
    {
        if (string.IsNullOrEmpty(str) || !Enabled) return;
        if (toPrintBuffer)
        {
            foreach (var button in HtmlManager.Html2ButtonList(str, _console.stringMeasure, _console))
                _console.printBuffer.AppendButton(button);
        }
        else
        {
            if (!_console.printBuffer.IsEmpty)
            {
                ConsoleDisplayLine[] dispList = _console.printBuffer.Flush(_console.stringMeasure, _state.force_temporary);
                AddRangeDisplayLine(dispList);
            }
            AddRangeDisplayLine(HtmlManager.Html2DisplayLine(str, _console.stringMeasure, _console));
        }
        _console.RefreshStrings(false);
    }

    public void PrintHTMLIsland(string html)
    {
        _state._htmlElementList.AddRange(HtmlManager.Html2DisplayLine(html, _console.stringMeasure, _console));
    }

    public void ClearHTMLIsland() => _state._htmlElementList.Clear();

    public void PrintImg(string name, string nameb, string namem, MixedNum height, MixedNum width, MixedNum ypos)
    {
        _console.printBuffer.Append(new ConsoleImagePart(name, nameb, namem, height, width, ypos));
    }

    public void PrintShape(string type, MixedNum[] param)
    {
        ConsoleShapePart part = ConsoleShapePart.CreateShape(type, param, _state.userStyle.Color, _state.userStyle.ButtonColor, false);
        _console.printBuffer.Append(part);
    }

    internal ConsoleDisplayLine PrintPlainwithSingleLine(string str)
    {
        if (!Enabled || string.IsNullOrEmpty(str))
            return null;
        _console.printBuffer.AppendPlainText(str, Style);
        return _console.printBuffer.FlushSingleLine(_console.stringMeasure, false);
    }

    public void PrintPlainWithSingleLineFix(string str)
    {
        ConsoleDisplayLine dispLine = PrintPlainwithSingleLine(str);
        if (dispLine == null) return;
        AddDisplayLine(dispLine, false);
        _console.RefreshStrings(false);
    }

    public ConsoleDisplayLine[] GetDisplayLines(long lineNo)
    {
        if (lineNo < 0 || lineNo > _state.displayLineList.Count)
            return null;
        int count = 0;
        List<ConsoleDisplayLine> list = [];
        for (int i = _state.displayLineList.Count - 1; i >= 0; i--)
        {
            if (count == lineNo)
                list.Insert(0, _state.displayLineList[i]);
            if (_state.displayLineList[i].IsLogicalLine)
                count++;
            if (count > lineNo)
                break;
        }
        if (list.Count == 0)
            return null;
        ConsoleDisplayLine[] ret = new ConsoleDisplayLine[list.Count];
        list.CopyTo(ret);
        return ret;
    }

    public ConsoleDisplayLine[] PopDisplayingLines()
    {
        if (!Enabled || _console.printBuffer.IsEmpty)
            return null;
        return _console.printBuffer.Flush(_console.stringMeasure, _state.force_temporary);
    }

    // --- Bar helpers ---

    public string GetDefStBar() => _state.stBar;

    public string GetStBar(string barStr)
    {
        var builder = new StringBuilder();
        builder.Append(barStr);
        int width = 0;
        Font font = Config.DefaultFont;
        while (width < Config.DrawableWidth)
        {
            builder.Append(barStr);
            width = _console.stringMeasure.GetDisplayLength(builder.ToString(), font);
        }
        while (width > Config.DrawableWidth)
        {
            builder.Remove(builder.Length - 1, 1);
            width = _console.stringMeasure.GetDisplayLength(builder.ToString(), font);
        }
        return builder.ToString();
    }

    public void SetStBar(string barStr) => _state.stBar = GetStBar(barStr);

    // --- Style helpers ---

    public void SetStringStyle(FontStyle fs) => _state.userStyle.FontStyle = fs;

    public void SetStringStyle(Color color)
    {
        _state.userStyle.Color = color;
        _state.userStyle.ColorChanged = color != Config.ForeColor;
    }

    public void SetFont(string fontname)
    {
        if (!string.IsNullOrEmpty(fontname))
            _state.userStyle.Fontname = fontname;
        else
            _state.userStyle.Fontname = Config.FontName;
    }

    public void ResetStyle()
    {
        _state.userStyle = _state.defaultStyle;
        _state.alignment = DisplayLineAlignment.LEFT;
    }

    public void SetBgColor(Color color)
    {
        _state.bgColor = color;
        _state.forceTextBoxColor = true;
        if (_state.redraw == ConsoleRedraw.None && _ui.ScrollBar.Value == _ui.ScrollBar.Maximum)
            return;
        if (_state._drawStopwatch == null)
            _state._drawStopwatch = System.Diagnostics.Stopwatch.StartNew();
        else
        {
            while (_state._drawStopwatch.ElapsedMilliseconds < _state.msPerFrame)
                _ui.ProcessEvents();
        }
        _console.RefreshStrings(true);
        _state._drawStopwatch.Restart();
    }

    // --- Log ---

    private bool OutputLogInternal(string fullpath, bool hideInfo)
    {
        try
        {
            var log = GetLog(hideInfo);
            File.WriteAllText(fullpath, log, EncodingHandler.UTF8BOMEncoding);
        }
        catch (Exception)
        {
            Dialog.Show(trmb.FailedOutputLog.Text, trmb.FailedOutputLogError.Text);
            return false;
        }
        return true;
    }

    public bool OutputLog(string filename, bool hideInfo)
    {
        if (filename == "" || filename == null)
            filename = Program.ExeDir + "emuera.log";
        else
            filename = Program.ExeDir + filename;
        if (filename.Contains("../", StringComparison.Ordinal))
        {
            Dialog.Show(trmb.FailedOutputLog.Text, trmb.CanNotOutputToParentDirectory.Text);
            return false;
        }
        if (!filename.StartsWith(Program.ExeDir, StringComparison.OrdinalIgnoreCase))
        {
            Dialog.Show(trmb.FailedOutputLog.Text, trmb.CanOnlyOutputToSubDirectory.Text);
            return false;
        }
        if (OutputLogInternal(filename, hideInfo))
        {
            if (_ui.Created)
            {
                PrintSystemLine(string.Format(trsl.LogFileHasBeenCreated.Text, filename.Replace(Program.ExeDir, "")));
                _console.RefreshStrings(true);
            }
            return true;
        }
        return false;
    }

    public bool OutputSystemLog(string filename)
    {
        if (filename == "" || filename == null)
            filename = Program.ExeDir + "emuera.log";
        if (!filename.StartsWith(Program.ExeDir, StringComparison.OrdinalIgnoreCase))
        {
            Dialog.Show(trmb.FailedOutputLog.Text, trmb.CanOnlyOutputToSubDirectory.Text);
            return false;
        }
        if (OutputLogInternal(filename, false))
        {
            if (_ui.Created)
            {
                PrintSystemLine(string.Format(trsl.LogFileHasBeenCreated.Text, filename.Replace(Program.ExeDir, "")));
                _console.RefreshStrings(true);
            }
            return true;
        }
        return false;
    }

    public string GetLog(bool hideInfo)
    {
        var builder = new StringBuilder();
        if (!hideInfo)
        {
            builder.AppendLine(trsl.EnvironmentInformation.Text);
            builder.AppendLine(AssemblyData.EmueraVersionText);
            builder.AppendLine();
            builder.AppendLine(trsl.Variant.Text);
            if (string.IsNullOrEmpty(_state.process.gameBase.ScriptTitle))
                builder.AppendLine(trsl.NotDefinedGameBase.Text);
            else
                builder.AppendLine(_state.process.gameBase.ScriptTitle + " " + _state.process.gameBase.ScriptVersionText);
            var patchVersionsPath = Path.Combine(Program.ExeDir, "patch_versions");
            if (Directory.Exists(patchVersionsPath))
            {
                builder.AppendLine(trsl.PatchVersion.Text);
                var versionTexts = Directory.EnumerateFiles(patchVersionsPath, "*.txt")
                    .Where(x => Path.GetExtension(x) == ".txt")
                    .OrderBy(x => x, StringComparer.Ordinal)
                    .Select(x => File.ReadAllText(x).Trim());
                builder.AppendLine(string.Join("+", versionTexts));
            }
            builder.AppendLine();
            builder.AppendLine(trsl.Log.Text);
            builder.AppendLine();
        }
        for (int i = 0; i < _state.displayLineList.Count; i++)
            builder.AppendLine(ClipboardProcessor.StripHTML(_state.displayLineList[i].ToString()));
        return builder.ToString();
    }

    // --- PrintC helpers ---

    private void CalcPrintCWidth(StringMeasure sm)
    {
        string str = new(' ', Config.PrintCLength);
        Font font = Config.DefaultFont;
        printCWidth = sm.GetDisplayLength(str, font);
        printCWidthL = sm.GetDisplayLength(str, font);
        printCWidthL2 = sm.GetDisplayLength(str, font);
    }

    private string CreateTypeCString(string str, bool alignmentRight)
    {
        if (printCWidth == -1)
            CalcPrintCWidth(_console.stringMeasure);
        int length = 0;
        int width;
        if (str != null)
            length = Encoding.GetEncoding("Shift-JIS").GetByteCount(str);
        int printcLength = Config.PrintCLength;
        Font font;
        try
        {
            font = new Font(Style.Fontname, Config.DefaultFont.Size, Style.FontStyle, GraphicsUnit.Pixel);
        }
        catch (Exception ex) { AgentLog.Instance.Write("font creation failed, fallback: " + ex.Message); return str; }

        if (alignmentRight && (length < printcLength))
        {
            str = new string(' ', printcLength - length) + str;
            width = _console.stringMeasure.GetDisplayLength(str, font);
            while (width > printCWidth)
            {
                if (str[0] != ' ') break;
                str = str.Remove(0, 1);
                width = _console.stringMeasure.GetDisplayLength(str, font);
            }
        }
        else if ((!alignmentRight) && (length < printcLength + 1))
        {
            str += new string(' ', printcLength + 1 - length);
            width = _console.stringMeasure.GetDisplayLength(str, font);
            while (width > printCWidthL)
            {
                if (str[^1] != ' ') break;
                str = str.Remove(str.Length - 1, 1);
                width = _console.stringMeasure.GetDisplayLength(str, font);
            }
        }
        return str;
    }
}
