using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Timers;
using MinorShift.Emuera.Primitives;
using MinorShift.Emuera.GameProc;
using MinorShift.Emuera.Runtime;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Runtime.Script.Statements;
using MinorShift.Emuera.UI.Game;
using GameProcProcess = MinorShift.Emuera.GameProc.Process;

namespace MinorShift.Emuera.GameView;

/// <summary>
/// Console state enum (from original EmueraConsole.cs).
/// </summary>
internal enum ConsoleState
{
    Initializing = 0,
    Quit = 5,
    Error = 6,
    Running = 7,
    WaitInput = 20,
    Sleep = 21,
}

/// <summary>
/// Console redraw enum (from original EmueraConsole.cs).
/// </summary>
internal enum ConsoleRedraw
{
    None = 0,
    Normal = 1,
}

/// <summary>
/// Centralized shared state for EmueraConsole.
/// All mutable state that crosses responsibility boundaries lives here.
/// Manager classes hold a reference to this object to read/write state.
/// </summary>
internal sealed class ConsoleStateData
{
    // --- Core game state ---
    internal ConsoleState State;
    internal InputRequest? inputReq;
    internal GameProcProcess? process;
    internal bool isTimeout;
    internal long timerID = -1;
    internal long timer_endTime;
    internal bool need_settimer;
    internal bool force_temporary;
    internal bool timer_suspended;
    internal ConsoleState prevState;
    internal InputRequest? prevReq;
    internal bool inProcess;
    internal volatile bool KillMacro;
    internal bool MesSkip;

    // --- Display / line management ---
    internal List<ConsoleDisplayLine> displayLineList = [];
    internal int lineNo;
    internal int lastDrawnLineNo = -1;
    internal long logicalLineCount;
    internal long deletedLines;
    internal Dictionary<int, List<AConsoleDisplayNode>>? escapedParts;
    internal List<ConsoleDisplayLine> _htmlElementList = new(10);

    // --- Button / selection state ---
    internal ConsoleButtonString? selectingButton;
    internal ConsoleButtonString? lastSelectingButton;
    internal ConsoleButtonString? pointingString;
    internal ConsoleButtonString? lastPointingString;
    internal HashSet<ConsoleButtonString> pointingStrings = [];
    internal long lastButtonGeneration;
    internal long newButtonGeneration;
    internal bool lastButtonIsInput = true;
    internal bool updatedGeneration;
    internal LogicalLine? lastInputLine;

	// --- Style ---
	internal StringStyle defaultStyle = new(Config.ForeColor, EmuFontStyle.Regular, null);
	internal StringStyle userStyle = new(Config.ForeColor, EmuFontStyle.Regular, null);
	internal DisplayLineAlignment alignment = DisplayLineAlignment.LEFT;
	internal EmuColor bgColor = Config.BackColor;
	internal bool UseUserStyle;
	internal bool UseSetColorStyle;
	internal string? stBar;

    // --- Timer ---
    internal readonly Stopwatch _genericTimerStopwatch = new();
    internal System.Timers.Timer genericTimer = new();
    internal System.Timers.Timer redrawTimer;
    internal long timeDisplayCount;
    internal bool inputed;

    // --- Draw state ---
    internal Stopwatch _frameDeltaTimer = Stopwatch.StartNew();
    internal uint msPerFrame = 1000 / 60;
    internal Stopwatch? _drawStopwatch;
    internal bool forceTextBoxColor;
    internal ConsoleRedraw redraw = ConsoleRedraw.Normal;

    // --- Misc ---
    internal bool runningERBfromMemory;
    internal bool noOutputLog;
    internal bool notToTitle;
    internal bool byError;
    internal bool AlwaysRefresh;
    internal string? debugTitle;
    internal readonly StringBuilder dConsoleLog = new("");
    internal List<string> dTraceLogList = [];
    internal bool tooltipUsed;
    internal int tooltip_duration;
    internal string tooltip_fontname = Config.FontName;
    internal long tooltip_fontsize = Config.FontSize;
    internal bool tooltip_img;
    internal readonly ClipboardProcessor? CBProc;

    // --- WinForms-only (stubs in headless) ---
    internal int selectingCBGButtonInt = -1;
    internal int lastSelectingCBGButtonInt = -1;

    // --- Agent / Headless ---
    internal AgentProtocolBase? agentBridge;
    internal readonly StringBuilder _agentBuffer = new();
    internal int _agentBufferLineCount;
    internal bool _needFullRefresh;
    internal int _pendingEraseRows;
    internal TerminalCharWidthConfig CharWidthConfig = TerminalCharWidthConfig.Default;

    // --- Bitmap cache ---
    public const nint bitmapCacheArrayCap = 256;
    internal ConsoleButtonString[] bitmapCacheArray = new ConsoleButtonString[bitmapCacheArrayCap];
    internal nint bitmapCacheArrayIndex = 0;
    internal bool bitmapCacheEnabledForNextLine;

    internal ConsoleStateData()
    {
        State = ConsoleState.Initializing;
        CBProc = null;

        genericTimer = new();
        genericTimer.Interval = 10;
        genericTimer.Enabled = false;

        redrawTimer = new System.Timers.Timer
        {
            Enabled = false
        };
        redrawTimer.Interval = 10;
    }
}
