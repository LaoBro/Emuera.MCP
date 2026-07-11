using MinorShift.Emuera.GameView;
using MinorShift.Emuera.Runtime;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Runtime.Script;
using MinorShift.Emuera.Runtime.Script.Data;
using MinorShift.Emuera.Runtime.Script.Statements.Variable;
using MinorShift.Emuera.Runtime.Utils;
using MinorShift.Emuera.UI.Game;
using MinorShift.Emuera.UI.Game.Image;
using System;
using System.Collections.Generic;
using trerror = MinorShift.Emuera.Runtime.Utils.EvilMask.Lang.Error;
using trsl = MinorShift.Emuera.Runtime.Utils.EvilMask.Lang.SystemLine;

namespace MinorShift.Emuera.GameProc;

// ADR-0011 Phase 3: IProcessState injected (as property over parent.state); parent.state.XXX → state.XXX.
// Remaining `parent.*` bridge: systemResult, skipPrint, isCTrain, count, coms,
//       flowinput*, noError, needCheck, NeedWaitToEventComEnd, doTrainSelectCom,
//       loadPrevState/deletePrevState/deleteAllPrevState, ClearCommands.
// TODO: Narrow per-instruction-family to F2 (no parent back-ref). Target families:
//       beginTrain/endTrain → pure SystemState routing via injected console+VEvaluator;
//       WaitInput/trainWaitInput → SessionIO abstraction;
//       ShwCom/printCom → display-only via injected console.
internal sealed partial class Process
{
	private string[] TrainName = null!;
	long systemResult;
	public long flowinputDef = 0;
	public bool flowinput = false;
	public bool flowinputCanSkip = false;
	public string flowinputDefString = "";
	public bool flowinputString = false;
	public bool flowinputForceSkip = false;
	public bool NeedWaitToEventComEnd;
	bool needCheck = true;
	List<long> coms = [];
	bool isCTrain;
	int count;
	bool skipPrint;
	public bool SkipPrint { get { return skipPrint; } set { skipPrint = value; } }
	private long doTrainSelectCom = -1;

	internal sealed class SystemProc
	{
		readonly Process parent;
		readonly EmueraConsole console;
		readonly IVariableEvaluator vEvaluator;
		readonly GameBase gamebase;
		readonly string[] trainName;
		IProcessState state => parent.state;
		delegate void SystemProcess();
		Dictionary<SystemStateCode, SystemProcess> systemProcessDictionary = [];
		int[] comAble = null!;
		int lastCalledComable = -1;
		int lastAddCom = -1;
		int printComCount;
		bool[] dataIsAvailable = new bool[21];
		bool isFirstTime = true;
		const int AutoSaveIndex = 99;
		int page;
		int saveTarget = -1;

		internal SystemProc(Process parent, EmueraConsole console, IVariableEvaluator vEvaluator, GameBase gamebase, string[] trainName)
		{
			this.parent = parent;
			this.console = console;
			this.vEvaluator = vEvaluator;
			this.gamebase = gamebase;
			this.trainName = trainName;
		}

		internal void Init()
		{
			comAble = new int[trainName.Length];
			systemProcessDictionary.Add(SystemStateCode.Title_Begin, new SystemProcess(beginTitle));
			systemProcessDictionary.Add(SystemStateCode.Openning, new SystemProcess(endOpenning));

			systemProcessDictionary.Add(SystemStateCode.Train_Begin, new SystemProcess(beginTrain));
			systemProcessDictionary.Add(SystemStateCode.Train_CallEventTrain, new SystemProcess(endCallEventTrain));
			systemProcessDictionary.Add(SystemStateCode.Train_CallShowStatus, new SystemProcess(endCallShowStatus));
			systemProcessDictionary.Add(SystemStateCode.Train_CallComAbleXX, new SystemProcess(endCallComAbleXX));
			systemProcessDictionary.Add(SystemStateCode.Train_CallShowUserCom, new SystemProcess(endCallShowUserCom));
			systemProcessDictionary.Add(SystemStateCode.Train_WaitInput, new SystemProcess(trainWaitInput));
			systemProcessDictionary.Add(SystemStateCode.Train_CallEventCom, new SystemProcess(endEventCom));
			systemProcessDictionary.Add(SystemStateCode.Train_CallComXX, new SystemProcess(endCallComXX));
			systemProcessDictionary.Add(SystemStateCode.Train_CallSourceCheck, new SystemProcess(endCallSourceCheck));
			systemProcessDictionary.Add(SystemStateCode.Train_CallEventComEnd, new SystemProcess(endCallEventComEnd)); ;
			systemProcessDictionary.Add(SystemStateCode.Train_DoTrain, new SystemProcess(doTrain));

			systemProcessDictionary.Add(SystemStateCode.AfterTrain_Begin, new SystemProcess(beginAfterTrain));

			systemProcessDictionary.Add(SystemStateCode.Ablup_Begin, new SystemProcess(beginAblup));
			systemProcessDictionary.Add(SystemStateCode.Ablup_CallShowJuel, new SystemProcess(endCallShowJuel));
			systemProcessDictionary.Add(SystemStateCode.Ablup_CallShowAblupSelect, new SystemProcess(endCallShowAblupSelect));
			systemProcessDictionary.Add(SystemStateCode.Ablup_WaitInput, new SystemProcess(ablupWaitInput));
			systemProcessDictionary.Add(SystemStateCode.Ablup_CallAblupXX, new SystemProcess(endCallAblupXX));

			systemProcessDictionary.Add(SystemStateCode.Turnend_Begin, new SystemProcess(beginTurnend));

			systemProcessDictionary.Add(SystemStateCode.Shop_Begin, new SystemProcess(beginShop));
			systemProcessDictionary.Add(SystemStateCode.Shop_CallEventShop, new SystemProcess(endCallEventShop));
			systemProcessDictionary.Add(SystemStateCode.Shop_CallShowShop, new SystemProcess(endCallShowShop));
			systemProcessDictionary.Add(SystemStateCode.Shop_WaitInput, new SystemProcess(shopWaitInput));
			systemProcessDictionary.Add(SystemStateCode.Shop_CallEventBuy, new SystemProcess(endCallEventBuy));

			systemProcessDictionary.Add(SystemStateCode.SaveGame_Begin, new SystemProcess(beginSaveGame));
			systemProcessDictionary.Add(SystemStateCode.SaveGame_WaitInput, new SystemProcess(saveGameWaitInput));
			systemProcessDictionary.Add(SystemStateCode.SaveGame_WaitInputOverwrite, new SystemProcess(saveGameWaitInputOverwrite));
			systemProcessDictionary.Add(SystemStateCode.SaveGame_CallSaveInfo, new SystemProcess(endCallSaveInfo));
			systemProcessDictionary.Add(SystemStateCode.LoadGame_Begin, new SystemProcess(beginLoadGame));
			systemProcessDictionary.Add(SystemStateCode.LoadGame_WaitInput, new SystemProcess(loadGameWaitInput));
			systemProcessDictionary.Add(SystemStateCode.LoadGameOpenning_Begin, new SystemProcess(beginLoadGameOpening));
			systemProcessDictionary.Add(SystemStateCode.LoadGameOpenning_WaitInput, new SystemProcess(loadGameWaitInput));

			systemProcessDictionary.Add(SystemStateCode.AutoSave_CallSaveInfo, new SystemProcess(endAutoSaveCallSaveInfo));
			systemProcessDictionary.Add(SystemStateCode.AutoSave_CallUniqueAutosave, new SystemProcess(endAutoSave));

			systemProcessDictionary.Add(SystemStateCode.LoadData_DataLoaded, new SystemProcess(beginDataLoaded));
			systemProcessDictionary.Add(SystemStateCode.LoadData_CallSystemLoad, new SystemProcess(endSystemLoad));
			systemProcessDictionary.Add(SystemStateCode.LoadData_CallEventLoad, new SystemProcess(endEventLoad));

			systemProcessDictionary.Add(SystemStateCode.Openning_TitleLoadgame, new SystemProcess(endTitleLoadgame));

			systemProcessDictionary.Add(SystemStateCode.System_Reloaderb, new SystemProcess(endReloaderb));
			systemProcessDictionary.Add(SystemStateCode.First_Begin, new SystemProcess(beginFirst));


			systemProcessDictionary.Add(SystemStateCode.Normal, new SystemProcess(endNormal));
			return;
		}

		public void Run()
		{
			systemProcessDictionary[state.SystemState]();
		}

		internal bool CallFunction(string functionName, bool force, bool isEvent)
		{
			CalledFunction call;
			if (isEvent)
				call = CalledFunction.CallEventFunction(parent, functionName, null!);
			else
				call = CalledFunction.CallFunction(parent, functionName, null!);
			if (call == null)
				if (!force)
					return false;
				else
					throw new CodeEE(string.Format(trerror.FuncIsNotFound.Text, functionName));
			state.IntoFunction(call, null!, null!);
			return true;
		}

		void setWait()
		{
			console.ReadAnyKey();
		}

		void setWaitInput()
		{
			InputRequest req = new();
			if (parent.flowinput)
			{
				req.HasDefValue = true;
				req.DefIntValue = parent.flowinputDef;
				req.MouseInput = parent.flowinput;
				req.DefStrValue = parent.flowinputDefString;
			}
			if (parent.flowinputString)
				req.InputType = InputType.StrValue;
			else
				req.InputType = InputType.IntValue;
			req.IsSystemInput = true;
			if (parent.flowinputForceSkip)
			{
				parent.systemResult = req.DefIntValue;
				if (parent.flowinputString)
					vEvaluator.RESULTS = req.DefStrValue;
			}
			else if (parent.flowinputCanSkip && console.MesSkip)
			{
				parent.systemResult = req.DefIntValue;
				if (parent.flowinputString)
					vEvaluator.RESULTS = req.DefStrValue;
			}
			console.WaitInput(req);
		}

	void beginTitle()
	{
		if (parent.isCTrain)
			if (parent.ClearCommands())
				return;
		parent.skipPrint = false;
		console.ResetStyle();
		parent.deleteAllPrevState();
			if (Program.AnalysisMode)
			{
				console.PrintSystemLine(trsl.AnalysisCompleted.Text);
				console.OutputSystemLog(Program.ExeDir + "Analysis.log");
				console.noOutputLog = true;
				console.PrintSystemLine(trsl.PressEnterOrClick.Text);
#if !HEADLESS
				System.Media.SystemSounds.Asterisk.Play();
#endif
				console.ThrowTitleError(false);
				return;
			}
			if ((!parent.noError) && (!Config.CompatiErrorLine))
			{
				console.PrintErrorButton(trsl.ExitBecauseCanNotInterpreted1.Text, null, 3);
				console.PrintSystemLine(string.Format(trsl.ExitBecauseCanNotInterpreted2.Text, Config.GetConfigName(ConfigCode.CompatiErrorLine)));
				console.PrintSystemLine(trsl.ExitBecauseCanNotInterpreted3.Text);
				console.OutputSystemLog(Program.ExeDir + "emuera.log");
				console.noOutputLog = true;
				console.PrintSystemLine(trsl.PressEnterOrClick.Text);
#if !HEADLESS
				System.Media.SystemSounds.Asterisk.Play();
#endif
				console.ThrowTitleError(true);
				return;
			}
			if (CallFunction("SYSTEM_TITLE", false, false))
			{
				state.SystemState = SystemStateCode.Normal;
				return;
			}
			console.PrintBar();
			console.NewLine();
			console.Alignment = DisplayLineAlignment.CENTER;
			console.PrintSingleLine(gamebase.ScriptTitle);
			if (gamebase.ScriptVersion != 0)
				console.PrintSingleLine(gamebase.ScriptVersionText);
			console.PrintSingleLine(gamebase.ScriptAutherName);
			console.PrintSingleLine("(" + gamebase.ScriptYear + ")");
			console.NewLine();
			console.PrintSingleLine(gamebase.ScriptDetail);
			console.Alignment = DisplayLineAlignment.LEFT;

			console.PrintBar();
			console.NewLine();
			console.PrintSingleLine("[0] " + Config.TitleMenuString0);
			console.PrintSingleLine("[1] " + Config.TitleMenuString1);
			openingInput();
			return;
		}

		void openingInput()
		{
			setWaitInput();
			state.SystemState = SystemStateCode.Openning;
			return;
		}

		void endOpenning()
		{
			if (parent.systemResult == 0)
			{
				vEvaluator.ResetData();
				vEvaluator.AddCharacterFromCsvNo(0);
				if (gamebase.DefaultCharacter > 0)
					vEvaluator.AddCharacterFromCsvNo(gamebase.DefaultCharacter);
				console.PrintBar();
				console.NewLine();
				beginFirst();
			}
			else if (parent.systemResult == 1)
			{
				if (CallFunction("TITLE_LOADGAME", false, false))
				{
					state.SystemState = SystemStateCode.Openning_TitleLoadgame;
				}
				else
				{
					beginLoadGameOpening();
				}
			}
			else
			{
				console.deleteLine(1);
				console.PrintTemporaryLine(trerror.InvalidValue.Text);
				console.updatedGeneration = true;
				openingInput();
			}

		}

		void beginFirst()
		{
			state.SystemState = SystemStateCode.Normal;
			if (parent.isCTrain)
				if (parent.ClearCommands())
					return;
			parent.skipPrint = false;
			CallFunction("EVENTFIRST", true, true);
		}

		void endTitleLoadgame()
		{
			beginTitle();
		}

		void beginTrain()
		{
			vEvaluator.UpdateInBeginTrain();
			state.SystemState = SystemStateCode.Train_CallEventTrain;
			if (!CallFunction("EVENTTRAIN", false, true))
			{
				endCallEventTrain();
			}
		}

		void endCallEventTrain()
		{
			if (vEvaluator.NEXTCOM >= 0)
			{
				state.SystemState = SystemStateCode.Train_CallEventCom;
				vEvaluator.SELECTCOM = vEvaluator.NEXTCOM;
				vEvaluator.NEXTCOM = 0;
				callEventCom();
				return;
			}
			else
			{
				if (parent.isCTrain)
					parent.skipPrint = true;
				CallFunction("SHOW_STATUS", true, false);
				state.SystemState = SystemStateCode.Train_CallShowStatus;
			}
		}

		void endCallShowStatus()
		{
			state.SystemState = SystemStateCode.Train_CallComAbleXX;
			lastCalledComable = -1;
			lastAddCom = -1;
			printComCount = 0;
			for (int i = 0; i < comAble.Length; i++)
				comAble[i] = -1;
			endCallComAbleXX();
		}

		string getTrainComString(int trainCode, int comNo)
		{
			string name = trainName[trainCode];
			return string.Format("{0}[{1,3}]", name, comNo);
		}

		void endCallComAbleXX()
		{
			if ((lastCalledComable >= 0) && (trainName[lastCalledComable] != null))
			{
				lastAddCom++;
				if (vEvaluator.RESULT != 0)
				{
					comAble[lastAddCom] = lastCalledComable;
					if (!parent.isCTrain)
					{
						console.PrintC(getTrainComString(lastCalledComable, lastAddCom), true);
						printComCount++;
						if ((Config.PrintCPerLine > 0) && (printComCount % Config.PrintCPerLine == 0))
							console.PrintFlush(false);
					}
					console.RefreshStrings(false);
				}
			}
			while (++lastCalledComable < trainName.Length)
			{
				if (trainName[lastCalledComable] == null)
					continue;
				string comName = string.Format("COM_ABLE{0}", lastCalledComable);
				if (!CallFunction(comName, false, false))
				{
					lastAddCom++;
					if (Config.ComAbleDefault == 0)
						continue;
					comAble[lastAddCom] = lastCalledComable;
					if (!parent.isCTrain)
					{
						console.PrintC(getTrainComString(lastCalledComable, lastAddCom), true);
						printComCount++;
						if ((Config.PrintCPerLine > 0) && (printComCount % Config.PrintCPerLine == 0))
							console.PrintFlush(false);
					}
					continue;
				}
				console.RefreshStrings(false);
				return;
			}
			if (lastCalledComable >= trainName.Length)
			{
				state.SystemState = SystemStateCode.Train_CallShowUserCom;
				console.PrintFlush(false);
				console.RefreshStrings(false);
				CallFunction("SHOW_USERCOM", true, false);
			}
		}

		void endCallShowUserCom()
		{
			if (parent.skipPrint)
				parent.skipPrint = false;
			vEvaluator.UpdateAfterShowUsercom();
			if (!parent.isCTrain)
			{
				setWaitInput();

				state.SystemState = SystemStateCode.Train_WaitInput;
			}
			else
			{
				if (parent.count < parent.coms.Count)
				{
					parent.systemResult = parent.coms[parent.count];
					parent.count++;
					trainWaitInput();
				}
			}
		}

		void trainWaitInput()
		{
			int selectCom = -1;
			if (!parent.isCTrain)
			{
				if ((parent.systemResult >= 0) && (parent.systemResult < comAble.Length))
					selectCom = comAble[parent.systemResult];
			}
			else
			{
				for (int i = 0; i < comAble.Length; i++)
				{
					if (comAble[i] == parent.systemResult)
						selectCom = (int)parent.systemResult;
				}
				console.PrintSingleLine(string.Format(trerror.ExecutedCom.Text, parent.count, parent.coms.Count));
			}
			if (selectCom >= 0)
			{
				vEvaluator.SELECTCOM = selectCom;
				callEventCom();
			}
			else
			{
				if (parent.isCTrain)
					console.PrintSingleLine(trerror.CouldNotExecuteCom.Text);
				vEvaluator.RESULT = parent.systemResult;
				state.SystemState = SystemStateCode.Train_CallEventComEnd;
				CallFunction("USERCOM", true, false);
			}
		}

		void doTrain()
		{
			vEvaluator.UpdateAfterShowUsercom();
			vEvaluator.SELECTCOM = parent.doTrainSelectCom;
			callEventCom();
		}

		void callEventCom()
		{
			vEvaluator.UpdateAfterInputCom();
			state.SystemState = SystemStateCode.Train_CallEventCom;
			if (!CallFunction("EVENTCOM", false, true))
				endEventCom();
			return;
		}

		void endEventCom()
		{
			long selectCom = vEvaluator.SELECTCOM;
			string comName = string.Format("COM{0}", selectCom);
			state.SystemState = SystemStateCode.Train_CallComXX;
			CallFunction(comName, true, false);
		}

		void endCallComXX()
		{
			if (vEvaluator.RESULT == 0)
			{
				endCallEventComEnd();
			}
			else
			{
				state.SystemState = SystemStateCode.Train_CallSourceCheck;
				CallFunction("SOURCE_CHECK", true, false);
			}
		}

		void endCallSourceCheck()
		{
			vEvaluator.UpdateAfterSourceCheck();
			state.SystemState = SystemStateCode.Train_CallEventComEnd;
			parent.NeedWaitToEventComEnd = true;
			if (!CallFunction("EVENTCOMEND", false, true))
			{
				endCallEventComEnd();
			}
		}

		void endCallEventComEnd()
		{
			if (console.LastLineIsTemporary && !parent.isCTrain && parent.needCheck)
			{
				if (console.LastLineIsEmpty)
				{
					console.deleteLine(2);
					console.PrintTemporaryLine(trerror.InvalidValue.Text);
				}
				console.updatedGeneration = true;
				endCallShowUserCom();
			}
			else
			{
				if (parent.isCTrain && parent.count == parent.coms.Count)
				{
					parent.isCTrain = false;
					parent.skipPrint = false;
					parent.coms.Clear();
					parent.count = 0;
					if (CallFunction("CALLTRAINEND", false, false))
					{
						parent.needCheck = false;
						return;
					}
				}
				parent.needCheck = true;
				if (parent.NeedWaitToEventComEnd)
					setWait();
				parent.NeedWaitToEventComEnd = false;
				endCallEventTrain();
			}
		}

		void beginAfterTrain()
		{
			if (parent.isCTrain)
				if (parent.ClearCommands())
					return;
			parent.skipPrint = false;
			state.SystemState = SystemStateCode.Normal;
			CallFunction("EVENTEND", true, true);
		}

		void beginAblup()
		{
			if (parent.isCTrain)
				if (parent.ClearCommands())
					return;
			parent.skipPrint = false;
			state.SystemState = SystemStateCode.Ablup_CallShowJuel;
			CallFunction("SHOW_JUEL", true, false);
		}

		void endCallShowJuel()
		{
			state.SystemState = SystemStateCode.Ablup_CallShowAblupSelect;
			CallFunction("SHOW_ABLUP_SELECT", true, false);
		}

		void endCallShowAblupSelect()
		{
			setWaitInput();
			state.SystemState = SystemStateCode.Ablup_WaitInput;
		}

		void ablupWaitInput()
		{
			if ((parent.systemResult >= 0) && (parent.systemResult < 100))
			{
				state.SystemState = SystemStateCode.Ablup_CallAblupXX;
				string ablName = string.Format("ABLUP{0}", parent.systemResult);
				if (!CallFunction(ablName, false, false))
				{
					console.deleteLine(1);
					console.PrintTemporaryLine(trerror.InvalidValue.Text);
					console.updatedGeneration = true;
					endCallShowAblupSelect();
				}
			}
			else
			{
				vEvaluator.RESULT = parent.systemResult;
				state.SystemState = SystemStateCode.Ablup_CallAblupXX;
				CallFunction("USERABLUP", true, false);
			}
		}

		void endCallAblupXX()
		{
			if (console.LastLineIsTemporary)
			{
				if (console.LastLineIsEmpty)
				{
					console.deleteLine(2);
					console.PrintTemporaryLine("無効な値です");
				}
				console.updatedGeneration = true;
				endCallShowAblupSelect();
			}
			else
				beginAblup();
		}

		void beginTurnend()
		{
			if (parent.isCTrain)
				if (parent.ClearCommands())
					return;
			parent.skipPrint = false;
			CallFunction("EVENTTURNEND", true, true);
			state.SystemState = SystemStateCode.Normal;
		}

		void beginShop()
		{
			if (parent.isCTrain)
				if (parent.ClearCommands())
					return;
			parent.skipPrint = false;
			state.SystemState = SystemStateCode.Shop_CallEventShop;
			if (!CallFunction("EVENTSHOP", false, true))
			{
				endCallEventShop();
			}
		}

		void endCallEventShop()
		{
			saveTarget = -1;
			if (Config.AutoSave && state.calledWhenNormal)
				beginAutoSave();
			else
			{
				state.SystemState = SystemStateCode.AutoSave_Skipped;
				endAutoSaveCallSaveInfo();
			}
		}

		void beginAutoSave()
		{
			if (CallFunction("SYSTEM_AUTOSAVE", false, false))
			{
				state.SystemState = SystemStateCode.AutoSave_CallUniqueAutosave;
				return;
			}
			saveTarget = AutoSaveIndex;
			vEvaluator.SAVEDATA_TEXT = DateTime.Now.ToString("yyyy/MM/dd HH:mm:ss") + " ";
			state.SystemState = SystemStateCode.AutoSave_CallSaveInfo;
			if (!CallFunction("SAVEINFO", false, false))
				endAutoSaveCallSaveInfo();
		}

		void endAutoSaveCallSaveInfo()
		{
			if (saveTarget == AutoSaveIndex)
			{
				if (!vEvaluator.SaveTo(saveTarget, vEvaluator.SAVEDATA_TEXT))
				{
					console.PrintError(trerror.AutoSaveError1.Text);
					console.PrintError(trerror.AutoSaveError2.Text);
					console.ReadAnyKey();
				}
			}
			endAutoSave();
		}

		void endAutoSave()
		{
			if (state.isBegun)
			{
				state.Begin();
				return;
			}
			state.SystemState = SystemStateCode.Shop_CallShowShop;
			CallFunction("SHOW_SHOP", true, false);
		}

		void endCallShowShop()
		{
			setWaitInput();
			state.SystemState = SystemStateCode.Shop_WaitInput;
		}

		void shopWaitInput()
		{
			if ((parent.systemResult >= 0) && (parent.systemResult < Config.MaxShopItem))
			{
				if (vEvaluator.ItemSales(parent.systemResult))
				{
					if (vEvaluator.BuyItem(parent.systemResult))
					{
						state.SystemState = SystemStateCode.Shop_CallEventBuy;
						if (!CallFunction("EVENTBUY", false, true))
							endCallEventBuy();
						return;
					}
					else
					{
						console.deleteLine(1);
						console.PrintTemporaryLine(trerror.NotEnoughMoney.Text);
					}
				}
				else
				{
					console.deleteLine(1);
					console.PrintTemporaryLine(trerror.OutOfStock.Text);
				}
				endCallShowShop();
				return;
			}
			else
			{
				vEvaluator.RESULT = parent.systemResult;

				CallFunction("USERSHOP", true, false);
				state.SystemState = SystemStateCode.Shop_CallEventBuy;
				return;
			}
		}

		void endCallEventBuy()
		{
			if (console.LastLineIsTemporary)
			{
				if (console.LastLineIsEmpty)
				{
					console.deleteLine(2);
					console.PrintTemporaryLine(trerror.InvalidValue.Text);
				}
				console.updatedGeneration = true;
				endCallShowShop();
			}
			else
			{
				endAutoSave();
			}
		}


		void beginDataLoaded()
		{
			state.SystemState = SystemStateCode.LoadData_CallSystemLoad;

			if (!CallFunction("SYSTEM_LOADEND", false, false))
				endSystemLoad();
		}
		void endSystemLoad()
		{
			AppContents.UnloadTempLoadedConstImageNames();
			AppContents.UnloadTempLoadedGraphicsImageNames();
			state.SystemState = SystemStateCode.LoadData_CallEventLoad;
			if (!CallFunction("EVENTLOAD", false, true))
			{
				endAutoSave();
			}
		}

		void endEventLoad()
		{
			endAutoSave();
		}

		void beginSaveGame()
		{
			console.PrintSingleLine(trsl.SaveQuestion.Text);
			state.SystemState = SystemStateCode.SaveGame_Begin;
			printSaveDataText();
		}

		void beginLoadGame()
		{
			console.PrintSingleLine(trsl.LoadQuestion.Text);
			state.SystemState = SystemStateCode.LoadGame_Begin;
			printSaveDataText();
		}

		void beginLoadGameOpening()
		{
			console.PrintSingleLine(trsl.LoadQuestion.Text);
			state.SystemState = SystemStateCode.LoadGameOpenning_Begin;
			printSaveDataText();
		}

		internal void LoadSilent()
		{
			state.SystemState = SystemStateCode.LoadGameOpenning_Begin;

			if (isFirstTime)
			{
				isFirstTime = false;
				dataIsAvailable = new bool[Config.SaveDataNos + 1];
			}
			int dataNo;
			for (int i = 0; i < page; i++)
			{
			}
			for (int i = 0; i < 20; i++)
			{
				dataNo = page * 20 + i;
				if (dataNo == dataIsAvailable.Length - 1)
					break;
				dataIsAvailable[dataNo] = false;
				if (!writeSavedataTextFrom_Silent(dataNo))
					continue;
				dataIsAvailable[dataNo] = true;
			}
			for (int i = page; i < ((dataIsAvailable.Length - 2) / 20); i++)
			{
			}
			dataIsAvailable[^1] = false;
			if (state.SystemState != SystemStateCode.SaveGame_Begin)
			{
				dataNo = AutoSaveIndex;
				if (writeSavedataTextFrom_Silent(dataNo))
					dataIsAvailable[^1] = true;
			}
			setWaitInput();
			if (state.SystemState == SystemStateCode.SaveGame_Begin)
				state.SystemState = SystemStateCode.SaveGame_WaitInput;
			else if (state.SystemState == SystemStateCode.LoadGame_Begin)
				state.SystemState = SystemStateCode.LoadGame_WaitInput;
			else
				state.SystemState = SystemStateCode.LoadGameOpenning_WaitInput;
		}

		void printSaveDataText()
		{
			if (isFirstTime)
			{
				isFirstTime = false;
				dataIsAvailable = new bool[Config.SaveDataNos + 1];
			}
			int dataNo;
			for (int i = 0; i < page; i++)
			{
				console.PrintFlush(false);
				console.Print(string.Format(trsl.DisplaySaveSlot.Text, i * 20, i * 20 + 19));
			}
			for (int i = 0; i < 20; i++)
			{
				dataNo = page * 20 + i;
				if (dataNo == dataIsAvailable.Length - 1)
					break;
				dataIsAvailable[dataNo] = false;
				console.PrintFlush(false);
				console.Print(string.Format("[{0, 2}] ", dataNo));
				if (!writeSavedataTextFrom(dataNo))
					continue;
				dataIsAvailable[dataNo] = true;
			}
			for (int i = page; i < ((dataIsAvailable.Length - 2) / 20); i++)
			{
				console.PrintFlush(false);
				console.Print(string.Format(trsl.DisplaySaveSlot.Text, (i + 1) * 20, (i + 1) * 20 + 19));
			}
			dataIsAvailable[^1] = false;
			if (state.SystemState != SystemStateCode.SaveGame_Begin)
			{
				dataNo = AutoSaveIndex;
				console.PrintFlush(false);
				console.Print(string.Format("[{0, 2}] ", dataNo));
				if (writeSavedataTextFrom(dataNo))
					dataIsAvailable[^1] = true;
			}
			console.RefreshStrings(false);
			console.PrintSingleLine("[100] 戻る");
			setWaitInput();
			if (state.SystemState == SystemStateCode.SaveGame_Begin)
				state.SystemState = SystemStateCode.SaveGame_WaitInput;
			else if (state.SystemState == SystemStateCode.LoadGame_Begin)
				state.SystemState = SystemStateCode.LoadGame_WaitInput;
			else
				state.SystemState = SystemStateCode.LoadGameOpenning_WaitInput;
		}

		void saveGameWaitInput()
		{
			if (parent.systemResult == 100)
			{
				parent.loadPrevState();
				return;
			}
			else if (((int)parent.systemResult / 20) != page && parent.systemResult != AutoSaveIndex && parent.systemResult >= 0 && parent.systemResult < dataIsAvailable.Length - 1)
			{
				page = (int)parent.systemResult / 20;
				state.SystemState = SystemStateCode.SaveGame_Begin;
				printSaveDataText();
				return;
			}
			bool available;
			if ((parent.systemResult >= 0) && (parent.systemResult < dataIsAvailable.Length - 1))
				available = dataIsAvailable[parent.systemResult];
			else
			{
				console.deleteLine(1);
				console.PrintTemporaryLine(trerror.InvalidValue.Text);
				console.updatedGeneration = true;
				setWaitInput();
				return;
			}
			saveTarget = (int)parent.systemResult;

			GlobalStatic.ctrlZ.OnSavePrepare(saveTarget);

			if (available)
			{
				console.PrintSingleLine(trsl.DoYouOverwrite.Text);
				console.PrintC(trsl.Yes.Text, false);
				console.PrintC(trsl.No.Text, false);
				setWaitInput();
				state.SystemState = SystemStateCode.SaveGame_WaitInputOverwrite;
				return;
			}
			parent.systemResult = 0;
			saveGameWaitInputOverwrite();
		}

		void saveGameWaitInputOverwrite()
		{
			if (parent.systemResult == 1)
			{
				beginSaveGame();
				return;
			}
			else if (parent.systemResult != 0)
			{
				console.deleteLine(1);
				console.PrintTemporaryLine(trerror.InvalidValue.Text);
				console.updatedGeneration = true;
				setWaitInput();
				return;
			}
			vEvaluator.SAVEDATA_TEXT = DateTime.Now.ToString("yyyy/MM/dd HH:mm:ss") + " ";
			state.SystemState = SystemStateCode.SaveGame_CallSaveInfo;
			if (!CallFunction("SAVEINFO", false, false))
				endCallSaveInfo();
		}

		void endCallSaveInfo()
		{
			if (!vEvaluator.SaveTo(saveTarget, vEvaluator.SAVEDATA_TEXT))
			{
				console.PrintError(trerror.UnexpectedSaveError.Text);
				console.ReadAnyKey();
			}

			GlobalStatic.ctrlZ.OnSave();

			parent.loadPrevState();
		}

		void loadGameWaitInput()
		{
			if (parent.systemResult == 100)
			{
				if (state.SystemState == SystemStateCode.LoadGameOpenning_WaitInput)
				{
					beginTitle();
					return;
				}
				parent.loadPrevState();
				return;
			}
			else if (((int)parent.systemResult / 20) != page && parent.systemResult != AutoSaveIndex && parent.systemResult >= 0 && parent.systemResult < dataIsAvailable.Length - 1)
			{
				page = (int)parent.systemResult / 20;
				if (state.SystemState == SystemStateCode.LoadGameOpenning_WaitInput)
					state.SystemState = SystemStateCode.LoadGameOpenning_Begin;
				else
					state.SystemState = SystemStateCode.LoadGame_Begin;
				printSaveDataText();
				return;
			}
			bool available;
			if ((parent.systemResult >= 0) && (parent.systemResult < dataIsAvailable.Length - 1))
				available = dataIsAvailable[parent.systemResult];
			else if (parent.systemResult == AutoSaveIndex)
				available = dataIsAvailable[^1];
			else
			{
				console.deleteLine(1);
				console.PrintTemporaryLine(trerror.InvalidValue.Text);
				console.updatedGeneration = true;
				setWaitInput();
				return;
			}
			if (!available)
			{
				console.PrintSingleLine(parent.systemResult.ToString());
				console.PrintError(trerror.NoData.Text);
				if (state.SystemState == SystemStateCode.LoadGameOpenning_WaitInput)
				{
					beginLoadGameOpening();
					return;
				}
				beginLoadGame();
				return;
			}

			GlobalStatic.ctrlZ.OnLoad((int)parent.systemResult);

			if (!vEvaluator.LoadFrom((int)parent.systemResult))
				throw new ExeEE(trerror.UnexpectedErrorInLoaddata.Text);
			parent.deletePrevState();
			beginDataLoaded();
		}


		void endNormal()
		{
			throw new CodeEE(trerror.UnexpectedScriptEnd.Text);
		}

		void endReloaderb()
		{
			parent.loadPrevState();
			console.ReloadErbFinished();
		}

		bool writeSavedataTextFrom(int saveIndex)
		{
			EraDataResult result = vEvaluator.CheckData(saveIndex, EraSaveFileType.Normal);
			console.Print(result.DataMes);
			console.NewLine();
			return result.State == EraDataState.OK;
		}

		bool writeSavedataTextFrom_Silent(int saveIndex)
		{
			EraDataResult result = vEvaluator.CheckData(saveIndex, EraSaveFileType.Normal);
			return result.State == EraDataState.OK;
		}

}
}
