//新設したコンフィグ設定のロード、セーブ、公開を担当する。
using System.IO;
using System.Text.Json;


namespace MinorShift.Emuera.Runtime.Config.JSON;
static class JSONConfig
{
	public static JSONConfigData Data;

	const string _configFileName = "setting.json";
	static string _configFilePath = Program.ExeDir + _configFileName;

	public static void Load()
	{
		var dir = Path.GetDirectoryName(_configFilePath);
		if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
		{
			try { Directory.CreateDirectory(dir); }
			catch { /* ignore directory creation failure in headless mode */ }
		}

		if (!File.Exists(_configFilePath))
		{
			var defaultData = new JSONConfigData();
			var defaultJson = JsonSerializer.Serialize(defaultData);
			try { File.WriteAllText(_configFilePath, defaultJson); }
			catch { /* ignore write failure in headless mode */ }
		}

		if (File.Exists(_configFilePath))
		{
			var json = File.ReadAllText(_configFilePath);
			Data = JsonSerializer.Deserialize<JSONConfigData>(json)!;
		}
		else
		{
			Data = new JSONConfigData();
		}
	}

	public static void Save()
	{
		var json = JsonSerializer.Serialize(Data);
		File.WriteAllText(_configFilePath, json);
	}
}
