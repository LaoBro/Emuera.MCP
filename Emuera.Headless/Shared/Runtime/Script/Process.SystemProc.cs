using MinorShift.Emuera.Runtime;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Runtime.Script;
using MinorShift.Emuera.Runtime.Utils;
using MinorShift.Emuera.UI.Game;
using MinorShift.Emuera.UI.Game.Image;
using System;
using System.Collections.Generic;
using trerror = MinorShift.Emuera.Runtime.Utils.EvilMask.Lang.Error;
using trsl = MinorShift.Emuera.Runtime.Utils.EvilMask.Lang.SystemLine;

namespace MinorShift.Emuera.GameProc;

// ADR-0011 phase-1 transitional form: SystemProc holds `parent` back-reference.
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

	internal SystemProc(Process parent)
		{
			this.parent = parent;
		}

		internal void Init()
		{
			comAble = new int[parent.TrainName.Length];
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
			systemProcessDictionary[parent.state.SystemState]();
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
			parent.state.IntoFunction(call, null!, null!);
			return true;
		}

		void setWait()
		{
			parent.console.ReadAnyKey();
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
					parent.exm.VEvaluator.RESULTS = req.DefStrValue;
			}
			else if (parent.flowinputCanSkip && parent.console.MesSkip)
			{
				parent.systemResult = req.DefIntValue;
				if (parent.flowinputString)
					parent.exm.VEvaluator.RESULTS = req.DefStrValue;
			}
			parent.console.WaitInput(req);
		}

	void beginTitle()
	{
		if (parent.isCTrain)
			if (parent.ClearCommands())
				return;
		parent.skipPrint = false;
		parent.console.ResetStyle();
		parent.deleteAllPrevState();
			if (Program.AnalysisMode)
			{
				parent.console.PrintSystemLine(trsl.AnalysisCompleted.Text);
				parent.console.OutputSystemLog(Program.ExeDir + "Analysis.log");
				parent.console.noOutputLog = true;
				parent.console.PrintSystemLine(trsl.PressEnterOrClick.Text);
#if !HEADLESS
				System.Media.SystemSounds.Asterisk.Play();
#endif
				parent.console.ThrowTitleError(false);
				return;
			}
			if ((!parent.noError) && (!Config.CompatiErrorLine))
			{
				parent.console.PrintErrorButton(trsl.ExitBecauseCanNotInterpreted1.Text, null, 3);
				parent.console.PrintSystemLine(string.Format(trsl.ExitBecauseCanNotInterpreted2.Text, Config.GetConfigName(ConfigCode.CompatiErrorLine)));
				parent.console.PrintSystemLine(trsl.ExitBecauseCanNotInterpreted3.Text);
				parent.console.OutputSystemLog(Program.ExeDir + "emuera.log");
				parent.console.noOutputLog = true;
				parent.console.PrintSystemLine(trsl.PressEnterOrClick.Text);
#if !HEADLESS
				System.Media.SystemSounds.Asterisk.Play();
#endif
				parent.console.ThrowTitleError(true);
				return;
			}
			if (CallFunction("SYSTEM_TITLE", false, false))
			{
				parent.state.SystemState = SystemStateCode.Normal;
				return;
			}
			parent.console.PrintBar();
			parent.console.NewLine();
			parent.console.Alignment = DisplayLineAlignment.CENTER;
			parent.console.PrintSingleLine(parent.gamebase.ScriptTitle);
			if (parent.gamebase.ScriptVersion != 0)
				parent.console.PrintSingleLine(parent.gamebase.ScriptVersionText);
			parent.console.PrintSingleLine(parent.gamebase.ScriptAutherName);
			parent.console.PrintSingleLine("(" + parent.gamebase.ScriptYear + ")");
			parent.console.NewLine();
			parent.console.PrintSingleLine(parent.gamebase.ScriptDetail);
			parent.console.Alignment = DisplayLineAlignment.LEFT;

			parent.console.PrintBar();
			parent.console.NewLine();
			parent.console.PrintSingleLine("[0] " + Config.TitleMenuString0);
			parent.console.PrintSingleLine("[1] " + Config.TitleMenuString1);
			openingInput();
			return;
		}

		void openingInput()
		{
			setWaitInput();
			parent.state.SystemState = SystemStateCode.Openning;
			return;
		}

		void endOpenning()
		{
			if (parent.systemResult == 0)
			{
				parent.vEvaluator.ResetData();
				parent.vEvaluator.AddCharacterFromCsvNo(0);
				if (parent.gamebase.DefaultCharacter > 0)
					parent.vEvaluator.AddCharacterFromCsvNo(parent.gamebase.DefaultCharacter);
				parent.console.PrintBar();
				parent.console.NewLine();
				beginFirst();
			}
			else if (parent.systemResult == 1)
			{
				if (CallFunction("TITLE_LOADGAME", false, false))
				{
					parent.state.SystemState = SystemStateCode.Openning_TitleLoadgame;
				}
				else
				{
					beginLoadGameOpening();
				}
			}
			else
			{
				parent.console.deleteLine(1);
				parent.console.PrintTemporaryLine(trerror.InvalidValue.Text);
				parent.console.updatedGeneration = true;
				openingInput();
			}

		}

		void beginFirst()
		{
			parent.state.SystemState = SystemStateCode.Normal;
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
			parent.vEvaluator.UpdateInBeginTrain();
			parent.state.SystemState = SystemStateCode.Train_CallEventTrain;
			if (!CallFunction("EVENTTRAIN", false, true))
			{
				endCallEventTrain();
			}
		}

		void endCallEventTrain()
		{
			if (parent.vEvaluator.NEXTCOM >= 0)
			{
				parent.state.SystemState = SystemStateCode.Train_CallEventCom;
				parent.vEvaluator.SELECTCOM = parent.vEvaluator.NEXTCOM;
				parent.vEvaluator.NEXTCOM = 0;
				callEventCom();
				return;
			}
			else
			{
				if (parent.isCTrain)
					parent.skipPrint = true;
				CallFunction("SHOW_STATUS", true, false);
				parent.state.SystemState = SystemStateCode.Train_CallShowStatus;
			}
		}

		void endCallShowStatus()
		{
			parent.state.SystemState = SystemStateCode.Train_CallComAbleXX;
			lastCalledComable = -1;
			lastAddCom = -1;
			printComCount = 0;
			for (int i = 0; i < comAble.Length; i++)
				comAble[i] = -1;
			endCallComAbleXX();
		}

		string getTrainComString(int trainCode, int comNo)
		{
			string trainName = parent.TrainName[trainCode];
			return string.Format("{0}[{1,3}]", trainName, comNo);
		}

		void endCallComAbleXX()
		{
			if ((lastCalledComable >= 0) && (parent.TrainName[lastCalledComable] != null))
			{
				lastAddCom++;
				if (parent.vEvaluator.RESULT != 0)
				{
					comAble[lastAddCom] = lastCalledComable;
					if (!parent.isCTrain)
					{
						parent.console.PrintC(getTrainComString(lastCalledComable, lastAddCom), true);
						printComCount++;
						if ((Config.PrintCPerLine > 0) && (printComCount % Config.PrintCPerLine == 0))
							parent.console.PrintFlush(false);
					}
					parent.console.RefreshStrings(false);
				}
			}
			while (++lastCalledComable < parent.TrainName.Length)
			{
				if (parent.TrainName[lastCalledComable] == null)
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
						parent.console.PrintC(getTrainComString(lastCalledComable, lastAddCom), true);
						printComCount++;
						if ((Config.PrintCPerLine > 0) && (printComCount % Config.PrintCPerLine == 0))
							parent.console.PrintFlush(false);
					}
					continue;
				}
				parent.console.RefreshStrings(false);
				return;
			}
			if (lastCalledComable >= parent.TrainName.Length)
			{
				parent.state.SystemState = SystemStateCode.Train_CallShowUserCom;
				parent.console.PrintFlush(false);
				parent.console.RefreshStrings(false);
				CallFunction("SHOW_USERCOM", true, false);
			}
		}

		void endCallShowUserCom()
		{
			if (parent.skipPrint)
				parent.skipPrint = false;
			parent.vEvaluator.UpdateAfterShowUsercom();
			if (!parent.isCTrain)
			{
				setWaitInput();

				parent.state.SystemState = SystemStateCode.Train_WaitInput;
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
				parent.console.PrintSingleLine(string.Format(trerror.ExecutedCom.Text, parent.count, parent.coms.Count));
			}
			if (selectCom >= 0)
			{
				parent.vEvaluator.SELECTCOM = selectCom;
				callEventCom();
			}
			else
			{
				if (parent.isCTrain)
					parent.console.PrintSingleLine(trerror.CouldNotExecuteCom.Text);
				parent.vEvaluator.RESULT = parent.systemResult;
				parent.state.SystemState = SystemStateCode.Train_CallEventComEnd;
				CallFunction("USERCOM", true, false);
			}
		}

		void doTrain()
		{
			parent.vEvaluator.UpdateAfterShowUsercom();
			parent.vEvaluator.SELECTCOM = parent.doTrainSelectCom;
			callEventCom();
		}

		void callEventCom()
		{
			parent.vEvaluator.UpdateAfterInputCom();
			parent.state.SystemState = SystemStateCode.Train_CallEventCom;
			if (!CallFunction("EVENTCOM", false, true))
				endEventCom();
			return;
		}

		void endEventCom()
		{
			long selectCom = parent.vEvaluator.SELECTCOM;
			string comName = string.Format("COM{0}", selectCom);
			parent.state.SystemState = SystemStateCode.Train_CallComXX;
			CallFunction(comName, true, false);
		}

		void endCallComXX()
		{
			if (parent.vEvaluator.RESULT == 0)
			{
				endCallEventComEnd();
			}
			else
			{
				parent.state.SystemState = SystemStateCode.Train_CallSourceCheck;
				CallFunction("SOURCE_CHECK", true, false);
			}
		}

		void endCallSourceCheck()
		{
			parent.vEvaluator.UpdateAfterSourceCheck();
			parent.state.SystemState = SystemStateCode.Train_CallEventComEnd;
			parent.NeedWaitToEventComEnd = true;
			if (!CallFunction("EVENTCOMEND", false, true))
			{
				endCallEventComEnd();
			}
		}

		void endCallEventComEnd()
		{
			if (parent.console.LastLineIsTemporary && !parent.isCTrain && parent.needCheck)
			{
				if (parent.console.LastLineIsEmpty)
				{
					parent.console.deleteLine(2);
					parent.console.PrintTemporaryLine(trerror.InvalidValue.Text);
				}
				parent.console.updatedGeneration = true;
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
			parent.state.SystemState = SystemStateCode.Normal;
			CallFunction("EVENTEND", true, true);
		}

		void beginAblup()
		{
			if (parent.isCTrain)
				if (parent.ClearCommands())
					return;
			parent.skipPrint = false;
			parent.state.SystemState = SystemStateCode.Ablup_CallShowJuel;
			CallFunction("SHOW_JUEL", true, false);
		}

		void endCallShowJuel()
		{
			parent.state.SystemState = SystemStateCode.Ablup_CallShowAblupSelect;
			CallFunction("SHOW_ABLUP_SELECT", true, false);
		}

		void endCallShowAblupSelect()
		{
			setWaitInput();
			parent.state.SystemState = SystemStateCode.Ablup_WaitInput;
		}

		void ablupWaitInput()
		{
			if ((parent.systemResult >= 0) && (parent.systemResult < 100))
			{
				parent.state.SystemState = SystemStateCode.Ablup_CallAblupXX;
				string ablName = string.Format("ABLUP{0}", parent.systemResult);
				if (!CallFunction(ablName, false, false))
				{
					parent.console.deleteLine(1);
					parent.console.PrintTemporaryLine(trerror.InvalidValue.Text);
					parent.console.updatedGeneration = true;
					endCallShowAblupSelect();
				}
			}
			else
			{
				parent.vEvaluator.RESULT = parent.systemResult;
				parent.state.SystemState = SystemStateCode.Ablup_CallAblupXX;
				CallFunction("USERABLUP", true, false);
			}
		}

		void endCallAblupXX()
		{
			if (parent.console.LastLineIsTemporary)
			{
				if (parent.console.LastLineIsEmpty)
				{
					parent.console.deleteLine(2);
					parent.console.PrintTemporaryLine("無効な値です");
				}
				parent.console.updatedGeneration = true;
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
			parent.state.SystemState = SystemStateCode.Normal;
		}

		void beginShop()
		{
			if (parent.isCTrain)
				if (parent.ClearCommands())
					return;
			parent.skipPrint = false;
			parent.state.SystemState = SystemStateCode.Shop_CallEventShop;
			if (!CallFunction("EVENTSHOP", false, true))
			{
				endCallEventShop();
			}
		}

		void endCallEventShop()
		{
			saveTarget = -1;
			if (Config.AutoSave && parent.state.calledWhenNormal)
				beginAutoSave();
			else
			{
				parent.state.SystemState = SystemStateCode.AutoSave_Skipped;
				endAutoSaveCallSaveInfo();
			}
		}

		void beginAutoSave()
		{
			if (CallFunction("SYSTEM_AUTOSAVE", false, false))
			{
				parent.state.SystemState = SystemStateCode.AutoSave_CallUniqueAutosave;
				return;
			}
			saveTarget = AutoSaveIndex;
			parent.vEvaluator.SAVEDATA_TEXT = DateTime.Now.ToString("yyyy/MM/dd HH:mm:ss") + " ";
			parent.state.SystemState = SystemStateCode.AutoSave_CallSaveInfo;
			if (!CallFunction("SAVEINFO", false, false))
				endAutoSaveCallSaveInfo();
		}

		void endAutoSaveCallSaveInfo()
		{
			if (saveTarget == AutoSaveIndex)
			{
				if (!parent.vEvaluator.SaveTo(saveTarget, parent.vEvaluator.SAVEDATA_TEXT))
				{
					parent.console.PrintError(trerror.AutoSaveError1.Text);
					parent.console.PrintError(trerror.AutoSaveError2.Text);
					parent.console.ReadAnyKey();
				}
			}
			endAutoSave();
		}

		void endAutoSave()
		{
			if (parent.state.isBegun)
			{
				parent.state.Begin();
				return;
			}
			parent.state.SystemState = SystemStateCode.Shop_CallShowShop;
			CallFunction("SHOW_SHOP", true, false);
		}

		void endCallShowShop()
		{
			setWaitInput();
			parent.state.SystemState = SystemStateCode.Shop_WaitInput;
		}

		void shopWaitInput()
		{
			if ((parent.systemResult >= 0) && (parent.systemResult < Config.MaxShopItem))
			{
				if (parent.vEvaluator.ItemSales(parent.systemResult))
				{
					if (parent.vEvaluator.BuyItem(parent.systemResult))
					{
						parent.state.SystemState = SystemStateCode.Shop_CallEventBuy;
						if (!CallFunction("EVENTBUY", false, true))
							endCallEventBuy();
						return;
					}
					else
					{
						parent.console.deleteLine(1);
						parent.console.PrintTemporaryLine(trerror.NotEnoughMoney.Text);
					}
				}
				else
				{
					parent.console.deleteLine(1);
					parent.console.PrintTemporaryLine(trerror.OutOfStock.Text);
				}
				endCallShowShop();
				return;
			}
			else
			{
				parent.vEvaluator.RESULT = parent.systemResult;

				CallFunction("USERSHOP", true, false);
				parent.state.SystemState = SystemStateCode.Shop_CallEventBuy;
				return;
			}
		}

		void endCallEventBuy()
		{
			if (parent.console.LastLineIsTemporary)
			{
				if (parent.console.LastLineIsEmpty)
				{
					parent.console.deleteLine(2);
					parent.console.PrintTemporaryLine(trerror.InvalidValue.Text);
				}
				parent.console.updatedGeneration = true;
				endCallShowShop();
			}
			else
			{
				endAutoSave();
			}
		}


		void beginDataLoaded()
		{
			parent.state.SystemState = SystemStateCode.LoadData_CallSystemLoad;

			if (!CallFunction("SYSTEM_LOADEND", false, false))
				endSystemLoad();
		}
		void endSystemLoad()
		{
			AppContents.UnloadTempLoadedConstImageNames();
			AppContents.UnloadTempLoadedGraphicsImageNames();
			parent.state.SystemState = SystemStateCode.LoadData_CallEventLoad;
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
			parent.console.PrintSingleLine(trsl.SaveQuestion.Text);
			parent.state.SystemState = SystemStateCode.SaveGame_Begin;
			printSaveDataText();
		}

		void beginLoadGame()
		{
			parent.console.PrintSingleLine(trsl.LoadQuestion.Text);
			parent.state.SystemState = SystemStateCode.LoadGame_Begin;
			printSaveDataText();
		}

		void beginLoadGameOpening()
		{
			parent.console.PrintSingleLine(trsl.LoadQuestion.Text);
			parent.state.SystemState = SystemStateCode.LoadGameOpenning_Begin;
			printSaveDataText();
		}

		internal void LoadSilent()
		{
			parent.state.SystemState = SystemStateCode.LoadGameOpenning_Begin;

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
			if (parent.state.SystemState != SystemStateCode.SaveGame_Begin)
			{
				dataNo = AutoSaveIndex;
				if (writeSavedataTextFrom_Silent(dataNo))
					dataIsAvailable[^1] = true;
			}
			setWaitInput();
			if (parent.state.SystemState == SystemStateCode.SaveGame_Begin)
				parent.state.SystemState = SystemStateCode.SaveGame_WaitInput;
			else if (parent.state.SystemState == SystemStateCode.LoadGame_Begin)
				parent.state.SystemState = SystemStateCode.LoadGame_WaitInput;
			else
				parent.state.SystemState = SystemStateCode.LoadGameOpenning_WaitInput;
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
				parent.console.PrintFlush(false);
				parent.console.Print(string.Format(trsl.DisplaySaveSlot.Text, i * 20, i * 20 + 19));
			}
			for (int i = 0; i < 20; i++)
			{
				dataNo = page * 20 + i;
				if (dataNo == dataIsAvailable.Length - 1)
					break;
				dataIsAvailable[dataNo] = false;
				parent.console.PrintFlush(false);
				parent.console.Print(string.Format("[{0, 2}] ", dataNo));
				if (!writeSavedataTextFrom(dataNo))
					continue;
				dataIsAvailable[dataNo] = true;
			}
			for (int i = page; i < ((dataIsAvailable.Length - 2) / 20); i++)
			{
				parent.console.PrintFlush(false);
				parent.console.Print(string.Format(trsl.DisplaySaveSlot.Text, (i + 1) * 20, (i + 1) * 20 + 19));
			}
			dataIsAvailable[^1] = false;
			if (parent.state.SystemState != SystemStateCode.SaveGame_Begin)
			{
				dataNo = AutoSaveIndex;
				parent.console.PrintFlush(false);
				parent.console.Print(string.Format("[{0, 2}] ", dataNo));
				if (writeSavedataTextFrom(dataNo))
					dataIsAvailable[^1] = true;
			}
			parent.console.RefreshStrings(false);
			parent.console.PrintSingleLine("[100] 戻る");
			setWaitInput();
			if (parent.state.SystemState == SystemStateCode.SaveGame_Begin)
				parent.state.SystemState = SystemStateCode.SaveGame_WaitInput;
			else if (parent.state.SystemState == SystemStateCode.LoadGame_Begin)
				parent.state.SystemState = SystemStateCode.LoadGame_WaitInput;
			else
				parent.state.SystemState = SystemStateCode.LoadGameOpenning_WaitInput;
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
				parent.state.SystemState = SystemStateCode.SaveGame_Begin;
				printSaveDataText();
				return;
			}
			bool available;
			if ((parent.systemResult >= 0) && (parent.systemResult < dataIsAvailable.Length - 1))
				available = dataIsAvailable[parent.systemResult];
			else
			{
				parent.console.deleteLine(1);
				parent.console.PrintTemporaryLine(trerror.InvalidValue.Text);
				parent.console.updatedGeneration = true;
				setWaitInput();
				return;
			}
			saveTarget = (int)parent.systemResult;

			GlobalStatic.ctrlZ.OnSavePrepare(saveTarget);

			if (available)
			{
				parent.console.PrintSingleLine(trsl.DoYouOverwrite.Text);
				parent.console.PrintC(trsl.Yes.Text, false);
				parent.console.PrintC(trsl.No.Text, false);
				setWaitInput();
				parent.state.SystemState = SystemStateCode.SaveGame_WaitInputOverwrite;
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
				parent.console.deleteLine(1);
				parent.console.PrintTemporaryLine(trerror.InvalidValue.Text);
				parent.console.updatedGeneration = true;
				setWaitInput();
				return;
			}
			parent.vEvaluator.SAVEDATA_TEXT = DateTime.Now.ToString("yyyy/MM/dd HH:mm:ss") + " ";
			parent.state.SystemState = SystemStateCode.SaveGame_CallSaveInfo;
			if (!CallFunction("SAVEINFO", false, false))
				endCallSaveInfo();
		}

		void endCallSaveInfo()
		{
			if (!parent.vEvaluator.SaveTo(saveTarget, parent.vEvaluator.SAVEDATA_TEXT))
			{
				parent.console.PrintError(trerror.UnexpectedSaveError.Text);
				parent.console.ReadAnyKey();
			}

			GlobalStatic.ctrlZ.OnSave();

			parent.loadPrevState();
		}

		void loadGameWaitInput()
		{
			if (parent.systemResult == 100)
			{
				if (parent.state.SystemState == SystemStateCode.LoadGameOpenning_WaitInput)
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
				if (parent.state.SystemState == SystemStateCode.LoadGameOpenning_WaitInput)
					parent.state.SystemState = SystemStateCode.LoadGameOpenning_Begin;
				else
					parent.state.SystemState = SystemStateCode.LoadGame_Begin;
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
				parent.console.deleteLine(1);
				parent.console.PrintTemporaryLine(trerror.InvalidValue.Text);
				parent.console.updatedGeneration = true;
				setWaitInput();
				return;
			}
			if (!available)
			{
				parent.console.PrintSingleLine(parent.systemResult.ToString());
				parent.console.PrintError(trerror.NoData.Text);
				if (parent.state.SystemState == SystemStateCode.LoadGameOpenning_WaitInput)
				{
					beginLoadGameOpening();
					return;
				}
				beginLoadGame();
				return;
			}

			GlobalStatic.ctrlZ.OnLoad((int)parent.systemResult);

			if (!parent.vEvaluator.LoadFrom((int)parent.systemResult))
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
			parent.console.ReloadErbFinished();
		}

		bool writeSavedataTextFrom(int saveIndex)
		{
			EraDataResult result = parent.vEvaluator.CheckData(saveIndex, EraSaveFileType.Normal);
			parent.console.Print(result.DataMes);
			parent.console.NewLine();
			return result.State == EraDataState.OK;
		}

		bool writeSavedataTextFrom_Silent(int saveIndex)
		{
			EraDataResult result = parent.vEvaluator.CheckData(saveIndex, EraSaveFileType.Normal);
			return result.State == EraDataState.OK;
		}

}
}
