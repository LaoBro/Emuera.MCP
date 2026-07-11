using MinorShift.Emuera.GameView;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Runtime.Script;
using MinorShift.Emuera.Runtime.Script.Data;
using MinorShift.Emuera.Runtime.Script.Loader;
using MinorShift.Emuera.Runtime.Script.Parser;
using MinorShift.Emuera.Runtime.Script.Statements;
using MinorShift.Emuera.Runtime.Script.Statements.Expression;
using MinorShift.Emuera.Runtime.Script.Statements.Function;
using MinorShift.Emuera.Runtime.Script.Statements.Variable;
using MinorShift.Emuera.Runtime.Utils;
using MinorShift.Emuera.Runtime.Utils.PluginSystem;
using MinorShift.Emuera.UI.Game.Image;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using trerror = MinorShift.Emuera.Runtime.Utils.EvilMask.Lang.Error;
using trmb = MinorShift.Emuera.Runtime.Utils.EvilMask.Lang.MessageBox;
using trsl = MinorShift.Emuera.Runtime.Utils.EvilMask.Lang.SystemLine;

namespace MinorShift.Emuera.GameProc;

internal sealed partial class Process(EmueraConsole view)
{
	public LogicalLine getCurrentLine { get { return state.CurrentLine; } }

	/// <summary>
	/// @~~と$~~を集めたもの。CALL命令などで使う
	/// 実行順序はLogicalLine自身が保持する。
	/// </summary>
	LabelDictionary labelDic = null!;
	public LabelDictionary LabelDictionary { get { return labelDic; } }

	/// <summary>
	/// 変数全部。スクリプト中で必要になる変数は（ユーザーが直接触れないものも含め）この中にいれる
	/// </summary>
	private VariableEvaluator vEvaluator = null!;
	public VariableEvaluator VEvaluator { get { return vEvaluator; } }
	private ExpressionMediator exm = null!;
	// ADR-0011：引擎状态收归 Process 实例，GlobalStatic 对应属性转为只读转发层。
	public ExpressionMediator EMediator { get { return exm; } }
	private GameBase gamebase = null!;
	public GameBase gameBase { get { return gamebase; } }
	// ADR-0011：6 引擎字段之一，实例持有，不再写回 GlobalStatic。
	public GameBase GameBaseData { get { return gamebase; } }
	private ConstantData constantData = null!;
	// ADR-0011：6 引擎字段之一，实例持有，不再写回 GlobalStatic。
	public ConstantData ConstantData { get { return constantData; } }
	readonly EmueraConsole console = view;
	private IdentifierDictionary idDic = null!;
	// ADR-0011：6 引擎字段之一，实例持有，不再写回 GlobalStatic。
	public IdentifierDictionary IdentifierDictionary { get { return idDic; } }
	ProcessState state = null!;
	ProcessState originalState = null!;//リセットする時のために
	internal SystemProc _systemProc = null!;
	internal ScriptProc _scriptProc = null!;
	bool noError;
	//色々あって復活させてみる
	bool initialiing;
	public bool inInitializeing { get { return initialiing; } }

	public async Task<bool> Initialize(StreamWriter logWriter)
	{
		var stopWatch = new Stopwatch();
		stopWatch.Start();
		LexicalAnalyzer.UseMacro = false;
		state = new ProcessState(console);
		originalState = state;
		initialiing = true;
		try
		{
			logWriter?.WriteLine($"Proc:Init:Start {stopWatch.ElapsedMilliseconds}ms");
			logWriter?.WriteLine($"Proc:Init:Parser:Start {stopWatch.ElapsedMilliseconds}ms");
			ParserMediator.Initialize(console);
			//コンフィグファイルに関するエラーの処理（コンフィグファイルはこの関数に入る前に読込済み）
			if (ParserMediator.HasWarning)
			{
				ParserMediator.FlushWarningList();
				if (Dialog.ShowPrompt(trmb.ConfigFileError.Text, trmb.ConfigError.Text))
				{
					console.PrintSystemLine(trsl.SelectExitConfigMB.Text);
					return false;
				}
			}
			logWriter?.WriteLine($"Proc:Init:Parser:End {stopWatch.ElapsedMilliseconds}ms");

			logWriter?.WriteLine($"Proc:Init:Image:Start {stopWatch.ElapsedMilliseconds}ms");
			//リソースフォルダ読み込み
			var err = await Task.Run(() => AppContents.LoadContents(false));
			if (err != null)
			{
				ParserMediator.FlushWarningList();
				console.PrintSystemLine(trsl.ResourceReadError.Text);
				console.Print(err.ToString());
				return false;
			}
			ParserMediator.FlushWarningList();
			logWriter?.WriteLine($"Proc:Init:Image:End {stopWatch.ElapsedMilliseconds}ms");

			logWriter?.WriteLine($"Proc:Init:KeyMacro:Start {stopWatch.ElapsedMilliseconds}ms");
			//キーマクロ読み込み
			#region eee_カレントディレクトリー
			if (Config.UseKeyMacro && !Program.AnalysisMode)
			{
				//if (File.Exists(Program.ExeDir + "macro.txt"))
				if (File.Exists(Program.ExeDir + "macro.txt"))
				{
					if (Config.DisplayReport)
						console.PrintSystemLine(trsl.LoadingMacro.Text);
					//KeyMacro.LoadMacroFile(Program.ExeDir + "macro.txt");
					KeyMacro.LoadMacroFile(Program.ExeDir + "macro.txt");
				}
			}
			#endregion
			logWriter?.WriteLine($"Proc:Init:KeyMacro:End {stopWatch.ElapsedMilliseconds}ms");

			logWriter?.WriteLine($"Proc:Init:Replace:Start {stopWatch.ElapsedMilliseconds}ms");
			//_replace.csv読み込み
			if (Config.UseReplaceFile && !Program.AnalysisMode)
			{
				if (File.Exists(Program.CsvDir + "_Replace.csv"))
				{
					if (Config.DisplayReport)
						console.PrintSystemLine(trsl.LoadingReplace.Text);
					ConfigData.Current!.LoadReplaceFile(Program.CsvDir + "_Replace.csv");
					if (ParserMediator.HasWarning)
					{
						ParserMediator.FlushWarningList();
						if (Dialog.ShowPrompt(trmb.ReplaceFileError.Text, trmb.ReplaceError.Text))
						{
							console.PrintSystemLine(trsl.SelectExitReplaceMB.Text);
							return false;
						}
					}
				}
			}
			logWriter?.WriteLine($"Proc:Init:Replace:End {stopWatch.ElapsedMilliseconds}ms");

		// 候选 2 / ADR-0009：原 Config.SetReplace(ConfigData.Instance) 已删除——
		// MoneyLabel/DrawLineString 等 replace 值现在由 Config 薄视图直接从 _data 转发（经合并索引），
		// 无需再拷入 Config 静态镜像。
		//ここでBARを設定すれば、いいことに気づいた予感
		console.setStBar(Config.DrawLineString);

			logWriter?.WriteLine($"Proc:Init:Rename:Load:Start {stopWatch.ElapsedMilliseconds}ms");
			//_rename.csv読み込み
			if (Config.UseRenameFile)
			{
				if (File.Exists(Program.CsvDir + "_Rename.csv"))
				{
					if (Config.DisplayReport || Program.AnalysisMode)
						console.PrintSystemLine(trsl.LoadingRename.Text);
					ParserMediator.LoadEraExRenameFile(Program.CsvDir + "_Rename.csv");
				}
				else
					console.PrintError(trsl.MissingRename.Text);
			}
			logWriter?.WriteLine($"Proc:Init:Rename:Load:End {stopWatch.ElapsedMilliseconds}ms");

			if (!Config.DisplayReport)
			{
				console.PrintSingleLine(Config.LoadLabel);
				console.RefreshStrings(true);
			}
		// ADR-0011 决策二：引擎字段在 Initialize（composition root）中创建，经方法参数注入 Loader。
		// Loader 仅处理文件 I/O，不设 Process 反向引用（F2）。
		this.gamebase = new GameBase();
		this.constantData = new ConstantData();
		this.labelDic = new LabelDictionary();

		var loader = new Loader(console);
		if (!await loader.LoadBaseCsv(this.gamebase, this.constantData, logWriter, stopWatch))
			return false;
		console.SetWindowTitle(this.gamebase.ScriptWindowTitle);
		logWriter?.WriteLine($"Proc:Init:EtcCSV:End {stopWatch.ElapsedMilliseconds}ms");

		this.TrainName = this.constantData.GetCsvNameList(VariableCode.TRAINNAME);
		this.vEvaluator = new VariableEvaluator(this.gamebase, this.constantData);
		this.idDic = new IdentifierDictionary(this.vEvaluator.VariableData);
		StrForm.Initialize();
		VariableParser.Initialize();
		this.exm = new ExpressionMediator(this, this.vEvaluator, console);
		_systemProc = new SystemProc(this, console, this.vEvaluator, this.gamebase, this.TrainName);
		_scriptProc = new ScriptProc(this, console, this.vEvaluator, this.exm, this.idDic);

		logWriter?.WriteLine($"Proc:Init:ERH:Start {stopWatch.ElapsedMilliseconds}ms");
		if (!await loader.LoadHeadersAndScripts(this.idDic, this.exm, this.labelDic, this, logWriter, stopWatch))
			return false;
		logWriter?.WriteLine($"Proc:Init:ERB:End {stopWatch.ElapsedMilliseconds}ms");

		_systemProc.Init();
		initialiing = false;

			logWriter?.WriteLine($"Proc:Init:End {stopWatch.ElapsedMilliseconds}ms");
		}
		catch (Exception e)
		{
			handleException(e, null!, true);
			console.PrintSystemLine(trsl.ErhLoadingError.Text);
			return false;
		}
		if (labelDic == null)
		{
			return false;
		}
		state.Begin(BeginType.TITLE);
		return true;
	}

	public async Task ReloadErb()
	{
		await Preload.Load(Program.ErbDir);
		await Preload.Load(Program.CsvDir);
		saveCurrentState(false);
		state.SystemState = SystemStateCode.System_Reloaderb;
		ErbLoader loader = new(console, exm, this);
		await loader.LoadErbDir(Program.ErbDir, false, labelDic);
		console.ReadAnyKey();
	}

	public async Task ReloadPartialErb(List<string> paths)
	{
		saveCurrentState(false);
		state.SystemState = SystemStateCode.System_Reloaderb;
		await Preload.Load(paths);
		var loader = new ErbLoader(console, exm, this);
		await loader.LoadErbList(paths, labelDic);
		console.ReadAnyKey();
	}

	public void SetCommnds(long count)
	{
		coms = new List<long>((int)count);
		isCTrain = true;
		long[] selectcom = vEvaluator.SELECTCOM_ARRAY;
		if (count >= selectcom.Length)
		{
			throw new CodeEE(trerror.CalltrainArgMoreThanSelectcom.Text);
		}
		for (int i = 0; i < (int)count; i++)
		{
			coms.Add(selectcom[i + 1]);
		}
	}

	public bool ClearCommands()
	{
		coms.Clear();
		count = 0;
		isCTrain = false;
		skipPrint = true;
		return _systemProc.CallFunction("CALLTRAINEND", false, false);
	}
	#region EE_INPUTMOUSEKEYのボタン対応
	// public void InputResult5(int r0, int r1, int r2, int r3, int r4)
	public void InputResult5(int r0, int r1, int r2, int r3, int r4, long r5)
	{
		long[] result = vEvaluator.RESULT_ARRAY;
		result[0] = r0;
		result[1] = r1;
		result[2] = r2;
		result[3] = r3;
		result[4] = r4;
		result[5] = r5;
	}
	#endregion
	public void InputInteger(long i)
	{
		GlobalStatic.ctrlZ.Add(i.ToString());
		vEvaluator.RESULT = i;
	}
	#region EM_私家版_INPUT系機能拡張
	public void InputInteger(long idx, long i)
	{
		if (idx < vEvaluator.RESULT_ARRAY.Length)
			vEvaluator.RESULT_ARRAY[idx] = i;
	}
	public void InputString(long idx, string i)
	{
		if (idx < vEvaluator.RESULT_ARRAY.Length)
			vEvaluator.RESULTS_ARRAY[idx] = i;
	}
	#endregion
	public void InputSystemInteger(long i)
	{
		GlobalStatic.ctrlZ.Add(i.ToString());
		systemResult = i;
	}
	public void InputString(string s)
	{
		GlobalStatic.ctrlZ.Add(s);
		vEvaluator.RESULTS = s;
	}

	readonly Stopwatch startTime = new();

	public void DoScript()
	{
		startTime.Restart();
		state.lineCount = 0;
		bool systemProcRunning = true;
		try
		{
			while (true)
			{
				methodStack = 0;
				systemProcRunning = true;
				while (state.ScriptEnd && console.IsRunning)
					_systemProc.Run();
				if (!console.IsRunning)
					break;
				systemProcRunning = false;
				_scriptProc.Run();
			}
		}
		catch (Exception ec)
		{
#if HEADLESS
			// I-11：HeadlessConsole.Close()/ExitApplication() 抛 GameExitException 绕过
			// Environment.Exit(0)。此处必须 rethrow 让异常穿透到 Session/HeadlessRunner
			// 的 catch，否则 GameExitException 被当脚本错误处理 → Error 状态 → 僵尸 session。
			if (ec is GameExitException)
				throw;
#endif
			// ERB THROW / 除零等脚本级异常（CodeEE）在此处被 handleException 捕获并处理，
			// 不会传播到协议层（AgentJsonlProtocol.StepAsync / AgentCliProtocol.RunCliLoop）
			// 的 catch 块。StepAsync catch 是防御性兜底，仅捕获 Process 未预料的 C# 异常。
			LogicalLine currentLine = state.ErrorLine;
			if (currentLine != null && currentLine is NullLine)
				currentLine = null!;
			if (systemProcRunning)
				handleExceptionInSystemProc(ec, currentLine!, true);
			else
				handleException(ec, currentLine!, true);
		}
	}

	public void BeginTitle()
	{
		vEvaluator.ResetData();
		state = originalState;
		state.Begin(BeginType.TITLE);
	}

	public void UpdateCheckInfiniteLoopState()
	{
		startTime.Restart();
		state.lineCount = 0;
	}

	public void saveCurrentState(bool single) => _scriptProc.SaveCurrentState(single);

	public void loadPrevState() => _scriptProc.LoadPrevState();

	public ProcessState getCurrentState => _scriptProc.GetCurrentState;

	public void DoDebugNormalFunction(InstructionLine func, bool munchkin) => _scriptProc.DoDebugNormalFunction(func, munchkin);

	public void LoadSilent() => _systemProc.LoadSilent();

	private void checkInfiniteLoop()
	{
		var elapsedTime = startTime.ElapsedMilliseconds;
		if (elapsedTime < Config.InfiniteLoopAlertTime)
			return;
		LogicalLine currentLine = state.CurrentLine;
		if ((currentLine == null) || (currentLine is NullLine))
			return;//現在の行が特殊な状態ならスルー
		if (!console.Enabled)
			return;
		string text = string.Format(
			trmb.TooLongLoop.Text,
			currentLine.Position!.Value.Filename, currentLine.Position!.Value.LineNo, state.lineCount, elapsedTime);
#if HEADLESS
		// T-021：Headless/Server 模式无交互对话框，超时即终止脚本。
		// GameExitException 穿透 DoScript → RunEmueraProgram → 协议层 → Session/HeadlessRunner
		// 的 catch + finally，触发正常清理（BuildFinalTurn / IO.Close / GlobalStatic.Reset）。
		Console.Error.WriteLine($"[script-timeout] {text}");
		throw new GameExitException();
#else
		string caption = string.Format(trmb.InfiniteLoop.Text);
		if (Dialog.ShowPrompt(text, caption))
		{
			throw new CodeEE(trerror.SelectExitInfiniteLoopMB.Text);
		}
		else
		{
			state.lineCount = 0;
			startTime.Restart();
		}
#endif
	}

	int methodStack;
	public SingleTerm GetValue(SuperUserDefinedMethodTerm udmt)
	{
		methodStack++;
		if (methodStack > 100)
		{
			//StackOverflowExceptionはcatchできない上に再現性がないので発生前に一定数で打ち切る。
			//環境によっては100以前にStackOverflowExceptionがでるかも？
			throw new CodeEE(trerror.OverflowFuncStack.Text);
		}
		SingleTerm ret = null!;
		int temp_current = state.currentMin;
		state.currentMin = state.functionCount;
		udmt.Call.updateRetAddress(state.CurrentLine);
		try
		{
			state.IntoFunction(udmt.Call, udmt.Argument, exm);
			//do whileの中でthrow されたエラーはここではキャッチされない。
			//#functionを全て抜けてDoScriptでキャッチされる。
			_scriptProc.Run();
			ret = state.MethodReturnValue;
		}
		finally
		{
			if (udmt.Call.TopLabel.hasPrivDynamicVar)
				udmt.Call.TopLabel.ScopeOut();
			//1756beta2+v3:こいつらはここにないとデバッグコンソールで式中関数が事故った時に大事故になる
			state.currentMin = temp_current;
			methodStack--;
		}
		return ret;
	}

	public void clearMethodStack()
	{
		methodStack = 0;
	}

	public int MethodStack()
	{
		return methodStack;
	}

	public ScriptPosition? GetRunningPosition()
	{
		LogicalLine line = state.ErrorLine;
		if (line == null)
			return null!;
		return line.Position;
	}
	/*
			private readonly string scaningScope = null;
			private string GetScaningScope()
			{
				if (scaningScope != null)
					return scaningScope;
				return state.Scope;
			}
	*/
	public LogicalLine scaningLine = null!;
	internal LogicalLine GetScaningLine()
	{
		if (scaningLine != null)
			return scaningLine;
		LogicalLine line = state.ErrorLine;
		if (line == null)
			return default!;
		return line;
	}


	private void handleExceptionInSystemProc(Exception exc, LogicalLine current, bool playSound)
	{
		console.ThrowError(playSound);
		if (exc is CodeEE)
		{
			console.PrintError(string.Format(trerror.FuncEndError.Text, AssemblyData.EmueraVersionText));
			console.PrintError(exc.Message);
		}
		else if (exc is ExeEE)
		{
			console.PrintError(string.Format(trerror.FuncEndEmueraError.Text, AssemblyData.EmueraVersionText));
			console.PrintError(exc.Message);
		}
		else
		{
			console.PrintError(string.Format(trerror.FuncEndUnexpectedError.Text, AssemblyData.EmueraVersionText));
			console.PrintError(exc.GetType().ToString() + ":" + exc.Message);
			string[] stack = exc.StackTrace!.Split('\n');
			for (int i = 0; i < stack.Length; i++)
			{
				console.PrintError(stack[i]);
			}
		}
	}

	private void handleException(Exception exc, LogicalLine current, bool playSound)
	{
		console.ThrowError(playSound);
		ScriptPosition? position = null;
		if ((exc is EmueraException ee) && (ee.Position != null))
			position = ee.Position;
		else if ((current != null) && (current.Position != null))
			position = current.Position;
		string posString = "";
		if (position != null)
		{
			if (position.Value.LineNo >= 0)
				posString = string.Format(trerror.ErrorFileAndLine.Text, position.Value.Filename, position.Value.LineNo.ToString());
			else
				posString = string.Format(trerror.ErrorFile.Text, position.Value.Filename);

		}
		if (exc is CodeEE)
		{
			if (position != default)
			{
				if (current is InstructionLine procline && procline.FunctionCode == FunctionCode.THROW)
				{
					console.PrintErrorButton(string.Format(trerror.HasThrow.Text, posString), position);
					printRawLine(position);
					console.PrintError(string.Format(trerror.ThrowMessage.Text, exc.Message));
				}
				else
				{
					console.PrintErrorButton(string.Format(trerror.HasError.Text, posString, AssemblyData.EmueraVersionText), position);
					printRawLine(position);
					console.PrintError(string.Format(trerror.ErrorMessage.Text, exc.Message));
				}
				console!.PrintError(string.Format(trerror.ErrorInFunc.Text, current!.ParentLabelLine!.LabelName, current!.ParentLabelLine!.Position!.Value.Filename, current!.ParentLabelLine!.Position!.Value.LineNo.ToString()));
				console.PrintError(trerror.FuncCallStack.Text);
				LogicalLine parent;
				int depth = 0;
				while ((parent = state.GetReturnAddressSequensial(depth++)) != null)
				{
					if (parent.Position != null)
					{
						console.PrintErrorButton(string.Format(trerror.ErrorFuncStack.Text, parent.Position!.Value.Filename, parent.Position!.Value.LineNo.ToString(), parent.ParentLabelLine.LabelName), parent.Position);
					}
				}
			}
			else
			{
				console.PrintError(string.Format(trerror.HasError.Text, posString, AssemblyData.EmueraVersionText));
				console.PrintError(exc.Message);
			}
		}
		else if (exc is ExeEE)
		{
			console.PrintError(string.Format(trerror.HasEmueraError.Text, posString, AssemblyData.EmueraVersionText));
			console.PrintError(exc.Message);
		}
		else
		{
			console.PrintError(string.Format(trerror.HasUnexpectedError.Text, posString, AssemblyData.EmueraVersionText));
			console.PrintError(exc.GetType().ToString() + ":" + exc.Message);
			string[] stack = exc.StackTrace!.Split('\n');
			for (int i = 0; i < stack.Length; i++)
			{
				console.PrintError(stack[i]);
			}
		}
	}

	public void printRawLine(ScriptPosition? position)
	{
		string str = getRawTextFormFilewithLine(position);
		if (!string.IsNullOrEmpty(str))
			console.PrintError(str);
	}

	public static string getRawTextFormFilewithLine(ScriptPosition? position)
	{
		string extents = position!.Value.Filename[^4..].ToLower();
		if (extents == ".erb")
		{
			return File.Exists(Program.ErbDir + position!.Value.Filename)
				? position!.Value.LineNo > 0 ? File.ReadLines(Program.ErbDir + position!.Value.Filename, EncodingHandler.DetectEncoding(Program.ErbDir + position!.Value.Filename)).Skip(position!.Value.LineNo - 1).First() : ""
				: "";
		}
		else if (extents == ".csv")
		{
			return File.Exists(Program.CsvDir + position!.Value.Filename)
				? position!.Value.LineNo > 0 ? File.ReadLines(Program.CsvDir + position!.Value.Filename, EncodingHandler.DetectEncoding(Program.ErbDir + position!.Value.Filename)).Skip(position!.Value.LineNo - 1).First() : ""
				: "";
		}
		else
			return "";
	}

	private void deletePrevState()
	{
		_scriptProc.DeletePrevState();
	}

	private void deleteAllPrevState()
	{
		_scriptProc.DeleteAllPrevState();
	}

	/// <summary>
	/// ADR-0011 决策二：引擎数据加载子模块（F2 深模块）。
	/// 字段在 <see cref="Initialize"/>（composition root）中创建；Loader 仅处理文件 I/O，
	/// 经方法参数接收所需对象。不存储 Process 反向引用字段（F2），但方法签名
	/// 暂仍接收 Process 以传递给 ErhLoader/ErbLoader（二者尚未 F2）。
	/// </summary>
	internal sealed class Loader
	{
		private readonly EmueraConsole console;
		internal Loader(EmueraConsole console) { this.console = console; }

		/// <summary>加载 GAMEBASE.CSV 与其余全部 CSV 数据。</summary>
		internal async Task<bool> LoadBaseCsv(GameBase gamebase, ConstantData constantData,
			StreamWriter? logWriter, Stopwatch stopWatch)
		{
			logWriter?.WriteLine($"Proc:Init:MainCSV:Start {stopWatch.ElapsedMilliseconds}ms");
			if (!await Task.Run(() => gamebase.LoadGameBaseCsv(Program.CsvDir + "GAMEBASE.CSV")))
			{
				ParserMediator.FlushWarningList();
				console.PrintSystemLine(trsl.GamebaseError.Text);
				return false;
			}
			logWriter?.WriteLine($"Proc:Init:MainCSV:End {stopWatch.ElapsedMilliseconds}ms");

			constantData.LoadData(Program.CsvDir, console, Config.DisplayReport);
			return true;
		}

		/// <summary>加载 ERH 头文件与 ERB 脚本文件。</summary>
		internal async Task<bool> LoadHeadersAndScripts(IdentifierDictionary idDic, ExpressionMediator exm,
			LabelDictionary labelDic, Process process, StreamWriter? logWriter, Stopwatch stopWatch)
		{
			ErhLoader hLoader = new(console, idDic, process);

			LexicalAnalyzer.UseMacro = false;

			PluginManager.GetInstance().SetParent(process, process.state, exm);
			PluginManager.GetInstance().LoadPlugins();

			if (!await Task.Run(() => hLoader.LoadHeaderFiles(Program.ErbDir, Config.DisplayReport)))
			{
				ParserMediator.FlushWarningList();
				console.PrintSystemLine("");
				return false;
			}
			LexicalAnalyzer.UseMacro = idDic.UseMacro();
			logWriter?.WriteLine($"Proc:Init:ERH:End {stopWatch.ElapsedMilliseconds}ms");

			logWriter?.WriteLine($"Proc:Init:ERB:Start {stopWatch.ElapsedMilliseconds}ms");
			var erbLoader = new ErbLoader(console, exm, process);
			if (Program.AnalysisMode)
				process.noError = await erbLoader.LoadErbList(Program.AnalysisFiles, labelDic);
			else
				process.noError = await erbLoader.LoadErbDir(Program.ErbDir, Config.DisplayReport, labelDic);
			return true;
		}
	}
}
