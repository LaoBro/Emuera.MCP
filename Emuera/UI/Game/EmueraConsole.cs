//using System.Drawing.Imaging;
#if !HEADLESS
using MinorShift.Emuera.Forms;
#endif
//using MinorShift.Emuera.GameData;
using MinorShift.Emuera.GameProc.Function;
using MinorShift.Emuera.Runtime;

//using System.Diagnostics.Eventing.Reader;
//using System.Linq.Expressions;
//using System.Windows;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Runtime.Config.JSON;
using MinorShift.Emuera.Runtime.Script.Parser;
using MinorShift.Emuera.Runtime.Script.Statements;
using MinorShift.Emuera.Runtime.Script.Statements.Expression;
using MinorShift.Emuera.Runtime.Utils;
using MinorShift.Emuera.Runtime.Utils.EvilMask;
using MinorShift.Emuera.UI.Game;
using MinorShift.Emuera.UI.Game.Image;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
#if !HEADLESS
using System.Drawing.Imaging;
#endif
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
#if !HEADLESS
using System.Windows.Forms;
#endif
using trerror = MinorShift.Emuera.Runtime.Utils.EvilMask.Lang.Error;
using trmb = MinorShift.Emuera.Runtime.Utils.EvilMask.Lang.MessageBox;
using trsl = MinorShift.Emuera.Runtime.Utils.EvilMask.Lang.SystemLine;


namespace MinorShift.Emuera.GameView;

//入出力待ちの状況。
//難読化用属性。enum.ToString()やenum.Parse()を行うなら(Exclude=true)にすること。
[System.Reflection.Obfuscation(Exclude = false)]
internal enum ConsoleState
{
	Initializing = 0,
	Quit = 5,//QUIT
	Error = 6,//Exceptionによる強制終了
	Running = 7,
	WaitInput = 20,
	Sleep = 21,//DoEvents

	//WaitKey = 1,//WAIT
	//WaitSystemInteger = 2,//Systemが要求するInput
	//WaitInteger = 3,//INPUT
	//WaitString = 4,//INPUTS
	//WaitIntegerWithTimer = 8,
	//WaitStringWithTimer = 9,
	//Timeout = 10,
	//Timeouts = 11,
	//WaitKeyWithTimer = 12,
	//WaitKeyWithTimerF = 13,
	//WaitOneInteger = 14,
	//WaitOneString = 15,
	//WaitOneIntegerWithTimer = 16,
	//WaitOneStringWithTimer = 17,
	//WaitAnyKey = 18,

}

//難読化用属性。enum.ToString()やenum.Parse()を行うなら(Exclude=true)にすること。
[System.Reflection.Obfuscation(Exclude = false)]
internal enum ConsoleRedraw
{
	None = 0,
	Normal = 1,
}

internal sealed partial class EmueraConsole : IDisposable
{
	#region EmuEra-Rikaichan
#if !HEADLESS
	public Rikaichan rikaichan = new();
#else
	public object rikaichan;
#endif
	#endregion

	//Bitmap Cache
	public const nint bitmapCacheArrayCap = 256;
	public ConsoleButtonString[] bitmapCacheArray = new ConsoleButtonString[bitmapCacheArrayCap];
	public nint bitmapCacheArrayIndex = 0;
	public bool bitmapCacheEnabledForNextLine;

#if !HEADLESS
	public EmueraConsole(MainWindow parent)
	{
		_uiAdapter = new WinFormsConsole(parent);
		#region EE_AnchorのCB機能移植
		CBProc = new ClipboardProcessor(parent);
		#endregion

		//1.713 この段階でsetStBarを使用してはいけない
		//setStBar(StaticConfig.DrawLineString);
		state = ConsoleState.Initializing;
		if (Config.FPS > 0)
			msPerFrame = 1000 / (uint)Config.FPS;
		displayLineList = [];
		printBuffer = new PrintStringBuffer(this);

		genericTimer = new();
		genericTimer.Elapsed += tickTimer;
		genericTimer.Interval = 10;
		genericTimer.Enabled = false;
#if !HEADLESS
		CBG_Clear();//文字列描画用ダミー追加
#endif

		redrawTimer = new System.Timers.Timer
		{
			Enabled = false//TODO:1824アニメ用再描画タイマー有効化関数の追加
		};
		redrawTimer.Elapsed += tickRedrawTimer;
		redrawTimer.Interval = 10;

		// 检测终端输入协议（不自动启动线程）
		_agentBridge = AgentProtocolBase.Detect(this, _uiAdapter);
	}
#endif

	public EmueraConsole(IConsoleUI ui)
	{
		_uiAdapter = ui;
		#region EE_AnchorのCB機能移植
		CBProc = null;
		#endregion

		state = ConsoleState.Initializing;
		if (Config.FPS > 0)
			msPerFrame = 1000 / (uint)Config.FPS;
		displayLineList = [];
		printBuffer = new PrintStringBuffer(this);

		genericTimer = new();
		genericTimer.Elapsed += tickTimer;
		genericTimer.Interval = 10;
		genericTimer.Enabled = false;
#if !HEADLESS
		CBG_Clear();//文字列描画用ダミー追加
#endif

		redrawTimer = new System.Timers.Timer
		{
			Enabled = false//TODO:1824アニメ用再描画タイマー有効化関数の追加
		};
		redrawTimer.Elapsed += tickRedrawTimer;
		redrawTimer.Interval = 10;

		// 检测终端输入协议（不自动启动线程）
		_agentBridge = AgentProtocolBase.Detect(this, _uiAdapter);
	}
	#region 1823 cbg関連
#if !HEADLESS
	private readonly List<ClientBackGroundImage> cbgList = [];
	private GraphicsImage cbgButtonMap;
	private int selectingCBGButtonInt = -1;
	private int lastSelectingCBGButtonInt = -1;
	//ConsoleButtonString selectingButton = null;
	//ConsoleButtonString lastSelectingButton = null;

	sealed class ClientBackGroundImage : IComparable<ClientBackGroundImage>
	{
		/// <summary>
		/// zdepth == 0は文字列用ダミーなので他で使ってはいけない
		/// </summary>
		/// <param name="zdepth"></param>
		internal ClientBackGroundImage(int zdepth)
		{ this.zdepth = zdepth; }
		public ASprite Img;
		public ASprite ImgB;
		public int x;
		public int y;
		public readonly int zdepth;
		public bool isButton;
		public int buttonValue;
		public string tooltipString;
		public int CompareTo(ClientBackGroundImage other)
		{
			if (other == null)
				return -1;
			//逆順でSort
			return -zdepth.CompareTo(other.zdepth);
		}
	}
	public void CBG_Clear()
	{
		foreach (ClientBackGroundImage cimg in cbgList)
		{
			//使い捨て無名Imageを一応disposeしておく
			if (cimg.Img != null && cimg.Img.Name.Length == 0)
				cimg.Img.Dispose();
		}
		cbgList.Clear();
		CBG_ClearBMap();
		cbgList.Add(new ClientBackGroundImage(0));
	}

	public void CBG_ClearRange(int zmin, int zmax)
	{
		if (zmin > zmax)
			return;
		for (int i = 0; i < cbgList.Count; i++)
		{
			ClientBackGroundImage cimg = cbgList[i];
			if (cimg.zdepth < zmin || cimg.zdepth > zmax || cimg.zdepth == 0)//0はダミーなので削除しない
				continue;

			//使い捨て無名Imageを一応disposeしておく
			if (cimg.Img != null && cimg.Img.Name.Length == 0)
				cimg.Img.Dispose();
			cbgList.RemoveAt(i);
			i--;
		}
	}

	public void CBG_ClearButton()
	{
		for (int i = 0; i < cbgList.Count; i++)
		{
			ClientBackGroundImage cimg = cbgList[i];
			if (!cimg.isButton)
				continue;

			//使い捨て無名Imageを一応disposeしておく
			if (cimg.Img != null && cimg.Img.Name.Length == 0)
				cimg.Img.Dispose();
			cbgList.RemoveAt(i);
			i--;
		}
		CBG_ClearBMap();
	}

	public void CBG_ClearBMap()
	{
		cbgButtonMap = null;
		selectingCBGButtonInt = -1;
		lastSelectingCBGButtonInt = -1;
	}

	public bool CBG_SetGraphics(GraphicsImage gra, int x, int y, int zdepth)
	{
		if (gra == null || !gra.IsCreated)
			return false;
		return CBG_SetImage(new SpriteG("", gra, new Rectangle(0, 0, gra.Width, gra.Height)), x, y, zdepth);
	}
	public bool CBG_SetImage(ASprite image, int x, int y, int zdepth)
	{
		if (image == null || !image.IsCreated)
			return false;
		ArgumentOutOfRangeException.ThrowIfZero(zdepth);
		ClientBackGroundImage cbg = new(zdepth)
		{
			Img = image,
			x = x,
			y = y
		};
		//cbg.zdepth = zdepth;
		cbgList.Add(cbg);
		cbgList.Sort();
		return true;
	}

	public bool CBG_SetButtonMap(GraphicsImage gra)
	{
		if (gra == null || !gra.IsCreated)
			return false;
		if (cbgButtonMap == gra)
			return false;
		cbgButtonMap = gra;
		selectingCBGButtonInt = -1;
		lastSelectingCBGButtonInt = -1;
		return true;
	}

	public bool CBG_SetButtonImage(int buttonValue, ASprite imageN, ASprite imageB, int x, int y, int zdepth, string tooltip = null)
	{
		ArgumentOutOfRangeException.ThrowIfZero(zdepth);
		ClientBackGroundImage cbg = new(zdepth)
		{
			Img = imageN,
			ImgB = imageB,
			x = x,
			y = y,
			//cbg.zdepth = zdepth;
			isButton = true,
			buttonValue = buttonValue,
			tooltipString = tooltip
		};
		cbgList.Add(cbg);
		cbgList.Sort();
		return true;
	}
	public int ClientWidth { get { return _uiAdapter.ClientWidth; } }
	public int ClientHeight { get { return _uiAdapter.ClientHeight; } }
#endif // !HEADLESS
	#endregion
#if HEADLESS
	private int selectingCBGButtonInt = -1;
	private int lastSelectingCBGButtonInt = -1;
	public int ClientWidth => _uiAdapter.ClientWidth;
	public int ClientHeight => _uiAdapter.ClientHeight;
	public void CBG_Clear() { }
	public void CBG_ClearRange(int zmin, int zmax) { }
	public void CBG_ClearButton() { }
	public void CBG_ClearBMap() { }
	public bool CBG_SetGraphics(GraphicsImage gra, int x, int y, int zdepth) => false;
	public bool CBG_SetImage(ASprite image, int x, int y, int zdepth) => false;
	public bool CBG_SetButtonMap(GraphicsImage gra) => false;
	public bool CBG_SetButtonImage(int buttonValue, ASprite imageN, ASprite imageB, int x, int y, int zdepth, string tooltip = null) => false;
	public void AddBackgroundImage(string name, long depth, float opacity) { }
#endif

	const string ErrorButtonsText = "__openFileWithDebug__";
	private readonly IConsoleUI _uiAdapter;
	private AgentProtocolBase _agentBridge;
#if !HEADLESS
	#region EE_MOUSEB
	public MainWindow Window { get { return (_uiAdapter as WinFormsConsole)?.GetMainWindow(); } }
	#endregion
#endif
	public IConsoleUI UIAdapter => _uiAdapter;
	public AgentProtocolBase AgentBridge => _agentBridge;
	#region EE_BINPUT
	public List<ConsoleDisplayLine> DisplayLineList { get { return displayLineList; } }
	#endregion
	#region EE_AnchorのCB機能移植
	public readonly ClipboardProcessor CBProc;
	#endregion
#if !HEADLESS
	private List<KeyValuePair<long, ConsoleBackground>> backgroundList = [];
	private Bitmap bakedBackground;
#endif

	GameProc.Process process;
	// ConsoleState state = ConsoleState.Initializing;
	#region EM_私家版_描画拡張
	ConsoleState _state = ConsoleState.Initializing;
	ConsoleState state
	{
		get { return _state; }
		set
		{
			switch (value)
			{
				case ConsoleState.Quit:
				case ConsoleState.Error:
					ConsoleEscapedParts.Clear();
					break;
			}
			_state = value;
		}
	}
	#endregion
	public bool Enabled { get { return _uiAdapter.Created; } }

	/// <summary>
	/// 現在、Emueraがアクティブかどうか
	/// </summary>
	internal bool IsActive
	{ get { return _uiAdapter.IsActive; } }

	/// <summary>
	/// スクリプトが継続中かどうか
	/// 入力系はメッセージスキップやマクロも含めてIsInProcessを参照すべき
	/// </summary>
	internal bool IsRunning
	{
		get
		{
			if (state == ConsoleState.Initializing)
				return true;
			return state == ConsoleState.Running || runningERBfromMemory;
		}
	}
	#region EM_私家版_INPUT系機能拡張
	internal bool IsWaintingInputWithMouse
	{
		get
		{
			return state == ConsoleState.WaitInput && inputReq.MouseInput;
		}
	}
	#endregion
	internal bool IsInProcess
	{
		get
		{
			if (state == ConsoleState.Initializing)
				return true;
			if (state == ConsoleState.Sleep)
				return true;
			if (inProcess)
				return true;
			return state == ConsoleState.Running || runningERBfromMemory;
		}
	}

	internal bool IsError
	{
		get
		{
			return state == ConsoleState.Error;
		}
	}
	#region EE_連続FORCE_QUIT_AND_RESTAR対策
	internal bool IsWaitingEnterKey
	{
		get
		{
			if ((state == ConsoleState.Quit) || (state == ConsoleState.Error))
			{
				GlobalStatic.ForceQuitAndRestart = false;
				return true;
			}
			if (state == ConsoleState.WaitInput)
			{
				GlobalStatic.ForceQuitAndRestart = false;
				return inputReq.InputType == InputType.AnyKey || inputReq.InputType == InputType.EnterKey;
			}
			return false;
		}
	}

	internal bool IsWaitAnyKey
	{
		get
		{
			GlobalStatic.ForceQuitAndRestart = false;
			return state == ConsoleState.WaitInput && inputReq.InputType == InputType.AnyKey;
		}
	}
	#endregion
	internal bool IsWaintingOnePhrase
	{
		get
		{
			return state == ConsoleState.WaitInput && inputReq.OneInput;
		}
	}

	internal bool IsRunningTimer
	{
		get
		{
			return state == ConsoleState.WaitInput && inputReq.Timelimit > 0 && !isTimeout;
		}
	}

	internal bool IsWaitingPrimitive
	{
		get
		{
			if (state == ConsoleState.WaitInput)
				return inputReq.InputType == InputType.PrimitiveMouseKey;
			return false;
		}
	}

	internal string SelectedString
	{
		get
		{
			if (selectingButton == null)
				return null;
			if (state == ConsoleState.Error)
				return selectingButton.Inputs;
			if (state != ConsoleState.WaitInput)
				return null;
			#region EE_BINPUT
			if ((inputReq.InputType == InputType.IntValue || inputReq.InputType == InputType.IntButton) && selectingButton.IsInteger)
				return selectingButton.Input.ToString();
			if (inputReq.InputType == InputType.StrValue || inputReq.InputType == InputType.StrButton)
				return selectingButton.Inputs;
			#endregion
			#region EE_INPUTANY
			if (inputReq.InputType == InputType.AnyValue && selectingButton.IsInteger)
				return selectingButton.Input.ToString();
			if (inputReq.InputType == InputType.AnyValue)
				return selectingButton.Inputs;
			#endregion
			return null;
		}
	}

	public async Task Initialize()
	{
		var boottimeDebugStopwatch = Stopwatch.StartNew();
		StreamWriter logWriter = null;
		try
		{
			if (Config.DisplayReport)
			{
				using var fs = new FileStream(Program.ExeDir + "time.log", FileMode.OpenOrCreate);
				logWriter = new StreamWriter(fs);
			}
		}
		catch
		{
			ParserMediator.Warn(trerror.TimeLogFileLocked.Text, null, 0);
		}
		logWriter?.WriteLine("Init:Start");
		logWriter?.WriteLine("File:Preload:Start");
		//必要なソースファイルを事前にメモリに一気に読み込む
		_genericTimerStopwatch.Restart();

		Preload.Clear();
		await Preload.Load(Program.ErbDir);
		await Preload.Load(Program.CsvDir);

		logWriter?.WriteLine("File:Preload:End " + boottimeDebugStopwatch.ElapsedMilliseconds + "ms");

		GlobalStatic.Console = this;
		// GlobalStatic.MainWindow = window;
		process = new GameProc.Process(this);
		GlobalStatic.Process = process;
		if (Program.DebugMode && Config.DebugShowWindow)
		{
			OpenDebugDialog();
			_uiAdapter.Focus();
		}
		ClearDisplay();
		if (!await process.Initialize(logWriter))
		{
			state = ConsoleState.Error;
			OutputLog(null, false);
			PrintFlush(false);
			RefreshStrings(true);
			return;
		}
		RunEmueraProgram("");
		RefreshStrings(true);

		logWriter?.WriteLine("Init:End " + boottimeDebugStopwatch.ElapsedMilliseconds + "ms");
	}


	public void Quit() { state = ConsoleState.Quit; }
	#region EE_FORCE_QUIT系
	public void ForceQuit()
	{

		if (GlobalStatic.ForceQuitAndRestart == true)
		{
#if HEADLESS
			Console.Error.WriteLine(trmb.ForceQuitAndRestart.Text);
			Program.rebootFlag = false;
			throw new CodeEE(trerror.ForceQuitAndRestartError.Text);
#else
			var result = MessageBox.Show(trmb.ForceQuitAndRestart.Text,
				"FORCE_QUIT_AND_RESTART",
				MessageBoxButtons.YesNo
				//System.Windows.MessageBoxIcon.None,
				//System.Windows.MessageBoxDefaultButton.Button1
				);
			if (result == DialogResult.Yes)
			{
				Program.rebootFlag = false;
				throw new CodeEE(trerror.ForceQuitAndRestartError.Text);
			}
#endif
		}
		if (Program.rebootFlag)
			_uiAdapter.Reboot();
		else
			_uiAdapter.ExitApplication();
		GlobalStatic.ForceQuitAndRestart = true;
		return;
	}
	#endregion

	public void ThrowTitleError(bool error)
	{
		state = ConsoleState.Error;
		notToTitle = true;
		byError = error;
	}
	public void ThrowError(bool playSound)
	{
#if !HEADLESS
		if (playSound)
			System.Media.SystemSounds.Hand.Play();
#endif
		forceUpdateGeneration();
		UseUserStyle = false;
		PrintFlush(false);
		RefreshStrings(false);
		state = ConsoleState.Error;
	}

	public bool notToTitle;
	public bool byError;
	//public ScriptPosition? ErrPos = null;

	#region button関連
	bool lastButtonIsInput = true;
	public bool updatedGeneration;
	int lastButtonGeneration;//最後に追加された選択肢の世代。これと世代が一致しない選択肢は選択できない。
	#region EE_BINPUT
	public int LastButtonGeneration { get { return lastButtonGeneration; } }
	#endregion
	int newButtonGeneration;//次に追加される選択肢の世代。Input又はInputsごとに増加
							//public int LastButtonGeneration { get { return lastButtonGeneration; } }
	public int NewButtonGeneration { get { return newButtonGeneration; } }
	public void UpdateGeneration() { lastButtonGeneration = newButtonGeneration; updatedGeneration = true; }
	public void forceUpdateGeneration() { newButtonGeneration++; lastButtonGeneration = newButtonGeneration; updatedGeneration = true; }
	LogicalLine lastInputLine;

	private void newGeneration()
	{
		//値の入力を求められない時は更新は必要ないはず
		if (state != ConsoleState.WaitInput || !inputReq.NeedValue)
			return;
		if (!updatedGeneration && process.getCurrentLine != lastInputLine)
		{
			//ボタン無しで次の入力に来たなら強制で世代更新
			lastButtonGeneration = newButtonGeneration;
		}
		else
			updatedGeneration = false;
		lastInputLine = process.getCurrentLine;
		#region EE_BINPUT
		switch (inputReq.InputType)
		{
			//古い選択肢を選択できないように。INPUTで使った選択肢をINPUTSには流用できないように。
			case InputType.IntValue:
			case InputType.IntButton:
				if (lastButtonGeneration == newButtonGeneration)
					unchecked { newButtonGeneration++; }
				else if (!lastButtonIsInput)
					lastButtonGeneration = newButtonGeneration;
				lastButtonIsInput = true;
				break;
			case InputType.StrValue:
			#region EE_INPUTANY
			case InputType.AnyValue:
			#endregion
			case InputType.StrButton:
				if (lastButtonGeneration == newButtonGeneration)
					unchecked { newButtonGeneration++; }
				else if (lastButtonIsInput)
					lastButtonGeneration = newButtonGeneration;
				lastButtonIsInput = false;
				break;
		}
		#endregion
	}

	/// <summary>
	/// 選択中のボタン。INPUTやINPUTSに対応したものでなければならない
	/// </summary>
	ConsoleButtonString selectingButton;
	ConsoleButtonString lastSelectingButton;
	public ConsoleButtonString SelectingButton { get { return selectingButton; } }
	public bool ButtonIsSelected(ConsoleButtonString button) { return selectingButton == button; }
	public bool ButtonIsPointing(ConsoleButtonString button) { return pointingStrings.Contains(button); }

	/// <summary>
	/// ToolTip表示したフラグ
	/// </summary>
	bool tooltipUsed;
	/// <summary>
	/// マウスの直下にあるテキスト。ボタンであってもよい。
	/// ToolTip表示用。世代無視、履歴中も表示
	/// </summary>
	ConsoleButtonString pointingString;
	// pointingStrings记录鼠标下所有Button图像。当多个Button重叠时，被鼠标划到的图像都会变化
	HashSet<ConsoleButtonString> pointingStrings = [];
	#region EE_MOUSEB
	public ConsoleButtonString PointingSring { get { return pointingString; } }
	#endregion
	ConsoleButtonString lastPointingString;
	#endregion

	#region Input & Timer系

	//bool hasDefValue = false;
	//Int64 defNum;
	//string defStr;

	public InputRequest inputReq;
	#region EE_INPUT第二引数修正
	public InputType NowInputType { get { return inputReq.InputType; } }
	#endregion
	public void Await(int time)
	{
		if (!Enabled || state != ConsoleState.Running)
		{
			Quit();
			return;
		}
		RefreshStrings(true);
		state = ConsoleState.Sleep;
		process.UpdateCheckInfiniteLoopState();
		_uiAdapter.ProcessEvents();
		if (time > 0)
			System.Threading.Thread.Sleep(time);
		////DoEvents()の間にウインドウが閉じられたらおしまい。
		//if (!Enabled || state != ConsoleState.Sleep)
		//{
		//	ReadAnyKey();
		//	return;
		//}

		state = ConsoleState.Running;
	}

	public void WaitInput(InputRequest req)
	{
		#region EE_AnchorのCB機能移植
		if (Config.CBUseClipboard)
			CBProc.Check(ClipboardProcessor.CBTriggers.InputWait);
		#endregion
		state = ConsoleState.WaitInput;
		inputReq = req;
		if (req.Timelimit > 0)
		{
			if (req.OneInput)
				_uiAdapter.UpdateLastInput();
			presetTimer();
			//				setTimer();
		}
		//updateMousePosition();
		//Point point = window.MainPicBox.PointToClient(Control.MousePosition);
		//if (window.MainPicBox.ClientRectangle.Contains(point))
		//{
		//	PrintFlush(false);
		//	MoveMouse(point);
		//}
	}

	public void ReadAnyKey(bool anykey = false, bool stopMesskip = false)
	{
		#region EE_AnchorのCB機能移植
		if (Config.CBUseClipboard)
			CBProc.Check(ClipboardProcessor.CBTriggers.AnyKeyWait);
		#endregion
		InputRequest req = new();
		if (!anykey)
			req.InputType = InputType.EnterKey;
		else
			req.InputType = InputType.AnyKey;
		req.StopMesskip = stopMesskip;
		inputReq = req;
		state = ConsoleState.WaitInput;
		process.NeedWaitToEventComEnd = false;
	}


#if !HEADLESS
	public void AddBackgroundImage(string name, long depth, float opacity)
	{
		var spr = AppContents.GetSprite(name) as SpriteF;
		if (spr == null)
		{
			return;
		}
		var bg = new ConsoleBackground(spr, opacity);
		var pair = new KeyValuePair<long, ConsoleBackground>(depth, bg);
		backgroundList.Add(pair);
		backgroundList.Sort((v1, v2) => (v1.Key >= v2.Key) ? -1 : 1);
		BakeBackground();
	}
#endif

#if !HEADLESS
	public void ClearBackgroundImage()
	{
		backgroundList.Clear();
		BakeBackground();
	}

	public void RemoveBackground(string key)
	{
		backgroundList.RemoveAt(backgroundList.FindIndex((v) => v.Value.bgImage.Name == key));
		BakeBackground();
	}
	public void ValidateBackground(int width, int height)
	{
		if (bakedBackground == null)
		{
			bakedBackground = new Bitmap(width, height);
			BakeBackground();
		}
		else if (bakedBackground.Width != width || bakedBackground.Height != height)
		{
			bakedBackground.Dispose();
			bakedBackground = new Bitmap(width, height);
			BakeBackground();
		}
	}
	private void BakeBackground()
	{
		if (bakedBackground == null)
		{
			return;
		}
		var graph = Graphics.FromImage(bakedBackground);
		graph.Clear(Color.Transparent);
		foreach (var pair in backgroundList)
		{
			var bg = pair.Value.bgImage;
			var scaleW = bakedBackground.Width / (float)bg.BaseImage.Bitmap.Width;
			var scaleH = bakedBackground.Height / (float)bg.BaseImage.Bitmap.Height;
			var cropHorizontally = bg.BaseImage.Bitmap.Height * scaleW < bakedBackground.Height;
			var newWidth = bg.BaseImage.Bitmap.Width * (cropHorizontally ? scaleH : scaleW);
			var newHeight = bg.BaseImage.Bitmap.Height * (cropHorizontally ? scaleH : scaleW);
			var paddingX = (int)((bakedBackground.Width - newWidth) / 2);
			var attributes = new ImageAttributes();
			attributes.SetColorMatrix(pair.Value.GetColorMatrix(), ColorMatrixFlag.Default, ColorAdjustType.Bitmap);
			bg.GraphicsDraw(graph, new Rectangle(paddingX, 0, (int)newWidth, (int)newHeight), attributes);
		}
	}
#else
	public void ClearBackgroundImage() { }
	public void RemoveBackground(string key) { }
	public void ValidateBackground(int width, int height) { }
#endif
	/// <summary>
	/// INPUT中のアニメーション用タイマー
	/// </summary>
	System.Timers.Timer redrawTimer;

	private void tickRedrawTimer(object sender, System.Timers.ElapsedEventArgs e)
	{
		if (!redrawTimer.Enabled)
			return;
		//INPUT待ちでないとき、又はタイマー付きINPUT状態の場合はこれ以外の処理に任せる
		if (state != ConsoleState.WaitInput || genericTimer.Enabled)
		{
			return;
		}
		_uiAdapter.Refresh();//OnPaint発行
	}

	/// <summary>
	/// アニメーション用タイマーの設定。0以下の値を指定するとタイマー停止
	/// </summary>
	public void setRedrawTimer(int tickcount)
	{
		if (tickcount <= 0)
		{
			redrawTimer.Enabled = false;
			return;
		}
		if (tickcount < 10)
			tickcount = 10;
		redrawTimer.Interval = tickcount;
		redrawTimer.Enabled = true;
	}



	System.Timers.Timer genericTimer = new();
	long timerID = -1;
	readonly Stopwatch _genericTimerStopwatch = new();//現在のタイマーを開始した時のミリ秒数（WinmmTimer.TickCount基準）
	long timer_endTime;//現在のタイマーを終了する時のTickCountミリ秒数
	bool isTimeout;
	long timeDisplayCount;
	bool inputed;
	public bool IsTimeOut { get { return isTimeout; } }

	/// <summary>
	/// 1824 TINPUT時に直接タイマーをセットせずに最初の再描画が終わってからタイマーをセットする（そうしないとTINPUTと再描画だけでループしてしまうので）
	/// </summary>
	bool need_settimer;

	private void presetTimer()
	{
		need_settimer = true;
		if (inputReq.DisplayTime)
		{
			var remainingMs = inputReq.Timelimit - _genericTimerStopwatch.ElapsedMilliseconds;
			PrintSingleLine(trsl.Remaining.Text + $"{remainingMs / 1000.0f:0.0}");
			timeDisplayCount = 0;
			inputed = false;
		}
	}
	private void setTimer()
	{
		isTimeout = false;
		timerID = inputReq.ID;
#if !HEADLESS
		genericTimer.Enabled = true;
#endif
		_genericTimerStopwatch.Restart();
		timer_endTime = inputReq.Timelimit;
		need_settimer = false;
	}

	//汎用
	private void tickTimer(object sender, EventArgs e)
	{
		if (!genericTimer.Enabled)
			return;
		if (state != ConsoleState.WaitInput || inputReq.Timelimit <= 0 || timerID != inputReq.ID)
		{
			stopTimer();
			return;
		}
		var elapsedMs = _genericTimerStopwatch.ElapsedMilliseconds;
		if (elapsedMs >= timer_endTime)
		{
			endTimer();
			return;
		}

		if (inputReq.DisplayTime)
			{
				var remainingMs = inputReq.Timelimit - _genericTimerStopwatch.ElapsedMilliseconds;
				timeDisplayCount++;
				if (timeDisplayCount%10 == 0 && !inputed)
					_uiAdapter.Invoke(() => changeLastLine(trsl.Remaining.Text + $"{remainingMs / 1000.0f:0.0}"));
			}
	}

	private void stopTimer()
	{
		//if (state == ConsoleState.WaitKeyWithTimerF && countTime < timeLimit)
		//{
		//	wait_timeout = true;
		//	while (countTime < timeLimit)
		//	{
		//		_uiAdapter.ProcessEvents();
		//	}
		//	wait_timeout = false;
		//}
		genericTimer.Enabled = false;
		//timer.Dispose();
	}

	/// <summary>
	/// tickTimerからのみ呼ぶ
	/// </summary>
	private void endTimer()
	{
		EndTimerCore();
	}

	/// <summary>
	/// endTimer() 的核心逻辑，HEADLESS 和 WinForms 共用。
	/// </summary>
	private void EndTimerCore()
	{
		stopTimer();
		isTimeout = true;
		if (IsWaitingPrimitive)
		{
			//callEmueraProgramは呼び出し先で行う。
			#region EE_INPUTMOUSEKEY拡張
			// InputMouseKey(4, 0, 0, 0, 0);
			InputMouseKey(4, 0, 0, 0, 0, 0);
#if !HEADLESS
			if (state == ConsoleState.WaitInput && inputReq.NeedValue)
			{
				Point point = _uiAdapter.MainPicBox.PointToClient(_uiAdapter.GetCursorPosition());
				if (_uiAdapter.MainPicBox.ClientRectangle.Contains(point))
					MoveMouse(point);
			}
#endif
			RefreshStrings(true);
			#endregion
			return;
		}
		if (inputReq.DisplayTime)
			changeLastLine(inputReq.TimeUpMes);
		else if (inputReq.TimeUpMes != null)
			PrintSingleLine(inputReq.TimeUpMes);
#if !HEADLESS
		_uiAdapter.Invoke(() =>
		{
			RunEmueraProgram("");//ディフォルト入力の処理はcallEmueraProgram側で
			if (state == ConsoleState.WaitInput && inputReq.NeedValue)
			{
				_uiAdapter.Invoke(() =>
				{
					Point point = _uiAdapter.MainPicBox.PointToClient(_uiAdapter.GetCursorPosition());
					if (_uiAdapter.MainPicBox.ClientRectangle.Contains(point))
						MoveMouse(point);
				});
			}
			RefreshStrings(true);
		});
#else
		RunEmueraProgram("");
		if (state == ConsoleState.WaitInput && inputReq.NeedValue)
		{
			Point point = _uiAdapter.MainPicBox.PointToClient(_uiAdapter.GetCursorPosition());
			if (_uiAdapter.MainPicBox.ClientRectangle.Contains(point))
				MoveMouse(point);
		}
		RefreshStrings(true);
#endif
	}

	public void forceStopTimer()
	{
		if (genericTimer.Enabled)
		{
			genericTimer.Enabled = false;
		}
	}

#if HEADLESS
	/// <summary>
	/// HEADLESS 下 TINPUT 超时提交，等价于原 endTimer() 的核心逻辑。
	/// </summary>
	internal void SubmitTimeout()
	{
		if (state != ConsoleState.WaitInput || inputReq == null || inputReq.Timelimit <= 0)
			return;

		EndTimerCore();
	}

	/// <summary>
	/// HEADLESS 下获取当前 TINPUT 请求的剩余超时毫秒数。
	/// 非 TINPUT 请求或已超时返回 null。
	/// 剩余时间 &lt;= 0 时返回 0。
	/// </summary>
	internal long? InputTimeoutMs
	{
		get
		{
			if (state != ConsoleState.WaitInput || inputReq == null || inputReq.Timelimit <= 0)
				return null;

			var remaining = inputReq.Timelimit - _genericTimerStopwatch.ElapsedMilliseconds;
			return remaining <= 0 ? 0 : remaining;
		}
	}
#endif
	#endregion

	#region Call系
	/// <summary>
	/// スクリプト実行。RefreshStringsはしないので呼び出し側がすること
	/// </summary>
	/// <param name="input"></param>
	private void RunEmueraProgram(string input)
	{
		//入力文字列の表示処理を行わない場合はstr == null
		if (input != null)
		{
			//INPUT文字列をPRINTする処理など
			if (!doInputToEmueraProgram(input))
				return;
			if (state == ConsoleState.Error)
				return;
		}
		state = ConsoleState.Running;
		process.DoScript();
		if (state == ConsoleState.Running)
		{//RunningならProcessは処理を継続するべき
			state = ConsoleState.Error;
			PrintError(trerror.ProgramStatusError.Text);
		}
		#region EE_OUTPUTLOG
		if (state == ConsoleState.Error && !noOutputLog)
			//OutputLog(Program.ExeDir + "emuera.log");
			OutputSystemLog(Program.ExeDir + "emuera.log");
		#endregion

		PrintFlush(false);
		//1819 Refreshは呼び出し側で行う
		//RefreshStrings(false);
		newGeneration();
	}

	private bool doInputToEmueraProgram(string str)
	{
		if (state == ConsoleState.WaitInput)
		{
			long inputValue;
			List<AConsoleDisplayNode> ep;

			switch (inputReq.InputType)
			{
				case InputType.IntValue:
					if (string.IsNullOrEmpty(str) && inputReq.HasDefValue && !IsRunningTimer)
					{
						inputValue = inputReq.DefIntValue;
						str = inputValue.ToString();
					}
					else if (!long.TryParse(str, out inputValue))
						return false;
					if (inputReq.IsSystemInput)
						process.InputSystemInteger(inputValue);
					else
						process.InputInteger(inputValue);
					break;
				#region EE_BINPUT
				case InputType.IntButton:
					if (string.IsNullOrEmpty(str) && inputReq.HasDefValue && !IsRunningTimer)
					{
						inputValue = inputReq.DefIntValue;
						str = inputValue.ToString();
					}
					else if (!long.TryParse(str, out inputValue))
						return false;
					foreach (ConsoleDisplayLine line in Enumerable.Reverse(displayLineList).ToList())
					{

						foreach (ConsoleButtonString button in line.Buttons)
						{
							if (button.IsInteger && button.Generation == lastButtonGeneration && button.Input == inputValue)
							{
								process.InputInteger(inputValue);
								goto loopendint;
							}
							//後ろから回してるので世代が違うボタンに到達したらもう無い
							else if (button.Generation != 0 && button.Generation != lastButtonGeneration)
								goto loopepint;
						}
					}
				loopepint:
					foreach (var value in escapedParts)
					{
						ep = value.Value;
						foreach (var part in ep)
						{
							if (part is ConsoleDivPart div)
							{
								foreach (ConsoleDisplayLine line in Enumerable.Reverse(div.Children).ToList())
								{
									foreach (ConsoleButtonString button in line.Buttons)
									{
										if (button.IsInteger && button.Input == inputValue)
										{
											process.InputInteger(inputValue);
											goto loopendint;
										}
									}
								}
							}
						}
					}
					return false;
				loopendint:
					break;
				#endregion
				case InputType.StrValue:
					if (string.IsNullOrEmpty(str) && inputReq.HasDefValue && !IsRunningTimer)
						str = inputReq.DefStrValue;
					//空入力と時間切れ
					if (str == null)
						str = "";
					//SHOP等の数値を求められる場面用にFLOWINPUTでINPUTSにしててもRESULTを処理する
					if (inputReq.IsSystemInput)
						process.InputSystemInteger(inputReq.DefIntValue);
					process.InputString(str);
					break;
				#region EE_BINPUT
				case InputType.StrButton:
					if (string.IsNullOrEmpty(str) && inputReq.HasDefValue && !IsRunningTimer)
						str = inputReq.DefStrValue;
					//空入力と時間切れ
					if (str == null)
						str = "";
					foreach (ConsoleDisplayLine line in Enumerable.Reverse(displayLineList).ToList())
					{
						foreach (ConsoleButtonString button in line.Buttons)
						{
							if (button.Generation == lastButtonGeneration && ((button.IsInteger && button.Input.ToString() == str) || button.Inputs == str))
							{
								process.InputString(str);
								goto loopendstr;
							}
							//後ろから回してるので世代が違うボタンに到達したらもう無い
							else if (button.Generation != 0 && button.Generation != lastButtonGeneration)
								goto loopepstr;
						}
					}
				loopepstr:
					foreach (var value in escapedParts)
					{
						ep = value.Value;

						foreach (var part in ep)
						{
							if (part is ConsoleDivPart div)
							{
								foreach (ConsoleDisplayLine line in Enumerable.Reverse(div.Children).ToList())
								{
									foreach (ConsoleButtonString button in line.Buttons)
									{
										if ((button.IsInteger && button.Input.ToString() == str) || button.Inputs == str)
										{
											process.InputString(str);
											goto loopendint;
										}
									}
								}
							}
						}
					}
					return false;
				loopendstr:
					break;
				#endregion
				#region EE_INPUTANY
				case InputType.AnyValue:
					if (long.TryParse(str, out inputValue))
					{
						if (inputReq.IsSystemInput)
							process.InputSystemInteger(inputValue);
						else
							process.InputInteger(inputValue);
					}
					else
					{
						process.InputString(str);
					}
					break;
					#endregion

			}
			stopTimer();
		}
		Print(str);
		inputed = true;
		PrintFlush(false);
		#region EM_textbox位置指定拡張
		// 入力成功した
			if (_uiAdapter.TextBoxPosChanged)
				_uiAdapter.ResetTextBoxPos();
		#endregion
		return true;
	}
	#endregion

	#region 入力系
	readonly string[] spliter = ["\\n", "\r\n", "\n", "\r"];//本物の改行コードが来ることは無いはずだけど一応

	public bool MesSkip;
	private bool inProcess;
	volatile public bool KillMacro;

	internal void MouseWheel(Point point, int delta)
	{
		if (!IsWaitingPrimitive)
			return;
		//pointはクライアント左上基準の座標。
		//clientPointをクライアント左下基準の座標に置き換え
		Point clientPoint = point;
		clientPoint.Y = point.Y - ClientHeight;
		#region EE_INPUTMOUSEKEY拡張
		// InputMouseKey(2, delta, clientPoint.X, clientPoint.Y, 0);
		InputMouseKey(2, delta, clientPoint.X, clientPoint.Y, 0, 0);
		#endregion
	}

#if !HEADLESS
	internal void MouseDown(Point point, MouseButtons button)
	{
		if (!IsWaitingPrimitive)
			return;
		//pointはクライアント左上基準の座標。
		//clientPointをクライアント左下基準の座標に置き換え
		Point clientPoint = point;
		clientPoint.Y = point.Y - ClientHeight;
		int buttonNum = -1;
		if (cbgButtonMap != null && cbgButtonMap.IsCreated)
		{
			//マップ画像の左上基準の座標に置き換え
			Point mapPoint = clientPoint;
			mapPoint.Y = clientPoint.Y + cbgButtonMap.Height;
			if (mapPoint.X >= 0 && mapPoint.Y >= 0 && mapPoint.X < cbgButtonMap.Width && mapPoint.Y < cbgButtonMap.Height)
			{
				Color c = cbgButtonMap.Bitmap.GetPixel(mapPoint.X, mapPoint.Y);
				if (c.A == 255)
				{
					buttonNum = c.ToArgb() & 0xFFFFFF;
				}
			}

		}
		#region EE_INPUTMOUSEKEY拡張
		// InputMouseKey(1, (int)button, clientPoint.X, clientPoint.Y, buttonNum);
		//ボタン押された場合にRESULT:5にボタンの値が代入される
		if (selectingButton != null)
		{
			// マスク色をRESULT:6にボタンの値が代入される
			if (!selectingButton.IsInteger)
			{
				GlobalStatic.VEvaluator.RESULTS = selectingButton.Inputs;
				InputMouseKey(1, (int)button, clientPoint.X, clientPoint.Y, buttonNum, 0);
			}
			else
			{
				InputMouseKey(1, (int)button, clientPoint.X, clientPoint.Y, buttonNum, selectingButton.Input);
			}
		}
		else
		{
			InputMouseKey(1, (int)button, clientPoint.X, clientPoint.Y, buttonNum, 0);
		}
		#endregion
	}

	//1823 Key入力を捕まえる
	internal void PressPrimitiveKey(Keys keycode, Keys keydata, Keys keymod)
	{
		if (IsWaitingPrimitive)
			#region EE_INPUTMOUSEKEY拡張
			// InputMouseKey(3, (int)keycode, (int)keydata, 0, 0);
			InputMouseKey(3, (int)keycode, (int)keydata, 0, 0, 0);
		#endregion
	}

#endif // !HEADLESS

	//1823 Key入力を捕まえる
	#region EE_INPUTMOUSEKEY拡張
	//internal void InputMouseKey(int type, int result1, int result2, int result3, int result4)
	internal void InputMouseKey(int type, int result1, int result2, int result3, int result4, long result5)
	{
		// emuera.InputResult5(type, result1, result2, result3, result4);
		process.InputResult5(type, result1, result2, result3, result4, result5);
		inProcess = true;
		try
		{
			//1823 Escキーもマクロも右クリックも不可。単純に押されたキーを送るのみ。
			RunEmueraProgram(null);
			if (state == ConsoleState.WaitInput && inputReq.NeedValue)
			{
				Point point = _uiAdapter.MainPicBox.PointToClient(_uiAdapter.GetCursorPosition());
				if (_uiAdapter.MainPicBox.ClientRectangle.Contains(point))
					MoveMouse(point);
			}
		}
		finally
		{
			inProcess = false;
		}
		RefreshStrings(true);
	}
	#endregion

	public void PressEnterKey(bool keySkip, string input, bool changedByMouse)
	{
		MesSkip = keySkip;
		if ((state == ConsoleState.Running) || (state == ConsoleState.Initializing))
			return;
		else if (state == ConsoleState.Quit)
		{
			if (Program.rebootFlag)
				_uiAdapter.Reboot();
			else
				_uiAdapter.Close();
			return;
		}
		else if (state == ConsoleState.Error)
		{
			if (Program.DebugMode)
               return;
			if (input == ErrorButtonsText && selectingButton != null && selectingButton.ErrPos != null)
			{
				OpenErrorFile(selectingButton.ErrPos);
				return;
			}
			_uiAdapter.Close();
			return;
		}
#if DEBUG
		if (state != ConsoleState.WaitInput || inputReq == null)
			throw new ExeEE("");
#endif
		KillMacro = false;
		try
		{
			string[] text;
			if (changedByMouse)//1823 マウスによって入力されたならマクロ解析を行わない
			{ text = [input]; }
			else
			{
				//INPUTSでも"@"のみが弾かれないようにおまじない
				if (input.Length > 1 && !inputReq.OneInput && input.StartsWith('@'))
				{
					doSystemCommand(input);
					return;
				}
				if (inputReq.InputType == InputType.Void)
					return;
				if (genericTimer.Enabled &&
						(inputReq.InputType == InputType.AnyKey || inputReq.InputType == InputType.EnterKey))
					stopTimer();
				//if((inputReq.InputType == InputType.IntValue || inputReq.InputType == InputType.StrValue)
				if (input.Contains('(', StringComparison.Ordinal))
					input = parseInput(new CharStream(input), false);
				text = input.Split(spliter, StringSplitOptions.None);
			}

			inProcess = true;
			for (int i = 0; i < text.Length; i++)
			{
				string inputs = text[i];
				if (inputs.Contains("\\e", StringComparison.Ordinal))
				{
					inputs = inputs.Replace("\\e", "", StringComparison.Ordinal);//\eの除去
					MesSkip = true;
				}

				if (inputReq.OneInput && (!Config.AllowLongInputByMouse || !changedByMouse) && inputs.Length > 1)
					inputs = inputs.Remove(1);
				//1819 TODO:入力無効系（強制待ちTWAIT）でスキップとマクロを止めるかそのままか
				//現在はそのまま。強制待ち中はスキップの開始もできないのにスキップ中なら飛ばせる。
				if (inputReq.InputType == InputType.Void)
				{
					i--;
					inputs = "";
				}
				RunEmueraProgram(inputs);
				RefreshStrings(false);
				while (MesSkip && state == ConsoleState.WaitInput)
				{
					//TODO:入力無効を通していいか？スキップ停止をマクロでは飛ばせていいのか？
					if (inputReq.NeedValue)
						break;
					if (inputReq.StopMesskip)
						break;
					RunEmueraProgram("");
					RefreshStrings(false);
					//EscがマクロストップかつEscがスキップ開始だからEscでスキップを止められても即開始しちゃったりするからあんまり意味ないよね
					//if (KillMacro)
					//	goto endMacro;
				}
				MesSkip = false;
				if (state != ConsoleState.WaitInput)
					break;
				//マクロループ時は待ち処理が起こらないのでここでシステムキューを捌く
				_uiAdapter.ProcessEvents();
#if DEBUG
				if (state != ConsoleState.WaitInput || inputReq == null)
					throw new ExeEE("");
#endif
				if (KillMacro)
				{
					endMacro();
					return;
				}
			}
		}
		finally
		{
			inProcess = false;
		}
		endMacro();

		void endMacro()
		{
			if (state == ConsoleState.WaitInput && inputReq.NeedValue)
			{
				Point point = _uiAdapter.MainPicBox.PointToClient(_uiAdapter.GetCursorPosition());
				if (_uiAdapter.MainPicBox.ClientRectangle.Contains(point))
					MoveMouse(point);
			}
			RefreshStrings(true);
		}
	}

	private void OpenErrorFile(ScriptPosition? pos)
	{
		ProcessStartInfo pInfo = new()
		{
			FileName = Config.TextEditor
		};
		var ignoreCaseCmp = StringComparison.OrdinalIgnoreCase;
		string fname = pos.Value.Filename.ToUpper();
		if (fname.EndsWith(".CSV", ignoreCaseCmp))
		{
			if (fname.Contains(Program.CsvDir, ignoreCaseCmp))
				fname = fname.Replace(Program.CsvDir, "", ignoreCaseCmp);
			fname = Program.CsvDir + fname;
		}
		else
		{
			//解析モードの場合は見ているファイルがERB\の下にあるとは限らないかつフルパスを持っているのでこの補正はしなくてよい
			if (!Program.AnalysisMode)
			{
				if (fname.Contains(Program.ErbDir, ignoreCaseCmp))
					fname = fname.Replace(Program.ErbDir, "", ignoreCaseCmp);
				fname = Path.Combine(Program.ErbDir + fname);
			}
		}
		switch (Config.EditorType)
		{
			case TextEditorType.SAKURA:
				pInfo.Arguments = "-Y=" + pos.Value.LineNo.ToString() + " \"" + fname + "\"";
				break;
			case TextEditorType.TERAPAD:
				pInfo.Arguments = "/jl=" + pos.Value.LineNo.ToString() + " \"" + fname + "\"";
				break;
			case TextEditorType.EMEDITOR:
				pInfo.Arguments = "/l " + pos.Value.LineNo.ToString() + " \"" + fname + "\"";
				break;
			case TextEditorType.USER_SETTING:
				if (!string.IsNullOrEmpty(Config.EditorArg) && Config.EditorArg != null)
					pInfo.Arguments = Config.EditorArg + pos.Value.LineNo.ToString() + " \"" + fname + "\"";
				else
					pInfo.Arguments = fname;
				break;
		}
		try
		{
			Process.Start(pInfo);
		}
		catch (System.ComponentModel.Win32Exception)
		{
#if !HEADLESS
			System.Media.SystemSounds.Hand.Play();
#endif
			PrintError(trerror.FailedOpenEditor.Text);
			forceUpdateGeneration();
		}
		return;
	}

	static string parseInput(CharStream st, bool isNest)
	{
		StringBuilder sb = new(20);
		StringBuilder num = new(20);
		bool hasRet = false;
		int res;
		while (!st.EOS && (!isNest || st.Current != ')'))
		{
			if (st.Current == '(')
			{
				st.ShiftNext();
				string tstr = parseInput(st, true);

				if (!st.EOS)
				{
					st.ShiftNext();
					if (st.Current == '*')
					{
						st.ShiftNext();
						while (char.IsNumber(st.Current))
						{
							num.Append(st.Current);
							st.ShiftNext();
						}
						if (num.ToString() != "" && num.ToString() != null)
						{
							int.TryParse(num.ToString(), out res);
							for (int i = 0; i < res; i++)
								sb.Append(tstr);
							num.Remove(0, num.Length);
						}
					}
					else
						sb.Append(tstr);
					continue;
				}
				else
				{
					sb.Append(tstr);
					break;
				}
			}
			else if (st.Current == '\\')
			{
				st.ShiftNext();
				switch (st.Current)
				{
					case 'n':
						if (!hasRet)
							sb.Append('\n');
						else
							hasRet = false;
						break;
					case 'r':
						sb.Append('\r');
						break;
					case 'e':
						sb.Append("\\e\n");
						hasRet = true;
						break;
					case '\n':
						break;
					default:
						sb.Append(st.Current);
						break;
				}
			}
			else
				sb.Append(st.Current);
			st.ShiftNext();
		}
		return sb.ToString();
	}


	bool runningERBfromMemory;
	/// <summary>
	/// 通常コンソールからのDebugコマンド、及びデバッグウインドウの変数ウォッチなど、
	/// *.ERBファイルが存在しないスクリプトを実行中
	/// 1750 IsDebugから改名
	/// </summary>
	public bool RunERBFromMemory { get { return runningERBfromMemory; } set { runningERBfromMemory = value; } }
	void doSystemCommand(string command)
	{
		if (genericTimer.Enabled)
		{
			PrintError(trerror.CanNotInputTimerWait.Text);
			PrintError("");//タイマー表示処理に消されちゃうかもしれないので
			RefreshStrings(true);
			return;
		}
		if (IsInProcess)
		{
			PrintError(trerror.CanNotInputScriptRunning.Text);
			RefreshStrings(true);
			return;
		}
		StringComparison sc = Config.StringComparison;
		Print(command);
		PrintFlush(false);
		RefreshStrings(true);
		string com = command[1..];
		if (com.Length == 0)
			return;
		if (com.Equals("REBOOT", sc))
		{
			_uiAdapter.Reboot();
			return;
		}
		else if (com.Equals("OUTPUT", sc) || com.Equals("OUTPUTLOG", sc))
		{
			#region EE_OUTPUTLOG
			// this.OutputLog(Program.ExeDir + "emuera.log");
			OutputSystemLog(Program.ExeDir + "emuera.log");
			#endregion

			return;
		}
		else if (com.Equals("QUIT", sc) || com.Equals("EXIT", sc))
		{
			_uiAdapter.Close();
			return;
		}
		else if (com.Equals("CONFIG", sc))
		{
			_uiAdapter.ShowConfigDialog();
			return;
		}
		else if (com.Equals("DEBUG", sc))
		{
			if (!Program.DebugMode)
			{
				PrintError(trerror.CanNotUseDebugWindow.Text);
				RefreshStrings(true);
				return;
			}
			OpenDebugDialog();
		}
		else
		{
			if (!Config.UseDebugCommand)
			{
				PrintError(trerror.CanNotUseDebugCommand.Text);
				RefreshStrings(true);
				return;
			}
			//処理をDebugMode系へ移動
			DebugCommand(com, Config.ChangeMasterNameIfDebug, false);
			PrintFlush(false);
		}
		RefreshStrings(true);
	}
	#endregion

	#region 描画系
	Stopwatch _frameDeltaTimer = Stopwatch.StartNew();
	uint msPerFrame = 1000 / 60;//60FPS
	ConsoleRedraw redraw = ConsoleRedraw.Normal;
	public ConsoleRedraw Redraw { get { return redraw; } }
	public void SetRedraw(long i)
	{
		if ((i & 1) == 0)
			redraw = ConsoleRedraw.None;
		else
			redraw = ConsoleRedraw.Normal;
		if ((i & 2) != 0)
			RefreshStrings(true);
	}

	string debugTitle;
	public void SetWindowTitle(string str)
	{
		if (Program.DebugMode)
		{
			debugTitle = str;
			_uiAdapter.Text = str + " (Debug Mode)";
		}
		else
			_uiAdapter.Text = str;
	}

	public void SetEmueraVersionInfo(string str)
	{
		_uiAdapter.TextBox.Text = str;
	}
	public string GetWindowTitle()
	{
		if (Program.DebugMode && debugTitle != null)
			return debugTitle;
		return _uiAdapter.Text;
	}

	/// <summary>
	/// 1818以前のRefreshStringsからselectingButton部分を抽出
	/// ここでOnPaintを発行
	/// </summary>
	public void RefreshStrings(bool force_Paint)
	{
		bool isBackLog = _uiAdapter.ScrollBar.Value != _uiAdapter.ScrollBar.Maximum;
		//ログ表示はREDRAWの設定に関係なく行うようにする
		if ((redraw == ConsoleRedraw.None) && (!force_Paint) && (!isBackLog))
			return;
		//選択中ボタンの適性チェック
		if (selectingButton != null)
		{
			//履歴表示中は選択肢無効→画面外に出てしまったボタンも履歴から選択できるように
			//if (isBackLog)
			//	selectingButton = null;
			//数値か文字列の入力待ち状態でなければ無効
			if (state != ConsoleState.Error && state != ConsoleState.WaitInput)
				selectingButton = null;
			else if ((state == ConsoleState.WaitInput) && !inputReq.NeedValue)
				selectingButton = null;
			//選択肢が最新でないなら無効
			else if (selectingButton.Generation != lastButtonGeneration)
				selectingButton = null;
		}
		if (!force_Paint)
		{//forceならば確実に再描画。
		 //履歴表示中でなく、最終行を表示済みであり、選択中ボタンが変更されていないなら更新不要
			if ((!isBackLog) && (lastDrawnLineNo == lineNo) && (lastSelectingButton == selectingButton))
				return;
			//まだ書き換えるタイミングでないなら次の更新を待ってみる
			//ただし、入力待ちなど、しばらく更新のタイミングがない場合には強制的に書き換えてみる
			if (_frameDeltaTimer.ElapsedMilliseconds < msPerFrame && (state == ConsoleState.Running || state == ConsoleState.Initializing))
				return;
		}
		if (forceTextBoxColor)
		{
			var sec = _genericTimerStopwatch.ElapsedMilliseconds;
			//色変化が速くなりすぎないように一定時間以内の再呼び出しは強制待ちにする
			if (_drawStopwatch == null)
			{
				_drawStopwatch = Stopwatch.StartNew();
			}
			else
			{
				while (_drawStopwatch.ElapsedMilliseconds < msPerFrame)
				{
					_uiAdapter.ProcessEvents();
				}
			}
			_uiAdapter.TextBox.BackColor = bgColor;

			_drawStopwatch.Restart();
		}
#if HEADLESS
		// HEADLESS 下 OnPaint 不执行，need_settimer 清零逻辑需要在此处执行
		if (need_settimer)
		{
			need_settimer = false;
			setTimer();
		}
#endif
		_uiAdapter.Invoke(() =>
		{
			verticalScrollBarUpdate();
			_uiAdapter.Refresh();//OnPaint発行
		});
	}

	#region EM_私家版_描画拡張
	Dictionary<int, List<AConsoleDisplayNode>> escapedParts;
	#endregion
	#region EE_BINPUT
	public Dictionary<int, List<AConsoleDisplayNode>> EscapedParts { get { return escapedParts; } }
	#endregion
	#region EM_私家版_imgマースク
	public int GetLinePointY(int lineNo)
	{
		int pointY = _uiAdapter.MainPicBox.Height - Config.LineHeight;
		int bottomLineNo = _uiAdapter.ScrollBar.Value - 1;
		if (displayLineList.Count - 1 < bottomLineNo)
			bottomLineNo = displayLineList.Count - 1;//1820 この処理不要な気がするけどエラー報告があったので入れとく
		pointY -= (bottomLineNo - lineNo) * Config.LineHeight;
		return pointY;
	}
	#endregion

	List<ConsoleDisplayLine> _htmlElementList = new(10);

	/// <summary>
	/// 1818以前のRefreshStringsの後半とm_RefreshStringsを融合
	/// 全面Clear法のみにしたのでさっぱりした。ダブルバッファリングはOnPaintが勝手にやるはず
	/// </summary>
	/// <param name="graph"></param>
	public void OnPaint(Graphics graph)
	{
#if HEADLESS
		return;
#else
		//デバッグ用。描画が超重い環境を想定1
		//System.Threading.Thread.Sleep(100);

		//描画中にEmueraが閉じられると廃棄されたPictureBoxにアクセスしてしまったりするので
		//OnPaintからgraphをもらった直後だから大丈夫だとは思うけど一応
		if (!Enabled)
			return;

		//1824 アニメスプライト用・現在フレームの時間を決定
		_frameDeltaTimer.Restart();

		bool isBackLog = _uiAdapter.ScrollBar.Value != _uiAdapter.ScrollBar.Maximum;
		int pointY = _uiAdapter.MainPicBox.Height - Config.LineHeight;


		int bottomLineNo = _uiAdapter.ScrollBar.Value - 1;
		int topLineNo = bottomLineNo - (pointY / Config.LineHeight + 1);
		if (topLineNo < 0)
			topLineNo = 0;
		pointY -= (bottomLineNo - topLineNo) * Config.LineHeight;
		if (Config.TextDrawingMode == TextDrawingMode.WINAPI)
		{
		}
		else
		{
			ValidateBackground((int)graph.ClipBounds.Width, (int)graph.ClipBounds.Height);
			graph.Clear(bgColor);
			if (bakedBackground != null)
			{
				graph.DrawImage(bakedBackground, 0, 0);
			}

			//1823 cbg追加
			#region EM_私家版_描画拡張
			if (escapedParts == null) escapedParts = [];
			if (ConsoleEscapedParts.Changed || !ConsoleEscapedParts.TestedInRange(topLineNo, bottomLineNo, lastButtonGeneration))
				ConsoleEscapedParts.GetPartsInRange(topLineNo, bottomLineNo, lastButtonGeneration, escapedParts);
			var edepth = escapedParts.Keys.ToArray();
			Array.Sort(edepth, (int a, int b) => -a.CompareTo(b));
			int eidx = 0, cidx = 0;
			int topPointY = pointY;
			while (eidx < edepth.Length || cidx < cbgList.Count)
			{
				var depth = Math.Max(eidx < edepth.Length ? edepth[eidx] : int.MinValue,
					cidx < cbgList.Count ? cbgList[cidx].zdepth : int.MinValue);
				if (cidx < cbgList.Count && cbgList[cidx].zdepth == depth)
				{
					// 先にCBGを描画
					ASprite img = cbgList[cidx].Img;
					if (cbgList[cidx].isButton && cbgList[cidx].buttonValue == selectingCBGButtonInt)
						img = cbgList[cidx].ImgB;
					if (img != null && img.IsCreated)
						img.GraphicsDraw(graph, new Point(cbgList[cidx].x, cbgList[cidx].y + _uiAdapter.MainPicBox.Height - img.DestBaseSize.Height));
					cidx++;
				}
				if (depth == 0)
				{
					// 普通のパーツを描画
					for (int i = topLineNo;
					i <= bottomLineNo &&
					i < displayLineList.Count;//何処かで非同期にDisplayLineListを触ってるやつがいる気がする...
					i++)
					{
						displayLineList[i].DrawTo(graph, pointY, isBackLog, true, Config.TextDrawingMode);
						pointY += Config.LineHeight;
					}
				}
				if (eidx < edepth.Length && edepth[eidx] == depth)
				{
					// 行範囲を超えたパーツを描画
					foreach (var p in escapedParts[edepth[eidx]])
					{
						var baseLineNo = p.Parent.ParentLine.LineNo;
						if (GlobalStatic.Console?.GetLineNo > Config.MaxLog)
						{
							var correction = GlobalStatic.Console.GetLineNo - Config.MaxLog;
							baseLineNo -= correction;
						}
						p.Parent.DrawPartTo(graph, p, topPointY + (baseLineNo - topLineNo) * Config.LineHeight, isBackLog, Config.TextDrawingMode);
					}
					eidx++;
				}
			}
			#endregion
			//for (int j = 0; j < cbgList.Count; j++)
			//{
			//	if (cbgList[j].zdepth == 0)
			//	{
			//		//1823以前の文字列描画
			//		for (int i = topLineNo; i <= bottomLineNo; i++)
			//		{
			//			displayLineList[i].DrawTo(graph, pointY, isBackLog, true, Config.TextDrawingMode);
			//			pointY += Config.LineHeight;
			//		}
			//		continue;
			//	}
			//	ASprite img = cbgList[j].Img;
			//	if (cbgList[j].isButton && cbgList[j].buttonValue == selectingCBGButtonInt)
			//		img = cbgList[j].ImgB;
			//	if (img == null || !img.IsCreated)
			//		continue;
			//	img.GraphicsDraw(graph, new Point(cbgList[j].x, cbgList[j].y + window.MainPicBox.Height - img.DestBaseSize.Height));
			//	//Bitmap bmp = img.Bitmap;
			//	//graph.DrawImage(bmp,
			//	//	new Rectangle(cbgList[j].x + img.DestBasePosition.X, window.MainPicBox.Height - img.SrcRectangle.Height + cbgList[j].y + img.DestBasePosition.Y, img.SrcRectangle.Width, img.SrcRectangle.Height),
			//	//	img.SrcRectangle, GraphicsUnit.Pixel);
			//}

		}
		#region EmuEra-Rikaichan
#if !HEADLESS
		if (Config.RikaiEnabled)
			rikaichan.OnPaint(graph, stringMeasure, _uiAdapter.MainPicBox.Width);
#endif
		#endregion

		//真のHTML描画
		var y = 0;
		foreach (var element in _htmlElementList)
		{
			element.DrawTo(graph, y, false, false, Config.TextDrawingMode);
			y += Config.LineHeight;
		}

		//ToolTip描画
		if (lastPointingString != pointingString || lastSelectingCBGButtonInt != selectingCBGButtonInt)
		{
			if (tooltipUsed)
				_uiAdapter.ToolTip.RemoveAll();

			string title = null;
			if (pointingString != null)
				title = pointingString.Title;
			else if (selectingCBGButtonInt > 0)
			{
				foreach (var cbg in cbgList)
				{
					if (!cbg.isButton || cbg.buttonValue != selectingCBGButtonInt)
						continue;
					if (string.IsNullOrEmpty(cbg.tooltipString))
						continue;
					title = cbg.tooltipString;
					break;
				}
			}
			if (!string.IsNullOrEmpty(title))
			{
				title = title.Replace("<br>", Environment.NewLine);
				#region EE_ツールチップ拡張での位置調整
				//if (tooltip_duration == 0 || window.ToolTip.OwnerDraw == true)
				//{
				//	//window.ToolTip.SetToolTip(window.MainPicBox, title);
				//	Point mousePos = window.MainPicBox.PointToClient(Control.MousePosition);
				//	window.ToolTip.Show(title, window.MainPicBox, new Point(mousePos.X + 30, mousePos.Y - 18));
				//}
				//else
				//{
				//if (window.ToolTip.InitialDelay == 0)
				//{
				//	window.ToolTip.Show(title, window.MainPicBox, p, tooltip_duration);
				//}
				//else
				//{
				System.Threading.SynchronizationContext context = System.Threading.SynchronizationContext.Current;
						Task.Run(async () =>
						{
							ConsoleButtonString savedPointingString = pointingString;
							if (_uiAdapter.ToolTip.InitialDelay != 0)
							//	await Task.Delay(500);
							//else
								await Task.Delay(_uiAdapter.ToolTip.InitialDelay);
							context.Post((state) =>
							{
								MoveMouse(GetMousePosition());
								if (lastPointingString == savedPointingString)
								{
									Point mousePos = _uiAdapter.MainPicBox.PointToClient(_uiAdapter.GetCursorPosition());
									Point p = new Point(mousePos.X + 2, mousePos.Y + _uiAdapter.GetCursorHeight());
									Point absoluteP = _uiAdapter.GetCursorPosition();
									if (absoluteP.Y + tooltip_size.Height > _uiAdapter.GetScreenWorkingAreaHeight(mousePos)) p.Y -= _uiAdapter.GetCursorHeight() * 2; 
									if (tooltip_duration == 0)
										_uiAdapter.ToolTip.Show(title, p);
									else
										_uiAdapter.ToolTip.Show(title, p, tooltip_duration);
								}
							}, null);
						});
				//}
				//}
				#endregion
				tooltipUsed = true;
			}
			lastPointingString = pointingString;
			lastSelectingCBGButtonInt = selectingCBGButtonInt;
		}
		if (isBackLog)
			lastDrawnLineNo = -1;
		else
			lastDrawnLineNo = lineNo;
		lastSelectingButton = selectingButton;
		/*デバッグ用。描画が超重い環境を想定2
		System.Threading.Thread.Sleep(50);
		*/
		forceTextBoxColor = false;
		if (need_settimer)
		{
			need_settimer = false;
			setTimer();
		}
#endif // !HEADLESS (OnPaint)
	}

	private void ToolTip_Draw(object sender, ToolTipDrawEventArgs e)
	{
#if !HEADLESS
		if (tooltip_img && int.TryParse(e.ToolTipText, out int i))
		{
			var g = GameData.Function.FunctionMethodCreator.ReadGraphics(i);
			if (g.IsCreated)
			{
				Image img = g.Bitmap;
				e.Graphics.DrawImage(img, 0, 0);
				return;
			}

		}
		foreach (FontFamily ff in GlobalStatic.Pfc.Families)
		{
			if (ff.Name == tooltip_fontname)
			{
				using (Font f = new(ff, tooltip_fontsize))
				{
					TextRenderer.DrawText(e.Graphics, e.ToolTipText, f, e.Bounds, _uiAdapter.ToolTip.ForeColor, _uiAdapter.ToolTip.BackColor, tooltip_format);
				}
				return;
			}
		}
		using (Font f = new(tooltip_fontname, tooltip_fontsize))
		{
			TextRenderer.DrawText(e.Graphics, e.ToolTipText, f, e.Bounds, _uiAdapter.ToolTip.ForeColor, _uiAdapter.ToolTip.BackColor, tooltip_format);
		}
#endif
	}
	Size tooltip_size;
	private void ToolTip_Popup(object sender, ToolTipPopupEventArgs e)
	{
#if !HEADLESS
		if (tooltip_img && int.TryParse((sender as IToolTip).GetToolTip(), out int i))
		{
			var g = GameData.Function.FunctionMethodCreator.ReadGraphics(i);
			if (g.IsCreated)
			{
				e.ToolTipSize = new Size(g.Width, g.Height);
				return;
			}
		}
		Font f;
		foreach (FontFamily ff in GlobalStatic.Pfc.Families)
		{
			if (ff.Name == tooltip_fontname)
			{
				f = new Font(ff, tooltip_fontsize);
				goto foundfont;
			}
		}
		f = new Font(tooltip_fontname, tooltip_fontsize);
	foundfont:
		var size = TextRenderer.MeasureText((sender as IToolTip).GetToolTip(), f, new Size(int.MaxValue, int.MaxValue), tooltip_format);
		e.ToolTipSize = new Size(size.Width, size.Height);
		tooltip_size = e.ToolTipSize;
#endif
	}

	public void CustomToolTip(bool b)
	{
		if (!b)
		{
			_uiAdapter.ToolTip.Draw -= ToolTip_Draw;
			_uiAdapter.ToolTip.Popup -= ToolTip_Popup;
		}
		else if (!_uiAdapter.ToolTip.OwnerDraw)
		{
			_uiAdapter.ToolTip.Draw += ToolTip_Draw;
			_uiAdapter.ToolTip.Popup += ToolTip_Popup;
		}
		_uiAdapter.ToolTip.OwnerDraw = b;
	}

	public void SetToolTipColor(Color foreColor, Color backColor)
	{
		_uiAdapter.ToolTip.ForeColor = foreColor;
		_uiAdapter.ToolTip.BackColor = backColor;

	}
	public void SetToolTipDelay(int delay)
	{
		_uiAdapter.ToolTip.InitialDelay = delay;
	}

	int tooltip_duration;
	string tooltip_fontname = Config.FontName;
	long tooltip_fontsize = Config.FontSize;
#if !HEADLESS
	TextFormatFlags tooltip_format;
#endif
	bool tooltip_img;
	public void SetToolTipDuration(int duration)
	{
		tooltip_duration = duration;
		_uiAdapter.ToolTip.AutoPopDelay = duration;
	}
	public void SetToolTipFontName(string fn)
	{
		tooltip_fontname = fn;
	}
	public void SetToolTipFontSize(long fs)
	{
		tooltip_fontsize = fs;
	}
	public void SetToolTipFormat(long f)
	{
#if !HEADLESS
		tooltip_format = (TextFormatFlags)f;
#endif
	}
	public void SetToolTipImg(bool b)
	{
		tooltip_img = b;
	}

	//private Graphics getGraphics()
	//{
	//	//消したいが怖いので残し
	//	if (!window.Created)
	//		throw new ExeEE("存在しないウィンドウにアクセスした");
	//	//if (Config.UseImageBuffer)
	//	//	return Graphics.FromImage(window.MainPicBox.Image);
	//	//else
	//		return window.MainPicBox.CreateGraphics();
	//}

	#endregion

	#region DebugMode系
#if !HEADLESS
	DebugDialog dd;
	public DebugDialog DebugDialog { get { return dd; } }
#else
	object dd;
	public object DebugDialog => dd;
#endif
	StringBuilder dConsoleLog = new("");
	public string DebugConsoleLog { get { return dConsoleLog.ToString(); } }
	List<string> dTraceLogList = [];
	public string GetDebugTraceLog(bool force)
	{
		//if (!dTraceLogChanged && !force)
		//	return null;
		StringBuilder builder = new("");
		LogicalLine line = process.GetScaningLine();
		builder.AppendLine(trsl.Processing.Text);
		if ((line == null) || (line.Position == null))
		{
			builder.AppendLine(trsl.FileNone.Text);
			builder.AppendLine(trsl.LineFuncNone.Text);
			builder.AppendLine("");
		}
		else
		{
			builder.AppendLine(string.Format(trsl.FileName.Text, line.Position.Value.Filename));
			builder.AppendLine(string.Format(trsl.LineFuncName.Text, line.Position.Value.LineNo.ToString(), line.ParentLabelLine.LabelName));
			builder.AppendLine("");
		}
		builder.AppendLine(trsl.FuncCallStack.Text);
		for (int i = dTraceLogList.Count - 1; i >= 0; i--)
		{
			builder.AppendLine(dTraceLogList[i]);
		}
		return builder.ToString();
	}
	public void OpenDebugDialog()
	{
#if !HEADLESS
		if (!Program.DebugMode)
			return;
		if (dd != null)
		{
			if (dd.Created)
			{
				dd.Focus();
				return;
			}
			else
			{
				dd.Dispose();
				dd = null;
			}
		}
		dd = new DebugDialog();
		dd.SetParent(this, process);
		dd.TranslateUI();
		dd.Show();
#endif
	}

	public void DebugPrint(string str)
	{
		if (!Program.DebugMode)
			return;
		dConsoleLog.Append(str);
	}

	public void DebugClear()
	{
		dConsoleLog.Remove(0, dConsoleLog.Length);
	}

	public void DebugNewLine()
	{
		if (!Program.DebugMode)
			return;
		dConsoleLog.Append(Environment.NewLine);
	}

	public void DebugAddTraceLog(string str)
	{
		//Emueraがデバッグモードで起動されていないなら無視
		//ERBファイル以外のもの(デバッグコマンド、変数ウォッチ)を実行中なら無視
		if (!Program.DebugMode || runningERBfromMemory)
			return;
		dTraceLogList.Add(str);
	}
	public void DebugRemoveTraceLog()
	{
		if (!Program.DebugMode || runningERBfromMemory)
			return;
		if (dTraceLogList.Count > 0)
			dTraceLogList.RemoveAt(dTraceLogList.Count - 1);
	}
	public void DebugClearTraceLog()
	{
		if (!Program.DebugMode || runningERBfromMemory)
			return;
		dTraceLogList.Clear();
	}

	public void DebugCommand(string com, bool munchkin, bool outputDebugConsole)
	{
		ConsoleState temp_state = state;
		runningERBfromMemory = true;
		//スクリプト等が失敗した場合に備えて念のための保存
		GlobalStatic.Process.saveCurrentState(false);
		try
		{
			//デバッグコマンドはReadEnabledLineを通してないのでRename変換を入れる
			if (Config.UseRenameFile && (com.IndexOf("[[", StringComparison.Ordinal) >= 0) && (com.IndexOf("]]", StringComparison.Ordinal) >= 0))
			{
				foreach (KeyValuePair<string, string> pair in ParserMediator.RenameDic)
					com = com.Replace(pair.Key, pair.Value);
			}
			LogicalLine line = null;
			if (!com.StartsWith('@') && !com.StartsWith('"') && !com.StartsWith('\\'))
				line = LogicalLineParser.ParseLine(com, null);
			if (line == null || (line is InvalidLine))
			{
				WordCollection wc = LexicalAnalyzer.Analyse(new CharStream(com), LexEndWith.EoL, LexAnalyzeFlag.None);
				AExpression term = ExpressionParser.ReduceExpressionTerm(wc, TermEndWith.EoL);
				if (term == null)
					throw new CodeEE(trerror.CanNotInterpretedLine.Text);
				if (term.GetOperandType() == typeof(long))
				{
					if (outputDebugConsole)
						com = "DEBUGPRINTFORML {" + com + "}";
					else
						com = "PRINTVL " + com;
				}
				else
				{
					if (outputDebugConsole)
						com = "DEBUGPRINTFORML %" + com + "%";
					else
						com = "PRINTFORMSL " + com;
				}
				line = LogicalLineParser.ParseLine(com, null);
			}
			if (line == null)
				throw new CodeEE(trerror.CanNotInterpretedLine.Text);
			if (line is InvalidLine)
				throw new CodeEE(line.ErrMes);
			if (!(line is InstructionLine))
				throw new CodeEE(trerror.InvalidDebugCommand.Text);
			InstructionLine func = (InstructionLine)line;
			if (func.Function.IsFlowContorol())
				throw new CodeEE(trerror.CanNotUseFlowInstruction.Text);
			//__METHOD_SAFE__をみるならいらないかも
			if (func.Function.IsWaitInput())
				throw new CodeEE(string.Format(trerror.CanNotUseInstruction.Text, func.Function.Name));
			//1750 __METHOD_SAFE__とほぼ条件同じだよねってことで
			if (!func.Function.IsMethodSafe())
				throw new CodeEE(string.Format(trerror.CanNotUseInstruction.Text, func.Function.Name));
			//1756 SIFの次に来てはいけないものはここでも不可。
			if (func.Function.IsPartial())
				throw new CodeEE(string.Format(trerror.CanNotUseInstruction.Text, func.Function.Name));
			switch (func.FunctionCode)
			{//取りこぼし
			 //逆にOUTPUTLOG、QUITはDebugCommandの前に捕まえる
				case FunctionCode.PUTFORM:
				case FunctionCode.UPCHECK:
				case FunctionCode.CUPCHECK:
				case FunctionCode.SAVEDATA:
					throw new CodeEE(string.Format(trerror.CanNotUseInstruction.Text, func.Function.Name));
			}
			ArgumentParser.SetArgumentTo(func);
			if (func.IsError)
				throw new CodeEE(func.ErrMes);
			process.DoDebugNormalFunction(func, munchkin);
			if (func.FunctionCode == FunctionCode.SET)
			{
				if (!outputDebugConsole)
					PrintSingleLine(com);
				//DebugWindowのほうは少しくどくなるのでいらないかな
			}
		}
		catch (Exception e)
		{
			if (outputDebugConsole)
			{
				DebugPrint(e.Message);
				DebugNewLine();
			}
			else
				PrintError(e.Message);
			process.clearMethodStack();
		}
		finally
		{
			//確実に元の状態に戻す
			GlobalStatic.Process.loadPrevState();
			runningERBfromMemory = false;
			state = temp_state;
		}
	}
	#endregion

	#region Window.Form系

	internal Point GetMousePosition()
	{
		return _uiAdapter.GetMousePosition();
	}
	#region EE_MOUSEB
	public bool AlwaysRefresh;
	#endregion

	#region EM_私家版_描画拡張
	int[] dummy = [0];
	#endregion
	/// <summary>
	/// マウス位置をボタンの選択状態に反映させる
	/// </summary>
	/// <param name="point"></param>
	/// <returns>この後でRefreshStringsが必要かどうか</returns>
	public bool MoveMouse(Point point)
	{
		#region EmuEra-Rikaichan
		int curLineY = -1;
		#endregion

#if !HEADLESS
		if (cbgButtonMap != null && cbgButtonMap.IsCreated)
		{
			//pointはクライアント左上基準の座標。
			//clientPointをクライアント左下基準の座標に置き換え
			Point clientPoint = point;
			clientPoint.Y = point.Y - ClientHeight;
			int buttonNum = -1;
			//マップ画像の左上基準の座標に置き換え
			Point mapPoint = clientPoint;
			mapPoint.Y = mapPoint.Y + cbgButtonMap.Height;
			if (mapPoint.X >= 0 && mapPoint.Y >= 0 && mapPoint.X < cbgButtonMap.Width && mapPoint.Y < cbgButtonMap.Height)
			{
				Color c = cbgButtonMap.Bitmap.GetPixel(mapPoint.X, mapPoint.Y);
				if (c.A == 255)
				{
					buttonNum = c.ToArgb() & 0xFFFFFF;
				}
			}
			if (buttonNum >= 0)
			{
				bool ret = pointingString != null || selectingButton != null || buttonNum != selectingCBGButtonInt;
				selectingCBGButtonInt = buttonNum;
				pointingString = null;
				pointingStrings.Clear();
				selectingButton = null;
				return ret;
			}
			else if (selectingCBGButtonInt >= 0)
			{
				selectingCBGButtonInt = -1;
				pointingString = null;
				pointingStrings.Clear();
				selectingButton = null;
				return true;
			}
		}
#endif
		selectingCBGButtonInt = -1;
		ConsoleButtonString select = null;
		ConsoleButtonString pointing = null;
		int prevPointingStringsLen = pointingStrings.Count;
		pointingStrings.Clear();
		bool firstPointngSelected = false;
		bool canSelect = false;
		//数値か文字列の入力待ち状態でなければ選択中にはならない
		if (state == ConsoleState.Error)
			canSelect = true;
		else if (state == ConsoleState.WaitInput && inputReq.NeedValue)
			canSelect = true;
		//スクリプト実行中は無視//入力・マクロ処理中は無視
		#region EE_MOUSEB
		if (IsInProcess && AlwaysRefresh == false)
			#endregion
			goto end;
		//履歴表示中は無視
		//if (window.ScrollBar.Value != window.ScrollBar.Maximum)
		//	goto end;
		int pointX = point.X;
		int pointY = point.Y;
		ConsoleDisplayLine curLine;

		int bottomLineNo = _uiAdapter.ScrollBar.Value - 1;
		if (displayLineList.Count - 1 < bottomLineNo)
			bottomLineNo = displayLineList.Count - 1;//1820 この処理不要な気がするけどエラー報告があったので入れとく
		int topLineNo = bottomLineNo - (_uiAdapter.MainPicBox.Height / Config.LineHeight);
		if (topLineNo < 0)
			topLineNo = 0;
		int relPointY = pointY - _uiAdapter.MainPicBox.Height;
		//下から上へ探索し発見次第打ち切り
		#region EM_私家版_描画拡張
		if (ConsoleEscapedParts.Changed || !ConsoleEscapedParts.TestedInRange(topLineNo, bottomLineNo, lastButtonGeneration))
		{
			if (escapedParts == null) escapedParts = [];
			ConsoleEscapedParts.GetPartsInRange(topLineNo, bottomLineNo, lastButtonGeneration, escapedParts);
		}
		var edepth = escapedParts == null || escapedParts.Keys.Count == 0 ? dummy : escapedParts.Keys.ToArray();
		Array.Sort(edepth);
		int eidx = 0;
		bool zeroTested = false;
		var bottomLineBase = _uiAdapter.MainPicBox.Height - Config.LineHeight;
		while (eidx < edepth.Length)
		{
			var depth = edepth[eidx];
			if (!zeroTested && depth >= 0)
			{
				depth = 0;
				zeroTested = true;
				// 普通のパーツのヒットテスト
				for (int i = bottomLineNo; i >= topLineNo; i--)
				{
					relPointY += Config.LineHeight;
					curLine = displayLineList[i];

					for (int b = 0; b < curLine.Buttons.Length; b++)
					{
						ConsoleButtonString button = curLine.Buttons[curLine.Buttons.Length - b - 1];
						if (button == null || button.StrArray == null)
							continue;
						if ((button.PointX <= pointX) && (button.PointX + button.Width >= pointX))
						{
							//if (relPointY >= 0 && relPointY <= Config.Config.FontSize)
							//{
							//	pointing = button;
							//	if(pointing.IsButton)
							//		goto breakfor;
							//}
							foreach (AConsoleDisplayNode part in button.StrArray)
							{
								if (part == null || part is ConsoleDivPart)
									continue;
								if ((part.PointX <= pointX) && (part.PointX + part.Width >= pointX)
									&& (relPointY >= part.Top) && (relPointY <= part.Bottom))
								{
									curLineY = _uiAdapter.MainPicBox.Height - Config.LineHeight * (bottomLineNo - i + 1);
									if (!firstPointngSelected)
										pointing = button;
									if (button.IsButton)
									{
										if (!canSelect)
										{
											goto breakfor;
										}
										pointingStrings.Add(button);
										firstPointngSelected = true;
										break; //退出button.StrArray的for循环
									}
								}
							}
						}
					}
					if (firstPointngSelected && bottomLineNo - i > 100)
					{
						break;
					}
				}
			}
			if (eidx < edepth.Length && edepth[eidx] == depth)
			{
				// 行範囲を超えたパーツのヒットテスト
				var correction = lineNo > Config.MaxLog ? lineNo - Config.MaxLog : 0;
				if (depth != 0 || escapedParts.ContainsKey(depth))
					foreach (var part in escapedParts[depth])
					{
						if (part is ConsoleDivPart div)
						{
							var lineY = bottomLineBase + (div.Parent.ParentLine.LineNo - bottomLineNo - correction) * Config.LineHeight;
							var childPointing = div.TestChildHitbox(pointX, pointY, lineY);
							if (childPointing != null)
							{
								pointing = childPointing;
								if (pointing.IsButton)
									goto breakfor;
							}
						}
						else if ((part.PointX <= pointX) && (part.PointX + part.Width >= pointX)
							&& (relPointY >= part.Top) && (relPointY <= part.Bottom))
						{
							pointing = part.Parent;
							if (pointing.IsButton)
								goto breakfor;
						}
					}
				eidx++;
			}
		}
	#endregion


	//int posy_bottom2up = window.MainPicBox.Height - pointY;
	//int logNum = window.ScrollBar.Maximum - window.ScrollBar.Value;
	////表示中の一番下の行番号
	//int curBottomLineNo = displayLineList.Count - logNum;
	//int curPointingLineNo = curBottomLineNo - (posy_bottom2up / Config.LineHeight + 1);
	//if ((curPointingLineNo < 0) || (curPointingLineNo >= displayLineList.Count))
	//	curLine = null;
	//else
	//	curLine =  displayLineList[curPointingLineNo];
	//if (curLine == null)
	//	goto end;

	//pointing = curLine.GetPointingButton(pointX);
	breakfor:
		#region EE_ボタン判定の改善
		if (pointingStrings.Count > 0)
		{
			foreach (var p in pointingStrings)
			{
				if ((p == null) || (p.Generation != lastButtonGeneration))
					continue;
				else if (!p.IsButton)
					continue;
				else if (state == ConsoleState.WaitInput && !p.IsInteger)
				{
					if ((inputReq.InputType == InputType.IntValue) || (inputReq.InputType == InputType.IntButton))
						continue;
				}
				pointing = p;
				goto end;
			}
			canSelect = false;
		}
		else
		{
			if ((pointing == null) || (pointing.Generation != lastButtonGeneration))
				canSelect = false;
			else if (!pointing.IsButton)
				canSelect = false;
			else if (state == ConsoleState.WaitInput && !pointing.IsInteger)
			{
				if ((inputReq.InputType == InputType.IntValue) || (inputReq.InputType == InputType.IntButton))
					canSelect = false;
			}
		}
	#endregion
	end:
		if (canSelect)
			select = pointing;
		bool needRefresh = select != selectingButton || pointing != pointingString || pointingStrings.Count != prevPointingStringsLen;
		pointingString = pointing;
		selectingButton = select;
		#region EmuEra-Rikaichan
#if !HEADLESS
		if (Config.RikaiEnabled && rikaichan.enabled)
		{
			//if (_pointingString != _lastPointingString &&
			if (pointing == null || pointing.StrArray.Length == 0) goto rikaichan_not_found;

			AConsoleDisplayNode cdp;
			ConsoleStyledString css;
			for (int first_subbutton_i = 0; first_subbutton_i < pointing.StrArray.Length; first_subbutton_i++)
			{
				cdp = pointing.StrArray[first_subbutton_i];
				if (cdp.rikaichaned)
				{
					css = cdp as ConsoleStyledString;
					if (css.PointX + css.Width > point.X)
					{
						goto rikaichan_found;
					}
				}
			}

			goto rikaichan_not_found;

		rikaichan_found:
			int xpos = point.X - css.PointX;
			//rikaichan.laststr_css = pointing; //LATER: do I even need this?
			//rikaichan.laststr = rikaichan.laststr_css.ToString();

			rikaichan.laststr = css.Text;
			if (css.NextLine != null)
			{
				rikaichan.laststr += css.NextLine.Text;
			}

			rikaichan.strpos = css.Ends.Length - 1;
			for (int i = css.Ends.Length - 1; i >= 0; i--)
			{
				if (css.Ends[i] < xpos) break;
				rikaichan.strpos = i;
			}

			if (pointingString == lastPointingString && rikaichan.strpos == rikaichan.laststrpos) goto rikaichan_end;

			rikaichan.css = css;
			rikaichan.point = point;
			rikaichan.curLineY = curLineY;

			rikaichan.hidden = false;
			rikaichan.laststrpos = rikaichan.strpos;
			//int show = 3;
			//int showmax = rikaichan.laststr.Length - rikaichan.strpos;
			//if (showmax < show) show = showmax;
			//rikaichan.output = rikaichan.laststr.Substring(rikaichan.strpos, show);
			rikaichan.output = rikaichan.laststr.Substring(rikaichan.strpos);

			//rikaichan.refresh_num++;
			//rikaichan.output = rikaichan.refresh_num.ToString();
			needRefresh = true;
			goto rikaichan_end;

		rikaichan_not_found:
			if (rikaichan.hidden == false)
			{
				rikaichan.hidden = true;
				needRefresh = true;
			}
		} //if rikaichan.enabled
	rikaichan_end:
#endif // !HEADLESS
		#endregion

		if (pointingStrings.Count!= 0)
			select = select;

		return needRefresh;
	}


	public void LeaveMouse()
	{
		bool needRefresh = selectingButton != null || pointingString != null;
		selectingButton = null;
		pointingString = null;
		pointingStrings.Clear();
		if (needRefresh)
		{
			RefreshStrings(true);
		}
	}

	#region EM_textbox位置指定拡張
	private void verticalScrollBarUpdate()
	{
		int max = displayLineList.Count;
		int move = max - _uiAdapter.ScrollBar.Maximum;
		if (move == 0)
			return;
		_uiAdapter.TextBoxIgnoreScrollBarChanges = true;
		if (move > 0)
		{
			_uiAdapter.ScrollBar.Maximum = max;
			_uiAdapter.ScrollBar.Value += move;
		}
		else
		{
			if (max > _uiAdapter.ScrollBar.Value)
				_uiAdapter.ScrollBar.Value = max;
			_uiAdapter.ScrollBar.Maximum = max;
		}
		_uiAdapter.ScrollBar.Enabled = max > 0;
		_uiAdapter.TextBoxIgnoreScrollBarChanges = false;
	}
	#endregion
	#endregion



	public void GotoTitle()
	{
		//if (state == ConsoleState.Error)
		//{
		//    MessageBox.Show("エラー発生時はこの機能は使えません");
		//}
		forceStopTimer();
		ClearDisplay();
		//動的作成の分だけは削除する
#if !HEADLESS
		AppContents.UnloadGraphicList();
#endif
		redraw = ConsoleRedraw.Normal;
		UseUserStyle = false;
		userStyle = new StringStyle(Config.ForeColor, FontStyle.Regular, null);
		process.BeginTitle();
		ReadAnyKey(false, false);
		RunEmueraProgram("");
		RefreshStrings(true);
	}

	// Used by ctrlZ.
	public void GotoTitleAndLoadAndRepeatInput()
	{
		if (!Config.Ctrl_Z_Enabled) return;
		if (JSONConfig.Data.UseNewRandom)
		{
#if HEADLESS
			Console.Error.WriteLine("CtrlZ: JSONConfig.Data.UseNewRandom not supported");
#else
			MessageBox.Show("CtrlZ: JSONConfig.Data.UseNewRandom not supported");
#endif
			return;
			// It is possible to implement, but I'm not sure if it will be worth it.
			// * Approach 1 is to implement Random class deep copy:
			// Would require making a custom Random class, since default one doesn't provide
			// a way to do deep clone.
			// https://stackoverflow.com/questions/47750409/deep-clone-of-system-random
			// https://referencesource.microsoft.com/#mscorlib/system/random.cs
			// * Approach 2 is to make a new random seed on every save,
			// that requires a bit less storage but can potentially modify the way game works.
			// It would probably still require making your 
			// * Approach 3 is to pretend that UseNewRandom doesn't exist.
			// I'm not sure what games currently use that anyway.
		}
		if (GlobalStatic.ctrlZ.mLastSave < 0) return;
		if (GlobalStatic.ctrlZ.mInputs.Count == 0) return;
		if (GlobalStatic.ctrlZ.mRewindInProgress)
		{
			GlobalStatic.ctrlZ.mRepeatedUndoRequested = true;
			return;
		}

	again:

		//GotoTitle
		forceStopTimer();
		ClearDisplay();
		//動的作成の分だけは削除する
#if !HEADLESS
		AppContents.UnloadGraphicList();
#endif
		redraw = ConsoleRedraw.Normal;
		UseUserStyle = false;
		userStyle = new StringStyle(Config.ForeColor, FontStyle.Regular, null);
		process.BeginTitle();
		ReadAnyKey(false, false);
		RunEmueraProgram("");
		RefreshStrings(true);

		//Load
		GlobalStatic.ctrlZ.mRewindInProgress = true;

		GlobalStatic.VEvaluator.Rand.SetRand(GlobalStatic.ctrlZ.mRandomSeed);

		GlobalStatic.Process.LoadSilent();

		PressEnterKey(true, GlobalStatic.ctrlZ.mLastSave.ToString(), false);

		//RepeatInput
		var inputs = GlobalStatic.ctrlZ.mInputs;
		if (inputs.Count > 0)
		{
			inputs.RemoveAt(inputs.Count - 1);
		}

		for (int i = 0; i < inputs.Count; i++)
		{
			if (GlobalStatic.ctrlZ.mRepeatedUndoRequested)
			{
				GlobalStatic.ctrlZ.mRepeatedUndoRequested = false;
				goto again;
			}

			PressEnterKey(true, inputs[i], false);
		}

		GlobalStatic.ctrlZ.mRewindInProgress = false;
		GlobalStatic.ctrlZ.mRepeatedUndoRequested = false;
		//^ because it's possible to leave it true overwise. I think.
	}

	bool force_temporary;
	bool timer_suspended;
	ConsoleState prevState;
	InputRequest prevReq;

	public async Task ReloadErb()
	{
		if (state == ConsoleState.Error)
		{
#if HEADLESS
			Console.Error.WriteLine(trerror.CanNotUseWhenError.Text);
#else
			MessageBox.Show(trerror.CanNotUseWhenError.Text);
#endif
			return;
		}
		if (state == ConsoleState.Initializing)
		{
#if HEADLESS
			Console.Error.WriteLine(trerror.CanNotUseWhenInitialize.Text);
#else
			MessageBox.Show(trerror.CanNotUseWhenInitialize.Text);
#endif
			return;
		}
		bool notRedraw = false;
		if (redraw == ConsoleRedraw.None)
		{
			notRedraw = true;
			redraw = ConsoleRedraw.Normal;
		}
		if (genericTimer.Enabled)
		{
			genericTimer.Enabled = false;
			timer_suspended = true;
		}
		prevState = state;
		prevReq = inputReq;
		state = ConsoleState.Initializing;
		PrintSingleLine(trsl.ReloadingErb.Text, true);
		force_temporary = true;
		await process.ReloadErb();
		force_temporary = false;
		PrintSingleLine(trsl.ReloadCompleted.Text, true);
		RefreshStrings(true);
		//強制的にボタン世代が切り替わるのを防ぐ
		updatedGeneration = true;
		if (notRedraw)
			redraw = ConsoleRedraw.None;
	}

	public void ReloadErbFinished()
	{
		state = prevState;
		inputReq = prevReq;
		PrintSingleLine(" ");
		if (timer_suspended)
		{
			timer_suspended = false;
			genericTimer.Enabled = true;
			//タイマー待機中の時間ずれは修正しない。タイマー中にリロードしたらほぼ強制タイムアウトする程度は仕様のうちであろう。
		}
	}

	public async Task ReloadPartialErb(List<string> path)
	{
		if (state == ConsoleState.Error)
		{
#if HEADLESS
			Console.Error.WriteLine(trerror.CanNotUseWhenError.Text);
#else
			MessageBox.Show(trerror.CanNotUseWhenError.Text);
#endif
			return;
		}
		if (state == ConsoleState.Initializing)
		{
#if HEADLESS
			Console.Error.WriteLine(trerror.CanNotUseWhenInitialize.Text);
#else
			MessageBox.Show(trerror.CanNotUseWhenInitialize.Text);
#endif
			return;
		}
		bool notRedraw = false;
		if (redraw == ConsoleRedraw.None)
		{
			notRedraw = true;
			redraw = ConsoleRedraw.Normal;
		}
		if (genericTimer.Enabled)
		{
			genericTimer.Enabled = false;
			timer_suspended = true;
		}
		prevState = state;
		prevReq = inputReq;
		state = ConsoleState.Initializing;
		PrintSingleLine(trsl.ReloadingErb.Text, true);
		force_temporary = true;
		await process.ReloadPartialErb(path);
		force_temporary = false;
		PrintSingleLine(trsl.ReloadCompleted.Text, true);
		RefreshStrings(true);
		//強制的にボタン世代が切り替わるのを防ぐ
		updatedGeneration = true;
		if (notRedraw)
			redraw = ConsoleRedraw.None;
	}

	public async Task ReloadFolder(string erbPath)
	{
		if (state == ConsoleState.Error)
		{
#if HEADLESS
			Console.Error.WriteLine(trerror.CanNotUseWhenError.Text);
#else
			MessageBox.Show(trerror.CanNotUseWhenError.Text);
#endif
			return;
		}
		if (state == ConsoleState.Initializing)
		{
#if HEADLESS
			Console.Error.WriteLine(trerror.CanNotUseWhenInitialize.Text);
#else
			MessageBox.Show(trerror.CanNotUseWhenInitialize.Text);
#endif
			return;
		}
		if (genericTimer.Enabled)
		{
			genericTimer.Enabled = false;
			timer_suspended = true;
		}
		List<string> paths = [];
		SearchOption op = SearchOption.AllDirectories;
		if (!Config.SearchSubdirectory)
			op = SearchOption.TopDirectoryOnly;
		var fnames = Directory.EnumerateFiles(erbPath, "*.ERB", op);
		foreach (var fname in fnames)
		{
			if (Ascii.EqualsIgnoreCase(Path.GetExtension(fname), ".ERB"))
				paths.Add(fname);
		}
		bool notRedraw = false;
		if (redraw == ConsoleRedraw.None)
		{
			notRedraw = true;
			redraw = ConsoleRedraw.Normal;
		}
		prevState = state;
		prevReq = inputReq;
		state = ConsoleState.Initializing;
		PrintSingleLine(trsl.ReloadingErb.Text, true);
		force_temporary = true;
		await process.ReloadPartialErb(paths);
		force_temporary = false;
		PrintSingleLine(trsl.ReloadCompleted.Text, true);
		RefreshStrings(true);
		//強制的にボタン世代が切り替わるのを防ぐ
		updatedGeneration = true;
		if (notRedraw)
			redraw = ConsoleRedraw.None;
	}
	public void ReloadResource()
	{
		/*
		if (state == ConsoleState.Error)
		{
			MessageBox.Show(trerror.CanNotUseWhenError.Text);
			return;
		}
		if (state == ConsoleState.Initializing)
		{
			MessageBox.Show(trerror.CanNotUseWhenInitialize.Text);
			return;
		}
		if (genericTimer.Enabled)
		{
			genericTimer.Enabled = false;
			timer_suspended = true;
		}
		bool notRedraw = false;
		if (redraw == ConsoleRedraw.None)
		{
			notRedraw = true;
			redraw = ConsoleRedraw.Normal;
		}

		prevState = state;
		prevReq = inputReq;
		state = ConsoleState.Initializing;
		force_temporary = true;
		*/
#if !HEADLESS
		AppContents.LoadContents(true);
#endif
		//force_temporary = false;
		PrintSingleLine(trsl.ReloadResourceMessage.Text, true);
		/*
		RefreshStrings(true);
		//強制的にボタン世代が切り替わるのを防ぐ
		updatedGeneration = true;
		if (notRedraw)
			redraw = ConsoleRedraw.None;
		*/
	}

	public void Dispose()
	{
		_agentBridge?.Stop();
		if (genericTimer != null)
			genericTimer.Dispose();
		//timer = null;
		//stringMeasure.Dispose();
	}
}

#if !HEADLESS
internal class ConsoleBackground
{
	public readonly SpriteF bgImage;

	public ConsoleBackground(SpriteF spr, float opacity = 1.0f)
	{
		bgImage = spr;
		colorMatrix = new ColorMatrix();
		SetOpacity(opacity);
	}

	public void SetOpacity(float opacity)
	{
		colorMatrix.Matrix33 = opacity;
	}
	public ColorMatrix GetColorMatrix()
	{
		return colorMatrix;
	}

	private ColorMatrix colorMatrix;
}
#endif
