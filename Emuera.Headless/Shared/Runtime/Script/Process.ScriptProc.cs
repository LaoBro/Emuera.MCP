using MinorShift.Emuera.GameData.Variable;
using MinorShift.Emuera.GameProc.Function;
using MinorShift.Emuera.GameView;
using MinorShift.Emuera.Primitives;
using MinorShift.Emuera.UI.Game;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Runtime.Script;
using MinorShift.Emuera.Runtime.Script.Statements;
using MinorShift.Emuera.Runtime.Script.Statements.Expression;
using MinorShift.Emuera.Runtime.Script.Statements.Variable;
using MinorShift.Emuera.Runtime.Utils;
using System;
using System.Collections.Generic;
using trerror = MinorShift.Emuera.Runtime.Utils.EvilMask.Lang.Error;

namespace MinorShift.Emuera.GameProc;

// ADR-0011 Phase 2: IVariableEvaluator, EmueraConsole injected; prevStateList/saveSkip/userDefinedSkip moved in.
// Remaining `parent.*` bridge: state, exm, skipPrint, isCTrain, count, coms, TrainName, gameBase, IdentifierDictionary.
// TODO: Narrow per-instruction-family to F2 (no parent back-ref). Target families:
//       print/printl → output-only via injected console;
//       input/inputint → SessionIO abstraction;
//       call/return → CallFunction already F2 via CalledFunction;
//       arithmetic/assign → pure ExpressionMediator + VEvaluator.
internal sealed partial class Process
{
	internal sealed class ScriptProc
	{
		readonly Process parent;
		readonly EmueraConsole console;
		readonly IVariableEvaluator vEvaluator;
		List<ProcessState> prevStateList = [];
		bool saveSkip;
		bool userDefinedSkip;

		internal ScriptProc(Process parent, EmueraConsole console, IVariableEvaluator vEvaluator)
		{
			this.parent = parent;
			this.console = console;
			this.vEvaluator = vEvaluator;
		}

		public void Run()
		{
			int loopIterCount = 0;
			while (true)
			{
				parent.state.ShiftNextLine();
				if (Config.InfiniteLoopAlertTime > 0 && (++loopIterCount % 10000 == 0))
					parent.checkInfiniteLoop();
				LogicalLine line = parent.state.CurrentLine;
				if (line.IsError)
					throw new CodeEE(line.ErrMes);
				else if (line is InstructionLine func)
				{
					if (!Program.DebugMode && func.Function.IsDebug())
					{
						continue;
					}
					if (func.Argument == null)
					{
						ArgumentParser.SetArgumentTo(func);
						if (func.IsError)
							throw new CodeEE(func.ErrMes);
					}
					if (parent.skipPrint && func.Function.IsPrint())
					{
						if (userDefinedSkip && func.Function.IsInput())
						{
							console.PrintError(trerror.SkipdispInputError1.Text);
							console.PrintError(trerror.SkipdispInputError2.Text);
							throw new CodeEE(trerror.SkipdispInputError3.Text);
						}
						continue;
					}
					if (func.Function.Instruction != null)
						func.Function.Instruction.DoInstruction(parent.exm, func, parent.state);
					else if (func.Function.IsFlowContorol())
						doFlowControlFunction(func);
					else
						doNormalFunction(func);
				}
				else if ((line is NullLine) || (line is FunctionLabelLine))
				{
					if (!parent.state.IsFunctionMethod)
						vEvaluator.RESULT = 0;
					parent.state.Return(0);
				}
				else if (line is GotoLabelLine)
					continue;
				else if (line is InvalidLine)
				{
					if (string.IsNullOrEmpty(line.ErrMes))
						throw new CodeEE(trerror.DoFailedLine.Text);
					else
						throw new CodeEE(line.ErrMes);
				}
				if (!console.IsRunning || parent.state.ScriptEnd)
					return;
			}
		}

		public void DoDebugNormalFunction(InstructionLine func, bool munchkin)
		{
			if (func.Function.Instruction != null)
				func.Function.Instruction.DoInstruction(parent.exm, func, parent.state);
			else
				doNormalFunction(func);
			if (munchkin)
				vEvaluator.IamaMunchkin();
		}

		void doNormalFunction(InstructionLine func)
		{
			long iValue = 0;
			string str = null!;
			AExpression term = null!;
			switch (func.FunctionCode)
			{

				case FunctionCode.PRINTBUTTON:
					{
						if (parent.skipPrint)
							break;
						parent.exm.Console.UseUserStyle = true;
						parent.exm.Console.UseSetColorStyle = true;
						SpButtonArgument bArg = (SpButtonArgument)func.Argument;
						str = bArg.PrintStrTerm.GetStrValue(parent.exm);
						str = str.Replace("\n", "");
						if (bArg.ButtonWord.GetOperandType() == typeof(long))
							parent.exm.Console.PrintButton(str, bArg.ButtonWord.GetIntValue(parent.exm));
						else
							parent.exm.Console.PrintButton(str, bArg.ButtonWord.GetStrValue(parent.exm));
					}
					break;
				case FunctionCode.PRINTBUTTONC:
				case FunctionCode.PRINTBUTTONLC:
					{
						if (parent.skipPrint)
							break;
						parent.exm.Console.UseUserStyle = true;
						parent.exm.Console.UseSetColorStyle = true;
						SpButtonArgument bArg = (SpButtonArgument)func.Argument;
						str = bArg.PrintStrTerm.GetStrValue(parent.exm);
						str = str.Replace("\n", "");
						bool isRight = (func.FunctionCode == FunctionCode.PRINTBUTTONC);
						if (bArg.ButtonWord.GetOperandType() == typeof(long))
							parent.exm.Console.PrintButtonC(str, bArg.ButtonWord.GetIntValue(parent.exm), isRight);
						else
							parent.exm.Console.PrintButtonC(str, bArg.ButtonWord.GetStrValue(parent.exm), isRight);
					}
					break;
				case FunctionCode.PRINTPLAIN:
				case FunctionCode.PRINTPLAINFORM:
					{
						if (parent.skipPrint)
							break;
						parent.exm.Console.UseUserStyle = true;
						parent.exm.Console.UseSetColorStyle = true;
						term = ((ExpressionArgument)func.Argument).Term;
						parent.exm.Console.PrintPlain(term.GetStrValue(parent.exm));
					}
					break;
				case FunctionCode.DRAWLINE:
					if (parent.skipPrint)
						break;
					parent.exm.Console.PrintBar();
					parent.exm.Console.NewLine();
					break;
				case FunctionCode.DRAWLINEFORM:
					{
						if (parent.skipPrint)
							break;
						term = ((ExpressionArgument)func.Argument).Term;
						str = term.GetStrValue(parent.exm);
						parent.exm.Console.printCustomBar(str, false);
						parent.exm.Console.NewLine();
					}
					break;
				case FunctionCode.PRINT_ABL:
				case FunctionCode.PRINT_TALENT:
				case FunctionCode.PRINT_MARK:
				case FunctionCode.PRINT_EXP:
					{
						if (parent.skipPrint)
							break;
						ExpressionArgument intExpArg = (ExpressionArgument)func.Argument;
						long target = intExpArg.Term.GetIntValue(parent.exm);
						parent.exm.Console.Print(vEvaluator.GetCharacterDataString(target, func.FunctionCode));
						parent.exm.Console.NewLine();
					}
					break;
				case FunctionCode.PRINT_PALAM:
					{
						if (parent.skipPrint)
							break;
						ExpressionArgument intExpArg = (ExpressionArgument)func.Argument;
						long target = intExpArg.Term.GetIntValue(parent.exm);
						int count = 0;
						for (int i = 0; i < 100; i++)
						{
							string printStr = vEvaluator.GetCharacterParamString(target, i);
							if (printStr != null)
							{
								parent.exm.Console.PrintC(printStr, true);
								count++;
								if ((Config.PrintCPerLine > 0) && (count % Config.PrintCPerLine == 0))
									parent.exm.Console.PrintFlush(false);
							}
						}
						parent.exm.Console.PrintFlush(false);
						parent.exm.Console.RefreshStrings(false);
					}
					break;
				case FunctionCode.PRINT_ITEM:
					if (parent.skipPrint)
						break;
					parent.exm.Console.Print(vEvaluator.GetHavingItemsString());
					parent.exm.Console.NewLine();
					break;
				case FunctionCode.PRINT_SHOPITEM:
					{
						if (parent.skipPrint)
							break;
						int length = Math.Min(vEvaluator.ITEMSALES.Length, vEvaluator.ITEMNAME.Length);
						if (length > vEvaluator.ITEMPRICE.Length)
							length = vEvaluator.ITEMPRICE.Length;
						int count = 0;
						for (int i = 0; i < length; i++)
						{
							if (vEvaluator.ItemSales(i))
							{
								string printStr = vEvaluator.ITEMNAME[i];
								if (printStr == null)
									printStr = "";
								long price = vEvaluator.ITEMPRICE[i];
								if (Config.MoneyFirst)
									parent.exm.Console.PrintC(string.Format("[{2}] {0}({3}{1})", printStr, price, i, Config.MoneyLabel), false);
								else
									parent.exm.Console.PrintC(string.Format("[{2}] {0}({1}{3})", printStr, price, i, Config.MoneyLabel), false);
								count++;
								if ((Config.PrintCPerLine > 0) && (count % Config.PrintCPerLine == 0))
									parent.exm.Console.PrintFlush(false);
							}
						}
						parent.exm.Console.PrintFlush(false);
						parent.exm.Console.RefreshStrings(false);
					}
					break;
				case FunctionCode.UPCHECK:
					vEvaluator.UpdateInUpcheck(parent.exm.Console, parent.skipPrint);
					break;
				case FunctionCode.CUPCHECK:
					{
						ExpressionArgument intExpArg = (ExpressionArgument)func.Argument;
						long target = intExpArg.Term.GetIntValue(parent.exm);
						vEvaluator.CUpdateInUpcheck(parent.exm.Console, target, parent.skipPrint);
					}
					break;
				case FunctionCode.DELALLCHARA:
					{
						vEvaluator.DelAllCharacter();
						break;
					}
				case FunctionCode.PICKUPCHARA:
					{
						ExpressionArrayArgument intExpArg = (ExpressionArrayArgument)func.Argument;
						long[] NoList = new long[intExpArg.TermList.Length];
						long charaNum = vEvaluator.CHARANUM;
						for (int i = 0; i < intExpArg.TermList.Length; i++)
						{
							AExpression term_i = intExpArg.TermList[i];
							NoList[i] = term_i.GetIntValue(parent.exm);
							if (!(term_i is VariableTerm) || ((((VariableTerm)term_i).Identifier.Code != VariableCode.MASTER) && (((VariableTerm)term_i).Identifier.Code != VariableCode.ASSI) && (((VariableTerm)term_i).Identifier.Code != VariableCode.TARGET)))
								if (NoList[i] < 0 || NoList[i] >= charaNum)
									throw new CodeEE(string.Format(trerror.OoRPickupcharaArg.Text, (i + 1).ToString(), NoList[i].ToString()));
						}
						vEvaluator.PickUpChara(NoList);
					}
					break;
				case FunctionCode.ADDDEFCHARA:
					{
						if ((func.ParentLabelLine != null) && (func.ParentLabelLine.LabelName != "SYSTEM_TITLE"))
							throw new CodeEE(trerror.CanNotUseOutsideSystemtitle.Text);
						vEvaluator.AddCharacterFromCsvNo(0);
						if (parent.gameBase.DefaultCharacter > 0)
							vEvaluator.AddCharacterFromCsvNo(parent.gameBase.DefaultCharacter);
						break;
					}
				case FunctionCode.PUTFORM:
					{
						term = ((ExpressionArgument)func.Argument).Term;
						str = term.GetStrValue(parent.exm);
						if (vEvaluator.SAVEDATA_TEXT != null)
							vEvaluator.SAVEDATA_TEXT += str;
						else
							vEvaluator.SAVEDATA_TEXT = str;
						break;
					}
				case FunctionCode.QUIT:
					parent.exm.Console.Quit();
					break;
				case FunctionCode.QUIT_AND_RESTART:
					Program.rebootFlag = true;
					parent.exm.Console.Quit();
					break;
				case FunctionCode.FORCE_QUIT:
					parent.exm.Console.ForceQuit();
					break;
				case FunctionCode.FORCE_QUIT_AND_RESTART:
					Program.rebootFlag = true;
					parent.exm.Console.ForceQuit();
					break;

				case FunctionCode.VARSIZE:
					{
						SpVarsizeArgument versizeArg = (SpVarsizeArgument)func.Argument;
						VariableToken varID = versizeArg.VariableID;
						vEvaluator.VarSize(varID);
					}
					break;
				case FunctionCode.SAVEDATA:
					{
						SpSaveDataArgument spSavedataArg = (SpSaveDataArgument)func.Argument;
						long target = spSavedataArg.Target.GetIntValue(parent.exm);
						if (target < 0)
							throw new CodeEE(string.Format(trerror.SavedataArgIsNegative.Text, target.ToString()));
						else if (target > int.MaxValue)
							throw new CodeEE(string.Format(trerror.TooLargeSavedataArg.Text, target.ToString()));
						string savemes = spSavedataArg.StrExpression.GetStrValue(parent.exm);
						if (savemes.Contains('\n'))
							throw new CodeEE(trerror.SavetextContainNewLineCharacter.Text);
						if (!vEvaluator.SaveTo((int)target, savemes))
						{
							console.PrintError(trerror.UnexpectedErrorInSavedata.Text);
						}
					}
					break;

				case FunctionCode.POWER:
					{
						SpPowerArgument powerArg = (SpPowerArgument)func.Argument;
						double x = powerArg.X.GetIntValue(parent.exm);
						double y = powerArg.Y.GetIntValue(parent.exm);
						double pow = Math.Pow(x, y);
						if (double.IsNaN(pow))
							throw new CodeEE(trerror.PowerResultNonNumeric.Text);
						else if (double.IsInfinity(pow))
							throw new CodeEE(trerror.PowerResultInfinite.Text);
						else if ((pow >= long.MaxValue) || (pow <= long.MinValue))
							throw new CodeEE(string.Format(trerror.PowerResultOverflow.Text, pow.ToString()));
						powerArg.VariableDest.SetValue((long)pow, parent.exm);
						break;
					}
				case FunctionCode.SWAP:
					{
						SpSwapVarArgument arg = (SpSwapVarArgument)func.Argument;
						FixedVariableTerm vTerm1 = arg.var1.GetFixedVariableTerm(parent.exm);
						FixedVariableTerm vTerm2 = arg.var2.GetFixedVariableTerm(parent.exm);
						if (vTerm1.GetOperandType() != vTerm2.GetOperandType())
							throw new CodeEE(trerror.VarsTypeDifferent.Text);
						if (vTerm1.GetOperandType() == typeof(long))
						{
							long temp = vTerm1.GetIntValue(parent.exm);
							vTerm1.SetValue(vTerm2.GetIntValue(parent.exm), parent.exm);
							vTerm2.SetValue(temp, parent.exm);
						}
						else if (arg.var1.GetOperandType() == typeof(string))
						{
							string temps = vTerm1.GetStrValue(parent.exm);
							vTerm1.SetValue(vTerm2.GetStrValue(parent.exm), parent.exm);
							vTerm2.SetValue(temps, parent.exm);
						}
						else
						{
							throw new CodeEE(trerror.UnknownVarType.Text);
						}
						break;
					}
				case FunctionCode.GETTIME:
					{
						long date = DateTime.Now.Year;
						date = date * 100 + DateTime.Now.Month;
						date = date * 100 + DateTime.Now.Day;
						date = date * 100 + DateTime.Now.Hour;
						date = date * 100 + DateTime.Now.Minute;
						date = date * 100 + DateTime.Now.Second;
						date = date * 1000 + DateTime.Now.Millisecond;
						vEvaluator.RESULT = date;
						vEvaluator.RESULTS = DateTime.Now.ToString("yyyy/MM/dd HH:mm:ss");
					}
					break;
				case FunctionCode.SETCOLOR:
					{
						SpColorArgument colorArg = (SpColorArgument)func.Argument;
						long colorR;
						long colorG;
						long colorB;
						if (colorArg.RGB != null)
						{
							long colorRGB = colorArg.RGB.GetIntValue(parent.exm);
							colorR = (colorRGB & 0xFF0000) >> 16;
							colorG = (colorRGB & 0x00FF00) >> 8;
							colorB = colorRGB & 0x0000FF;
						}
						else
						{
							colorR = colorArg.R.GetIntValue(parent.exm);
							colorG = colorArg.G.GetIntValue(parent.exm);
							colorB = colorArg.B.GetIntValue(parent.exm);
							if ((colorR < 0) || (colorG < 0) || (colorB < 0))
								throw new CodeEE(trerror.SetcolorArgLessThan0.Text);
							if ((colorR > 255) || (colorG > 255) || (colorB > 255))
								throw new CodeEE(trerror.SetcolorArgOver255.Text);
						}
						EmuColor c = EmuColor.FromArgb((int)colorR, (int)colorG, (int)colorB);
						parent.exm.Console.SetStringStyle(c);
					}
					break;
				case FunctionCode.SETCOLORBYNAME:
					{
						string colorName = func.Argument.ConstStr;
						EmuColor c = EmuColor.FromName(colorName);
						parent.exm.Console.SetStringStyle(c);
					}
					break;
				case FunctionCode.SETBGCOLOR:
					{
						SpColorArgument colorArg = (SpColorArgument)func.Argument;
						long colorR;
						long colorG;
						long colorB;
						if (colorArg.IsConst)
						{
							long colorRGB = colorArg.ConstInt;
							colorR = (colorRGB & 0xFF0000) >> 16;
							colorG = (colorRGB & 0x00FF00) >> 8;
							colorB = colorRGB & 0x0000FF;
						}
						else if (colorArg.RGB != null)
						{
							long colorRGB = colorArg.RGB.GetIntValue(parent.exm);
							colorR = (colorRGB & 0xFF0000) >> 16;
							colorG = (colorRGB & 0x00FF00) >> 8;
							colorB = colorRGB & 0x0000FF;
						}
						else
						{
							colorR = colorArg.R.GetIntValue(parent.exm);
							colorG = colorArg.G.GetIntValue(parent.exm);
							colorB = colorArg.B.GetIntValue(parent.exm);
							if ((colorR < 0) || (colorG < 0) || (colorB < 0))
								throw new CodeEE(trerror.SetcolorArgLessThan0.Text);
							if ((colorR > 255) || (colorG > 255) || (colorB > 255))
								throw new CodeEE(trerror.SetcolorArgOver255.Text);
						}
						EmuColor c = EmuColor.FromArgb((int)colorR, (int)colorG, (int)colorB);
						parent.exm.Console.SetBgColor(c);
					}
					break;
				case FunctionCode.SETBGCOLORBYNAME:
					{
						string colorName = func.Argument.ConstStr;
						EmuColor c = EmuColor.FromName(colorName);
						parent.exm.Console.SetBgColor(c);
					}
					break;
				case FunctionCode.FONTSTYLE:
					{
						EmuFontStyle fs = EmuFontStyle.Regular;
						if (func.Argument.IsConst)
							iValue = func.Argument.ConstInt;
						else
							iValue = ((ExpressionArgument)func.Argument).Term.GetIntValue(parent.exm);
						if ((iValue & 1) != 0)
							fs |= EmuFontStyle.Bold;
						if ((iValue & 2) != 0)
							fs |= EmuFontStyle.Italic;
						if ((iValue & 4) != 0)
							fs |= EmuFontStyle.Strikeout;
						if ((iValue & 8) != 0)
							fs |= EmuFontStyle.Underline;
						parent.exm.Console.SetStringStyle(fs);
					}
					break;
				case FunctionCode.SETFONT:
					if (func.Argument.IsConst)
						str = func.Argument.ConstStr;
					else
						str = ((ExpressionArgument)func.Argument).Term.GetStrValue(parent.exm);
					parent.exm.Console.SetFont(str);
					break;
				case FunctionCode.ALIGNMENT:
					str = func.Argument.ConstStr;
					if (str.Equals("LEFT", Config.StringComparison))
						parent.exm.Console.Alignment = DisplayLineAlignment.LEFT;
					else if (str.Equals("CENTER", Config.StringComparison))
						parent.exm.Console.Alignment = DisplayLineAlignment.CENTER;
					else if (str.Equals("RIGHT", Config.StringComparison))
						parent.exm.Console.Alignment = DisplayLineAlignment.RIGHT;
					else
						throw new CodeEE(string.Format(trerror.InvalidAlignment.Text, str));
					break;

				case FunctionCode.REDRAW:
					if (func.Argument.IsConst)
						iValue = func.Argument.ConstInt;
					else
						iValue = ((ExpressionArgument)func.Argument).Term.GetIntValue(parent.exm);
					parent.exm.Console.SetRedraw(iValue);
					break;

				case FunctionCode.RESET_STAIN:
					{
						if (func.Argument.IsConst)
							iValue = func.Argument.ConstInt;
						else
							iValue = ((ExpressionArgument)func.Argument).Term.GetIntValue(parent.exm);
						vEvaluator.SetDefaultStain(iValue);
					}
					break;
				case FunctionCode.SPLIT:
					{
						SpSplitArgument spSplitArg = (SpSplitArgument)func.Argument;
						string target = spSplitArg.TargetStr.GetStrValue(parent.exm);
						string[] split = [spSplitArg.Split.GetStrValue(parent.exm)];
						string[] retStr = target.Split(split, StringSplitOptions.None);
						spSplitArg.Num.SetValue(retStr.Length, parent.exm);
						if (retStr.Length > spSplitArg.Var.GetLength(0))
						{
							string[] temp = retStr;
							retStr = new string[spSplitArg.Var.GetLength(0)];
							Array.Copy(temp, retStr, retStr.Length);
						}
						spSplitArg.Var.SetValue(retStr, [0, 0, 0]);
					}
					break;
				case FunctionCode.PRINTCPERLINE:
					{
						SpGetIntArgument spGetintArg = (SpGetIntArgument)func.Argument;
						spGetintArg.VarToken.SetValue(Config.PrintCPerLine, parent.exm);
					}
					break;
				case FunctionCode.SAVENOS:
					{
						SpGetIntArgument spGetintArg = (SpGetIntArgument)func.Argument;
						spGetintArg.VarToken.SetValue(Config.SaveDataNos, parent.exm);
					}
					break;
				case FunctionCode.FORCEKANA:
					if (func.Argument.IsConst)
						iValue = func.Argument.ConstInt;
					else
						iValue = ((ExpressionArgument)func.Argument).Term.GetIntValue(parent.exm);
					parent.exm.ForceKana(iValue);
					break;
				case FunctionCode.SKIPDISP:
					{
						iValue = func.Argument.IsConst ? func.Argument.ConstInt : ((ExpressionArgument)func.Argument).Term.GetIntValue(parent.exm);
						parent.skipPrint = iValue != 0;
						userDefinedSkip = iValue != 0;
						vEvaluator.RESULT = parent.skipPrint ? 1L : 0L;
					}
					break;
				case FunctionCode.NOSKIP:
					{
						if (func.JumpTo == null)
							throw new CodeEE(trerror.MissingEndnoskip.Text);
						saveSkip = parent.skipPrint;
						if (parent.skipPrint)
							parent.skipPrint = false;
					}
					break;
				case FunctionCode.ENDNOSKIP:
					{
						if (func.JumpTo == null)
							throw new CodeEE(string.Format(trerror.MissingNoskip.Text, "ENDNOSKIP"));
						if (saveSkip)
							parent.skipPrint = true;
					}
					break;
				case FunctionCode.ARRAYSHIFT:
					{
						SpArrayShiftArgument arrayArg = (SpArrayShiftArgument)func.Argument;
						if (!arrayArg.VarToken.Identifier.IsArray1D)
							throw new CodeEE(string.Format(trerror.IsUsableOnly1DVar.Text, "ARRAYSHIFT"));
						FixedVariableTerm dest = arrayArg.VarToken.GetFixedVariableTerm(parent.exm);
						int shift = (int)arrayArg.Num1.GetIntValue(parent.exm);
						if (shift == 0)
							break;
						int start = (int)arrayArg.Num3.GetIntValue(parent.exm);
						if (start < 0)
							throw new CodeEE(string.Format(trerror.ArgIsNegative.Text, "ARRAYSHIFT", "4", start.ToString()));
						int num;
						if (arrayArg.Num4 != null)
						{
							num = (int)arrayArg.Num4.GetIntValue(parent.exm);
							if (num < 0)
								throw new CodeEE(string.Format(trerror.ArgIsNegative.Text, "ARRAYSHIFT", "5", num.ToString()));
							if (num == 0)
								break;
						}
						else
							num = -1;
						if (dest.Identifier.IsInteger)
						{
							long def = arrayArg.Num2.GetIntValue(parent.exm);
							VariableEvaluator.ShiftArray(dest, shift, def, start, num);
						}
						else
						{
							string defs = arrayArg.Num2.GetStrValue(parent.exm);
							VariableEvaluator.ShiftArray(dest, shift, defs, start, num);
						}
						break;
					}
				case FunctionCode.ARRAYREMOVE:
					{
						SpArrayControlArgument arrayArg = (SpArrayControlArgument)func.Argument;
						if (!arrayArg.VarToken.Identifier.IsArray1D)
							throw new CodeEE(string.Format(trerror.IsUsableOnly1DVar.Text, "ARRAYREMOVE"));
						FixedVariableTerm p = arrayArg.VarToken.GetFixedVariableTerm(parent.exm);
						int start = (int)arrayArg.Num1.GetIntValue(parent.exm);
						int num = (int)arrayArg.Num2.GetIntValue(parent.exm);
						if (start < 0)
							throw new CodeEE(string.Format(trerror.ArgIsNegative.Text, "ARRAYREMOVE", "2", start.ToString()));
						VariableEvaluator.RemoveArray(p, start, num);
						break;
					}
				case FunctionCode.ARRAYSORT:
					{
						SpArraySortArgument arrayArg = (SpArraySortArgument)func.Argument;
						if (!arrayArg.VarToken.Identifier.IsArray1D)
							throw new CodeEE(string.Format(trerror.IsUsableOnly1DVar.Text, "ARRAYSORT"));
						FixedVariableTerm p = arrayArg.VarToken.GetFixedVariableTerm(parent.exm);
						int start = (int)arrayArg.Num1.GetIntValue(parent.exm);
						if (start < 0)
							throw new CodeEE(string.Format(trerror.ArgIsNegative.Text, "ARRAYSORT", "3", start.ToString()));
						int num = 0;
						if (arrayArg.Num2 != null)
						{
							num = (int)arrayArg.Num2.GetIntValue(parent.exm);
							if (num < 0)
								throw new CodeEE(string.Format(trerror.ArgIsNegative.Text, "ARRAYSORT", "4", start.ToString()));
							if (num == 0)
								break;
						}
						else
							num = -1;
						VariableEvaluator.SortArray(p, arrayArg.Order, start, num);
						break;
					}
				case FunctionCode.ARRAYCOPY:
					{
						SpCopyArrayArgument arrayArg = (SpCopyArrayArgument)func.Argument;
						AExpression varName1 = arrayArg.VarName1;
						AExpression varName2 = arrayArg.VarName2;
						VariableToken[] vars = [null!, null!];
						if (!(varName1 is SingleTerm) || !(varName2 is SingleTerm))
						{
							string[] names = [null!, null!];
							names[0] = varName1.GetStrValue(parent.exm);
							names[1] = varName2.GetStrValue(parent.exm);
							if ((vars[0] = parent.IdentifierDictionary.GetVariableToken(names[0], null!, true)) == null)
								throw new CodeEE(string.Format(trerror.NotVariableName.Text, "ARRAYCOPY", "1", names[0]));
							if (!vars[0].IsArray1D && !vars[0].IsArray2D && !vars[0].IsArray3D)
								throw new CodeEE(string.Format(trerror.ArraycopyArgIsNotArray.Text, "1", names[0]));
							if (vars[0].IsCharacterData)
								throw new CodeEE(string.Format(trerror.ArraycopyArgIsCharaVar.Text, "1", names[0]));
							if ((vars[1] = parent.IdentifierDictionary.GetVariableToken(names[1], null!, true)) == null)
								throw new CodeEE(string.Format(trerror.NotVariableName.Text, "ARRAYCOPY", "2", names[1]));
							if (!vars[1].IsArray1D && !vars[1].IsArray2D && !vars[1].IsArray3D)
								throw new CodeEE(string.Format(trerror.ArraycopyArgIsNotArray.Text, "2", names[1]));
							if (vars[1].IsCharacterData)
								throw new CodeEE(string.Format(trerror.ArraycopyArgIsCharaVar.Text, "2", names[1]));
							if (vars[1].IsConst)
								throw new CodeEE(string.Format(trerror.ArraycopyArgIsConst.Text, "2", names[1]));
							if ((vars[0].IsArray1D && !vars[1].IsArray1D) || (vars[0].IsArray2D && !vars[1].IsArray2D) || (vars[0].IsArray3D && !vars[1].IsArray3D))
								throw new CodeEE(trerror.DifferentArraycopyArgsDim.Text);
							if ((vars[0].IsInteger && vars[1].IsString) || (vars[0].IsString && vars[1].IsInteger))
								throw new CodeEE(trerror.DifferentArraycopyArgsType.Text);
						}
						else
						{
							vars[0] = parent.IdentifierDictionary.GetVariableToken(((SingleStrTerm)varName1).Str, null!, true);
							vars[1] = parent.IdentifierDictionary.GetVariableToken(((SingleStrTerm)varName2).Str, null!, true);
							if ((vars[0].IsInteger && vars[1].IsString) || (vars[0].IsString && vars[1].IsInteger))
								throw new CodeEE(trerror.DifferentArraycopyArgsType.Text);
						}
						VariableEvaluator.CopyArray(vars[0], vars[1]);
					}
					break;
				case FunctionCode.ENCODETOUNI:
					{
						term = ((ExpressionArgument)func.Argument).Term;
						string target = term.GetStrValue(parent.exm);

						int length = vEvaluator.RESULT_ARRAY.Length;
						if (target.Length > length - 1)
							throw new CodeEE(string.Format(trerror.tooLongEncodetouniArg.Text, target.Length, length - 1));

						int[] ary = new int[target.Length];
						for (int i = 0; i < target.Length; i++)
							ary[i] = char.ConvertToUtf32(target, i);
						vEvaluator.SetEncodingResult(ary);
					}
					break;
				case FunctionCode.ASSERT:
					if (((ExpressionArgument)func.Argument).Term.GetIntValue(parent.exm) == 0)
						throw new CodeEE(trerror.AssertArgIs0.Text);
					break;
				case FunctionCode.THROW:
					throw new CodeEE(((ExpressionArgument)func.Argument).Term.GetStrValue(parent.exm));
				case FunctionCode.CLEARTEXTBOX:
					console.ClearText();
					break;
				case FunctionCode.STRDATA:
					{
						if (func.dataList.Count == 0)
						{
							parent.state.JumpTo(func.JumpTo);
							return;
						}
						int count = func.dataList.Count;
						int choice = (int)vEvaluator.GetNextRand(count);
						List<InstructionLine> iList = func.dataList[choice];
						int i = 0;
						foreach (InstructionLine selectedLine in iList)
						{
							parent.state.CurrentLine = selectedLine;
							if (selectedLine.Argument == null)
								ArgumentParser.SetArgumentTo(selectedLine);
							term = ((ExpressionArgument)selectedLine.Argument!).Term;
							str += term.GetStrValue(parent.exm);
							if (++i < iList.Count)
								str += "\n";
						}
						((StrDataArgument)func.Argument).Var.SetValue(str!, parent.exm);
						parent.state.JumpTo(func.JumpTo);
						break;
					}
				case FunctionCode.SKIPLOG:
					{
						iValue = func.Argument.IsConst ? func.Argument.ConstInt : ((ExpressionArgument)func.Argument).Term.GetIntValue(parent.exm);
						console.MesSkip = iValue != 0;
						break;
					}
#if DEBUG
				default:
					throw new ExeEE(trerror.UndefinedFunc.Text);
#endif
			}
			return;
		}


		bool doFlowControlFunction(InstructionLine func)
		{
			switch (func.FunctionCode)
			{
				case FunctionCode.LOADDATA:
					{
						ExpressionArgument intExpArg = (ExpressionArgument)func.Argument;
						long target = intExpArg.Term.GetIntValue(parent.exm);
						if (target < 0)
							throw new CodeEE(string.Format(trerror.LoaddataArgIsNegative.Text, target.ToString()));
						else if (target > int.MaxValue)
							throw new CodeEE(string.Format(trerror.TooLargeLoaddataArg.Text, target.ToString()));
						EraDataResult result = vEvaluator.CheckData((int)target, EraSaveFileType.Normal);
						if (result.State != EraDataState.OK)
							throw new CodeEE(trerror.LoadCorruptedData.Text);

						if (!vEvaluator.LoadFrom((int)target))
							throw new ExeEE(trerror.UnexpectedErrorInLoaddata.Text);
						parent.state.ClearFunctionList();
						parent.state.SystemState = SystemStateCode.LoadData_DataLoaded;
						return false;
					}

				case FunctionCode.TRYCALLLIST:
				case FunctionCode.TRYJUMPLIST:
					{
						string funcName = "";
						CalledFunction callto = null!;
						SpCallArgment cfa = null!;
						foreach (InstructionLine iLine in func.callList)
						{

							cfa = (SpCallArgment)iLine.Argument;
							funcName = cfa.FuncnameTerm.GetStrValue(parent.exm);
							callto = CalledFunction.CallFunction(parent, funcName, func.JumpTo);
							if (callto == null)
								continue;
							callto.IsJump = func.Function.IsJump();
							UserDefinedFunctionArgument args = callto.ConvertArg(cfa.RowArgs, out string errMes);
							if (args == null)
								throw new CodeEE(errMes);
							parent.state.IntoFunction(callto, args, parent.exm);
							return true;
						}
						parent.state.JumpTo(func.JumpTo);
					}
					break;
				case FunctionCode.TRYGOTOLIST:
					{
						string funcName = "";
						LogicalLine jumpto = null!;
						foreach (InstructionLine iLine in func.callList)
						{
							if (iLine.Argument == null)
								ArgumentParser.SetArgumentTo(iLine);
							funcName = ((SpCallArgment)iLine.Argument!).FuncnameTerm.GetStrValue(parent.exm);
							jumpto = parent.state.CurrentCalled.CallLabel(parent, funcName);
							if (jumpto != null)
								break;
						}
						if (jumpto == null)
							parent.state.JumpTo(func.JumpTo);
						else
							parent.state.JumpTo(jumpto);
					}
					break;
				case FunctionCode.CALLTRAIN:
					{
						ExpressionArgument intExpArg = (ExpressionArgument)func.Argument;
						long count = intExpArg.Term.GetIntValue(parent.exm);
						parent.SetCommnds(count);
						return false;
					}
				case FunctionCode.STOPCALLTRAIN:
					{
						if (parent.isCTrain)
						{
							parent.ClearCommands();
							parent.skipPrint = false;
						}
						return false;
					}
				case FunctionCode.DOTRAIN:
					{
						switch (parent.state.SystemState)
						{
							case SystemStateCode.Train_CallEventTrain:
							case SystemStateCode.Train_CallShowStatus:
							case SystemStateCode.Train_CallEventComEnd:
								break;
							default:
								parent.exm.Console.PrintSystemLine(parent.state.SystemState.ToString());
								throw new CodeEE(trerror.CanNotUseDotrainHere.Text);
						}
						parent.coms.Clear();
						parent.isCTrain = false;
						parent.count = 0;

						long train = ((ExpressionArgument)func.Argument).Term.GetIntValue(parent.exm);
						if (train < 0)
							throw new CodeEE(trerror.DotrainArgLessThan0.Text);
						if (train >= parent.TrainName.Length)
							throw new CodeEE(trerror.DotrainArgOverTrainnameArray.Text);
						parent.doTrainSelectCom = train;
						parent.state.SystemState = SystemStateCode.Train_DoTrain;
						return false;
					}
#if DEBUG
				default:
					throw new ExeEE(trerror.UndefinedFunc.Text);
#endif
			}
			return true;
		}

		internal void DeletePrevState()
		{
			if (prevStateList.Count == 0)
				return;
			prevStateList.RemoveAt(prevStateList.Count - 1);
		}

		internal void DeleteAllPrevState()
		{
			foreach (ProcessState state in prevStateList)
				state.ClearFunctionList();
			prevStateList.Clear();
		}

		internal void SaveCurrentState(bool single)
		{
			if (parent.state != null)
			{
				prevStateList.Add(parent.state);
				parent.state = parent.state.Clone();
			}
		}

		internal void LoadPrevState()
		{
			if (parent.state != null)
			{
				parent.state.ClearFunctionList();
				parent.state = prevStateList[prevStateList.Count - 1];
				DeletePrevState();
			}
		}

		internal ProcessState GetCurrentState
		{
			get { return parent.state; }
		}
	}
}
