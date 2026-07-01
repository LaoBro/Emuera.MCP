using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Text;
using MinorShift.Emuera.GameProc;
using MinorShift.Emuera.Runtime;
using MinorShift.Emuera.UI;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Runtime.Script.Parser;
using MinorShift.Emuera.Runtime.Script.Statements;
using MinorShift.Emuera.Runtime.Utils;
using MinorShift.Emuera.Runtime.Utils.EvilMask;
using MinorShift.Emuera.UI.Game;
using MinorShift.Emuera.UI.Game.Image;
using trerror = MinorShift.Emuera.Runtime.Utils.EvilMask.Lang.Error;

namespace MinorShift.Emuera.GameView;

internal sealed class EmueraConsole : IDisposable, IConsoleStateView
{
    internal readonly ConsoleStateData _state;
    internal readonly ConsoleStateManager _stateManager;
    internal readonly ConsolePrintManager _printManager;
    internal readonly ConsoleInputHandler _inputHandler;
    internal readonly ConsoleTimerManager _timer;
    internal readonly ConsoleRefreshHandler _refresh;
    private readonly IConsoleUI _uiAdapter;

    // --- Shared state objects required by PrintStringBuffer/ConsoleButtonString ---
    internal readonly PrintStringBuffer printBuffer;
    internal readonly StringMeasure stringMeasure = new();

    public EmueraConsole(IConsoleUI ui)
    {
        _uiAdapter = ui;
        _state = new ConsoleStateData();
        _stateManager = new ConsoleStateManager(_state, this, ui);
        _printManager = new ConsolePrintManager(_state, this, ui);
        _inputHandler = new ConsoleInputHandler(_state, this, ui);
        _timer = new ConsoleTimerManager(_state, this, ui);
        _refresh = new ConsoleRefreshHandler(_state, this, ui);

        // PrintStringBuffer needs EmueraConsole as parent
        printBuffer = new PrintStringBuffer(this);

        _state.displayLineList = [];
    }

    // ========================================
    // State & Properties (direct delegation to _state)
    // ========================================

    internal ConsoleState State => _state.State;
    internal InputRequest? CurrentRequest => _state.inputReq;
    public IConsoleUI UIAdapter => _uiAdapter;
    public bool Enabled => _uiAdapter.Created;
    internal bool IsActive => _uiAdapter.IsActive;
    public bool MesSkip { get => _state.MesSkip; set => _state.MesSkip = value; }
    public InputRequest? inputReq { get => _state.inputReq; set => _state.inputReq = value; }
    public bool IsTimeOut => _state.isTimeout;
    public InputType NowInputType => _state.inputReq!.InputType;
    public List<ConsoleDisplayLine> DisplayLineList => _state.displayLineList;
    public PrintStringBuffer PrintBuffer => printBuffer;
    public Dictionary<int, List<AConsoleDisplayNode>>? EscapedParts => _state.escapedParts;
    public int GetLineNo => _state.lineNo;
    public long LineCount => _state.logicalLineCount;
    public long DeletedLines => _state.deletedLines;
    public ConsoleButtonString? SelectingButton => _state.selectingButton;
    public ConsoleButtonString? PointingSring => _state.pointingString;
    public bool AlwaysRefresh { get => _state.AlwaysRefresh; set => _state.AlwaysRefresh = value; }
    public bool RunERBFromMemory { get => _state.runningERBfromMemory; set => _state.runningERBfromMemory = value; }
    public bool LastLineIsTemporary => _printManager.LastLineIsTemporary;
    public bool LastLineIsEmpty => _printManager.LastLineIsEmpty;
    public bool EmptyLine => printBuffer.IsEmpty;
    public bool noOutputLog { get => _state.noOutputLog; set => _state.noOutputLog = value; }
    public Color bgColor { get => _state.bgColor; set => _state.bgColor = value; }
    public bool UseUserStyle { get => _state.UseUserStyle; set => _state.UseUserStyle = value; }
    public bool UseSetColorStyle { get => _state.UseSetColorStyle; set => _state.UseSetColorStyle = value; }
    public StringStyle StringStyle => _state.userStyle;
    public DisplayLineAlignment Alignment { get => _state.alignment; set => _state.alignment = value; }
    public bool UpdatedGeneration { get => _state.updatedGeneration; set => _state.updatedGeneration = value; }
    public int LastButtonGeneration => (int)_state.lastButtonGeneration;
    public int NewButtonGeneration => (int)_state.newButtonGeneration;
    public ConsoleRedraw Redraw => _state.redraw;
    public StringMeasure StrMeasure => stringMeasure;
    public bool IsRunning
    {
        get
        {
            if (_state.State == ConsoleState.Initializing) return true;
            return _state.State == ConsoleState.Running || _state.runningERBfromMemory;
        }
    }
    public bool IsInProcess
    {
        get
        {
            if (_state.State == ConsoleState.Initializing) return true;
            if (_state.State == ConsoleState.Sleep) return true;
            if (_state.inProcess) return true;
            return _state.State == ConsoleState.Running || _state.runningERBfromMemory;
        }
    }
    public bool IsError => _state.State == ConsoleState.Error;
    public bool IsWaitingEnterKey
    {
        get
        {
            if (_state.State == ConsoleState.Quit || _state.State == ConsoleState.Error)
            {
                GlobalStatic.ForceQuitAndRestart = false;
                return true;
            }
            if (_state.State == ConsoleState.WaitInput)
            {
                GlobalStatic.ForceQuitAndRestart = false;
                return _state.inputReq!.InputType == InputType.AnyKey || _state.inputReq!.InputType == InputType.EnterKey;
            }
            return false;
        }
    }
    public bool IsWaitAnyKey
    {
        get
        {
            GlobalStatic.ForceQuitAndRestart = false;
            return _state.State == ConsoleState.WaitInput && _state.inputReq?.InputType == InputType.AnyKey;
        }
    }
    public bool IsWaintingOnePhrase => _state.State == ConsoleState.WaitInput && _state.inputReq?.OneInput == true;
    public bool IsWaintingInputWithMouse => _state.State == ConsoleState.WaitInput && _state.inputReq?.MouseInput == true;
    public bool IsRunningTimer => _state.State == ConsoleState.WaitInput && (_state.inputReq?.Timelimit ?? 0) > 0 && !_state.isTimeout;
    public bool IsWaitingPrimitive => _state.State == ConsoleState.WaitInput && _state.inputReq?.InputType == InputType.PrimitiveMouseKey;
    public bool ButtonIsSelected(ConsoleButtonString button) => _state.selectingButton == button;
    public bool ButtonIsPointing(ConsoleButtonString button) => _state.pointingStrings.Contains(button);
    public int ClientWidth => _uiAdapter.ClientWidth;
    public int ClientHeight => _uiAdapter.ClientHeight;
    public string? SelectedString
    {
        get
        {
            if (_state.selectingButton == null) return null;
            if (_state.State == ConsoleState.Error) return _state.selectingButton.Inputs;
            if (_state.State != ConsoleState.WaitInput) return null;
            if ((_state.inputReq!.InputType == InputType.IntValue || _state.inputReq!.InputType == InputType.IntButton) && _state.selectingButton.IsInteger)
                return _state.selectingButton.Input.ToString();
            if (_state.inputReq!.InputType == InputType.StrValue || _state.inputReq!.InputType == InputType.StrButton)
                return _state.selectingButton.Inputs;
            if (_state.inputReq!.InputType == InputType.AnyValue && _state.selectingButton.IsInteger)
                return _state.selectingButton.Input.ToString();
            if (_state.inputReq!.InputType == InputType.AnyValue)
                return _state.selectingButton.Inputs;
            return null;
        }
    }

    // ========================================
    // Lifecycle delegation
    // ========================================

    public System.Threading.Tasks.Task Initialize() => _stateManager.Initialize();
    public void Quit() => _stateManager.Quit();
    public void ForceQuit() => _stateManager.ForceQuit();
    public void ThrowTitleError(bool error) => _stateManager.ThrowTitleError(error);
    public void ThrowError(bool playSound) => _stateManager.ThrowError(playSound);
    public void GotoTitle() => _stateManager.GotoTitle();
    public void GotoTitleAndLoadAndRepeatInput() => _stateManager.GotoTitleAndLoadAndRepeatInput();
    public System.Threading.Tasks.Task ReloadErb() => _stateManager.ReloadErb();
    public void ReloadErbFinished() => _stateManager.ReloadErbFinished();
    public System.Threading.Tasks.Task ReloadPartialErb(List<string> path) => _stateManager.ReloadPartialErb(path);
    public System.Threading.Tasks.Task ReloadFolder(string erbPath) => _stateManager.ReloadFolder(erbPath);
    public void ReloadResource() => _stateManager.ReloadResource();
    public void Await(int time) => _stateManager.Await(time);

    // ========================================
    // Input delegation
    // ========================================

    public void WaitInput(InputRequest req) => _inputHandler.WaitInput(req);
    public void ReadAnyKey(bool anykey = false, bool stopMesskip = false) => _inputHandler.ReadAnyKey(anykey, stopMesskip);
    public void PressEnterKey(bool keySkip, string input, bool changedByMouse) => _inputHandler.PressEnterKey(keySkip, input, changedByMouse);
    public void InputMouseKey(int type, int result1, int result2, int result3, int result4, long result5) => _inputHandler.InputMouseKey(type, result1, result2, result3, result4, result5);
    public void MouseWheel(System.Drawing.Point point, int delta) => _inputHandler.MouseWheel(point, delta);
    public void MouseDown(System.Drawing.Point point, int button) => _inputHandler.MouseDown(point, button);
    public void PressPrimitiveKey(int keycode, int keydata, int keymod) => _inputHandler.PressPrimitiveKey(keycode, keydata, keymod);
    public bool MoveMouse(System.Drawing.Point point) => _inputHandler.MoveMouse(point);
    public void LeaveMouse() => _inputHandler.LeaveMouse();
    internal void SetSelectingButton(ConsoleButtonString? button) => _inputHandler.SetSelectingButton(button);
    internal List<ConsoleButtonString> CollectCurrentButtons()
    {
        var result = new List<ConsoleButtonString>();
        _inputHandler.CollectCurrentButtons(result);
        return result;
    }

    // ========================================
    // Print delegation
    // ========================================

    public void Print(string str, bool lineEnd = true) => _printManager.Print(str, lineEnd);
    public void PrintSingleLine(string str) => _printManager.PrintSingleLine(str);
    public void PrintSingleLine(string str, bool temporary) => _printManager.PrintSingleLine(str, temporary);
    public void PrintC(string str, bool alignmentRight) => _printManager.PrintC(str, alignmentRight);
    public void PrintFlush(bool force) => _printManager.PrintFlush(force);
    public void PrintSystemLine(string str) => _printManager.PrintSystemLine(str);
    public void PrintError(string str) => _printManager.PrintError(str);
    internal void PrintErrorButton(string str, ScriptPosition? pos, int level = 0) => _printManager.PrintErrorButton(str, pos, level);
    public void PrintWarning(string str, ScriptPosition? position, int level) => _printManager.PrintWarning(str, position, level);
    public void PrintTemporaryLine(string str) => _printManager.PrintTemporaryLine(str);
    public void PrintBar() => _printManager.PrintBar();
    public void printCustomBar(string barStr, bool isConst) => _printManager.PrintCustomBar(barStr, isConst);
    public void PrintButton(string str, string p) => _printManager.PrintButton(str, p);
    public void PrintButton(string str, long p) => _printManager.PrintButton(str, p);
    internal void PrintButtonC(string str, string p, bool isRight) => _printManager.PrintButtonC(str, p, isRight);
    internal void PrintButtonC(string str, long p, bool isRight) => _printManager.PrintButtonC(str, p, isRight);
    internal void PrintPlain(string str) => _printManager.PrintPlain(str);
    public void PrintHtml(string str, bool toPrintBuffer) => _printManager.PrintHtml(str, toPrintBuffer);
    public void PrintHTMLIsland(string html) => _printManager.PrintHTMLIsland(html);
    public void ClearHTMLIsland() => _printManager.ClearHTMLIsland();
    public void PrintImg(string name, string nameb, string namem, Utils.MixedNum height, Utils.MixedNum width, Utils.MixedNum ypos) => _printManager.PrintImg(name, nameb, namem, height, width, ypos);
    public void PrintShape(string type, Utils.MixedNum[] param) => _printManager.PrintShape(type, param);
    public ConsoleDisplayLine[]? GetDisplayLines(long lineNo) => _printManager.GetDisplayLines(lineNo);
    public ConsoleDisplayLine[]? PopDisplayingLines() => _printManager.PopDisplayingLines();
    internal ConsoleDisplayLine? PrintPlainwithSingleLine(string str) => _printManager.PrintPlainwithSingleLine(str);
    public void PrintPlainWithSingleLineFix(string str) => _printManager.PrintPlainWithSingleLineFix(str);
    public ConsoleDisplayLine? BufferToSingleLine(bool force, bool temporary) => _printManager.BufferToSingleLine(force, temporary);
    public void ClearText() => _printManager.ClearText();

    // ========================================
    // Line management
    // ========================================

    public void ClearDisplay() => _printManager.ClearDisplay();
    public void deleteLine(int argNum) => _printManager.DeleteLine(argNum);
    public void changeLastLine(string str) { _printManager.DeleteLine(1); _printManager.PrintSingleLine(str, false); }
    public void ChangeLastLine(string str) => changeLastLine(str);
    public void ForceStopTimer() => _timer.ForceStopTimer();
    public void NewLine() { PrintFlush(true); RefreshStrings(false); }

    // ========================================
    // Style delegation
    // ========================================

    public void SetStringStyle(FontStyle fs) => _printManager.SetStringStyle(fs);
    public void SetStringStyle(Color color) => _printManager.SetStringStyle(color);
    public void SetFont(string fontname) => _printManager.SetFont(fontname);
    public void ResetStyle() => _printManager.ResetStyle();
    public void SetBgColor(Color color) => _printManager.SetBgColor(color);

    // ========================================
    // Bar / StBar delegation
    // ========================================

    public string getStBar(string barStr) => _printManager.GetStBar(barStr);
    public string? getDefStBar() => _printManager.GetDefStBar();
    public void setStBar(string barStr) => _printManager.SetStBar(barStr);

    // ========================================
    // Log delegation
    // ========================================

    public bool OutputLog(string? filename, bool hideInfo) => _printManager.OutputLog(filename, hideInfo);
    public bool OutputSystemLog(string filename) => _printManager.OutputSystemLog(filename);
    public string GetLog(bool hideInfo) => _printManager.GetLog(hideInfo);

    // ========================================
    // Timer delegation
    // ========================================

    internal void SubmitTimeout() => _timer.SubmitTimeout();
    internal long? InputTimeoutMs => _timer.InputTimeoutMs;
    internal bool IsDisplayTimeActive => _timer.IsDisplayTimeActive;
    internal string? TimeUpMessage => _timer.TimeUpMessage;
    internal string BuildCountdownText() => _timer.BuildCountdownText();
    public void setRedrawTimer(int tickcount) => _timer.SetRedrawTimer(tickcount);
    public void forceStopTimer() => _timer.ForceStopTimer();

    // ========================================
    // Refresh delegation
    // ========================================

    public void RefreshStrings(bool force_Paint) => _refresh.RefreshStrings(force_Paint);
    public void SetRedraw(long i) => _refresh.SetRedraw(i);
    internal void VerticalScrollBarUpdate() => _refresh.VerticalScrollBarUpdate();

    // ========================================
    // RunEmueraProgram / newGeneration (core game loop)
    // ========================================

    internal void RunEmueraProgram(string? input)
    {
        if (input != null)
        {
            if (!_inputHandler.DoInputToEmueraProgram(input))
                return;
            if (_state.State == ConsoleState.Error)
                return;
        }
        _state.State = ConsoleState.Running;
        _state.process!.DoScript();
        if (_state.State == ConsoleState.Running)
        {
            _state.State = ConsoleState.Error;
            PrintError(trerror.ProgramStatusError.Text);
        }
        if (_state.State == ConsoleState.Error && !_state.noOutputLog)
            OutputSystemLog(Program.ExeDir + "emuera.log");
        PrintFlush(false);
        _inputHandler.NewGeneration();
    }

    internal void ForceUpdateGeneration() => _inputHandler.ForceUpdateGeneration();
    internal void UpdateGeneration() => _inputHandler.UpdateGeneration();

    // ========================================
    // Debug delegation
    // ========================================

    public void DebugPrint(string str)
    {
        if (!Program.DebugMode) return;
        _state.dConsoleLog.Append(str);
    }
    public void DebugClear() => _state.dConsoleLog.Remove(0, _state.dConsoleLog.Length);
    public void DebugNewLine()
    {
        if (!Program.DebugMode) return;
        _state.dConsoleLog.Append(Environment.NewLine);
    }
    public string DebugConsoleLog => _state.dConsoleLog.ToString();
    public void OpenDebugDialog() { } // headless stub
    public string? DebugTitle => _state.debugTitle;
    public void DebugCommand(string com, bool munchkin, bool outputDebugConsole) { } // headless stub
    public void DebugAddTraceLog(string str)
    {
        if (!Program.DebugMode || _state.runningERBfromMemory) return;
        _state.dTraceLogList.Add(str);
    }
    public void DebugRemoveTraceLog()
    {
        if (!Program.DebugMode || _state.runningERBfromMemory) return;
        if (_state.dTraceLogList.Count > 0)
            _state.dTraceLogList.RemoveAt(_state.dTraceLogList.Count - 1);
    }
    public void DebugClearTraceLog()
    {
        if (!Program.DebugMode || _state.runningERBfromMemory) return;
        _state.dTraceLogList.Clear();
    }

    // ========================================
    // Window delegation
    // ========================================

    public void SetWindowTitle(string str)
    {
        if (Program.DebugMode)
        {
            _state.debugTitle = str;
            _uiAdapter.Text = str + " (Debug Mode)";
        }
        else
            _uiAdapter.Text = str;
    }
    public string GetWindowTitle()
    {
        if (Program.DebugMode && _state.debugTitle != null)
            return _state.debugTitle;
        return _uiAdapter.Text;
    }
    public void SetEmueraVersionInfo(string str) => _uiAdapter.TextBox.Text = str;

    // ========================================
    // Mouse helpers
    // ========================================

    public System.Drawing.Point GetMousePosition() => _uiAdapter.GetMousePosition();

    // ========================================
    // WinForms stubs (no-ops in headless)
    // ========================================

    public void CBG_Clear() { }
    public void CBG_ClearRange(int zmin, int zmax) { }
    public void CBG_ClearButton() { }
    public void CBG_ClearBMap() { }
    public bool CBG_SetGraphics(GraphicsImage gra, int x, int y, int zdepth) => false;
    public bool CBG_SetImage(ASprite image, int x, int y, int zdepth) => false;
    public bool CBG_SetButtonMap(GraphicsImage gra) => false;
    public bool CBG_SetButtonImage(int buttonValue, ASprite imageN, ASprite imageB, int x, int y, int zdepth, string? tooltip = null) => false;
    public void AddBackgroundImage(string name, long depth, float opacity) { }
    public void ClearBackgroundImage() { }
    public void RemoveBackground(string key) { }
    public void ValidateBackground(int width, int height) { }
    public void SetToolTipColor(Color foreColor, Color backColor) { }
    public void SetToolTipDelay(int delay) { }
    public void SetToolTipDuration(int duration) { }
    public void SetToolTipFontName(string fn) { }
    public void SetToolTipFontSize(long fs) { }
    public void SetToolTipFormat(long f) { }
    public void SetToolTipImg(bool b) { }
    public void CustomToolTip(bool b) { }

    // Bitmap cache stubs
    public const nint bitmapCacheArrayCap = 256;
    public ConsoleButtonString[] bitmapCacheArray = new ConsoleButtonString[bitmapCacheArrayCap];
    public nint bitmapCacheArrayIndex = 0;
    public bool bitmapCacheEnabledForNextNextLine { get => _state.bitmapCacheEnabledForNextLine; set => _state.bitmapCacheEnabledForNextLine = value; }

    // Rikaichan stub
    public object? rikaichan;

    // Clipboard stub
    public readonly ClipboardProcessor? CBProc = null;

    // ========================================
    // Terminal rendering (from AgentBridge.cs partial)
    // ========================================

    internal TerminalCharWidthConfig CharWidthConfig
    {
        get => _state.CharWidthConfig;
        set => _state.CharWidthConfig = value;
    }

    // ========================================
    // IConsoleStateView implementation (from AgentBridge.cs partial)
    // ========================================

    public bool ConsumeNeedFullRefresh()
    {
        bool v = _state._needFullRefresh;
        _state._needFullRefresh = false;
        return v;
    }
    public int ConsumePendingEraseRows()
    {
        int rows = _state._pendingEraseRows;
        _state._pendingEraseRows = 0;
        return rows;
    }
    public void AppendToAgentBuffer(string text, bool newLine)
    {
        if (newLine) _state._agentBuffer.AppendLine(text);
        else _state._agentBuffer.Append(text);
    }

    // ========================================
    // AgentBuffer methods (from AgentBuffer.cs partial)
    // ========================================

    internal void WriteToAgentBuffer(string text)
    {
        _state._agentBuffer.AppendLine(text);
        _state._agentBufferLineCount++;
    }
    internal void WriteToAgentBufferNoNewline(string text)
    {
        _state._agentBuffer.Append(text);
        _state._agentBufferLineCount++;
    }
    internal string TakeAgentBuffer()
    {
        var text = _state._agentBuffer.ToString();
        _state._agentBuffer.Clear();
        _state._agentBufferLineCount = 0;
        return text;
    }
    internal bool RemoveLastLineFromAgentBuffer()
    {
        if (_state._agentBufferLineCount <= 0) return false;
        string content = _state._agentBuffer.ToString();
        if (content.Length <= 1)
        {
            _state._agentBuffer.Clear();
            _state._agentBufferLineCount--;
            return true;
        }
        int lastNewline = content.LastIndexOf('\n', content.Length - 2, content.Length - 1);
        if (lastNewline < 0)
            _state._agentBuffer.Clear();
        else
            _state._agentBuffer.Remove(lastNewline, _state._agentBuffer.Length - lastNewline);
        _state._agentBufferLineCount--;
        return true;
    }

    // ========================================
    // Terminal rendering helpers (from AgentBridge.cs)
    // ========================================

    internal string FormatLineForTerminal(ConsoleDisplayLine line)
    {
        string text = BuildTerminalLine(line, out int textWidth);
        if (textWidth == 0 && text.Length == 0)
            return "";
        int gameWidth = GetGameColumnWidth();
        string styledText = IsAnsiEnabled() ? FormatLineWithAnsi(line) : text;
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
        return TerminalDisplayWidth.ReplaceForTerminal(output, CharWidthConfig);
    }

    internal void WriteAlignedLine(ConsoleDisplayLine line)
    {
        string text = BuildTerminalLine(line, out int textWidth);
        if (textWidth == 0 && text.Length == 0)
        {
            if (line.IsLineEnd) WriteToAgentBuffer("");
            else WriteToAgentBufferNoNewline("");
            return;
        }
        int gameWidth = GetGameColumnWidth();
        string styledText = IsAnsiEnabled() ? FormatLineWithAnsi(line) : text;
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
        output = TerminalDisplayWidth.ReplaceForTerminal(output, CharWidthConfig);
        if (line.IsLineEnd)
            WriteToAgentBuffer(output);
        else
            WriteToAgentBufferNoNewline(output);
    }

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
                        var style = css.StringStyle;
                        if (lastColor != style.Color || lastFontStyle != style.FontStyle)
                        {
                            if (lastColor != null || lastFontStyle != FontStyle.Regular)
                            {
                                sb.Append("\x1b[0m");
                                if (isSelected) sb.Append("\x1b[7m");
                            }
                            sb.Append($"\x1b[38;2;{style.Color.R};{style.Color.G};{style.Color.B}m");
                            if ((style.FontStyle & FontStyle.Bold) != 0) sb.Append("\x1b[1m");
                            if ((style.FontStyle & FontStyle.Italic) != 0) sb.Append("\x1b[3m");
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
                                sb.Append(FormatLineWithAnsi(child));
                        }
                        break;
                    default:
                        sb.Append(node.Text ?? "");
                        break;
                }
            }
            if (isSelected) sb.Append("\x1b[27m");
        }
        if (lastColor != null || lastFontStyle != FontStyle.Regular)
            sb.Append("\x1b[0m");
        return sb.ToString();
    }

    private static int GetGameColumnWidth()
    {
        int charWidth = Math.Max(Config.FontSize / 2, 1);
        return Config.DrawableWidth / charWidth;
    }

    private static bool IsAnsiEnabled() => WindowsConsoleHelper.AnsiEnabled || !OperatingSystem.IsWindows();

    // ========================================
    // Agent / Bridge
    // ========================================

    internal AgentProtocolBase? AgentBridge => _state.agentBridge;
    internal void SetAgentBridge(AgentProtocolBase protocol) => _state.agentBridge = protocol;

    // ========================================
    // Button generation helpers
    // ========================================

    public void forceUpdateGeneration() => _inputHandler.ForceUpdateGeneration();
    public bool bitmapCacheEnabledForNextLine { get => _state.bitmapCacheEnabledForNextLine; set => _state.bitmapCacheEnabledForNextLine = value; }
    public bool updatedGeneration { get => _state.updatedGeneration; set => _state.updatedGeneration = value; }

    // ========================================
    // GetLinePointY (used by ConsoleButtonString)
    // ========================================

    public int GetLinePointY(int lineNo)
    {
        int pointY = _uiAdapter.ClientHeight - Config.LineHeight;
        int bottomLineNo = _uiAdapter.ScrollBar.Value - 1;
        if (_state.displayLineList.Count - 1 < bottomLineNo)
            bottomLineNo = _state.displayLineList.Count - 1;
        pointY -= (bottomLineNo - lineNo) * Config.LineHeight;
        return pointY;
    }

    // ========================================
    // Dispose
    // ========================================

    public void Dispose()
    {
        _stateManager.Dispose();
    }
}
