using MinorShift.Emuera.GameView;
using MinorShift.Emuera.Sub;
using MinorShift.Emuera.Runtime.Utils;
using System;
using System.IO;
using trmk = MinorShift.Emuera.Runtime.Utils.EvilMask.Lang.KeyMacro;
using trmb = MinorShift.Emuera.Runtime.Utils.EvilMask.Lang.MessageBox;

namespace MinorShift.Emuera.Runtime.Script;

internal static class KeyMacro
{
	public const string gID = "グループ";
	public const int MaxGroup = 10;
	public const int MaxFkey = 12;
	public const int MaxMacro = MaxFkey * MaxGroup;
	/// <summary>
	/// マクロの内容
	/// </summary>
	static string[] macro = new string[MaxMacro];
	/// <summary>
	/// マクロキー
	/// </summary>
	static string[] macroName = new string[MaxMacro];
	static string[] groupName = new string[MaxGroup];
	static bool isMacroChanged;
	static KeyMacro()
	{
		ResetNames();
	}

	public static void ResetNames()
	{
		for (int g = 0; g < MaxGroup; g++)
		{
			groupName[g] = string.Format(trmk.SetMacroGroup.Text, g.ToString());
			for (int f = 0; f < MaxFkey; f++)
			{
				int i = f + g * MaxFkey;
				macro[i] = "";
				if (g == 0)
					macroName[i] = string.Format(trmk.MacroKeyF.Text, (f + 1).ToString());
				else
					macroName[i] = string.Format(trmk.GMacroKeyF.Text, g.ToString(), (f + 1).ToString());

			}
		}
	}

	public static bool SaveMacro()
	{
		if (!isMacroChanged)
			return true;
		try
		{
			string macroPath = SafCompat.CombinePath(Program.ExeDir, "macro.txt");
			using var writer = new StreamWriter(SafCompat.OpenWrite(macroPath), Config.Config.Encode);
			for (int g = 0; g < MaxGroup; g++)
			{
				writer.WriteLine(gID + g.ToString() + ":" + groupName[g]);
			}
			for (int i = 0; i < MaxMacro; i++)
			{
				writer.WriteLine(macroName[i] + macro[i]);
			}
		}
		catch (Exception ex)
		{
			EmueraLog.Warn("KeyMacro", $"SaveMacro failed: {ex}");
			Dialog.Show(trmb.ConfigError.Text, trmb.MacroSaveFailure.Text);
			return false;
		}
		return true;
	}

	public static void LoadMacroFile(string filename)
	{
		using var eReader = new EraStreamReader(false);
		if (!eReader.Open(filename))
			return;
		try
	{
		string line = null!;
			while ((line = eReader.ReadLine()) != null)
			{
				if (line.Length == 0 || line[0] == ';')
					continue;
				if (line.StartsWith(gID))
				{
					if (line.Length < gID.Length + 4)
						continue;
					int num = line[gID.Length] - '0';
					if (num < 0 || num > 9)
						continue;
					if (line[gID.Length + 1] != ':')
						continue;
					groupName[num] = line[(gID.Length + 2)..];
				}
				for (int i = 0; i < MaxMacro; i++)
				{
					if (line.StartsWith(macroName[i]))
					{
						macro[i] = line[macroName[i].Length..];
						break;
					}
				}
			}
		}
		catch { return; }
		finally { eReader.Dispose(); }
	}

	public static void SetMacro(int FkeyNum, int groupNum, string macroStr)
	{
		isMacroChanged = true;
		macro[FkeyNum + groupNum * MaxFkey] = macroStr;
	}

	public static string GetMacro(int FkeyNum, int groupNum)
	{
		return macro[FkeyNum + groupNum * MaxFkey];
	}

	public static string GetGroupName(int groupNum)
	{
		return groupName[groupNum];
	}
}
