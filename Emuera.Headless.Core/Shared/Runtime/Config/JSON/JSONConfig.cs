//新設したコンフィグ設定のロード、セーブ、公開を担当する。
using System.IO;
using System.Text.Json;


namespace MinorShift.Emuera.Runtime.Config.JSON;
static class JSONConfig
{
	// Some runtime types read this during static initialization before the startup loader runs.
	public static JSONConfigData Data = new();

	const string _configFileName = "setting.json";
	static string ConfigFilePath => AppDataPaths.CombinePath(_configFileName);

	public static void Load()
	{
		var configFilePath = ConfigFilePath;
		var dir = Path.GetDirectoryName(configFilePath);
		if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
		{
			try { Directory.CreateDirectory(dir); }
			catch { /* ignore directory creation failure in headless mode */ }
		}

		if (!File.Exists(configFilePath))
		{
			var defaultData = new JSONConfigData();
			var defaultJson = JsonSerializer.Serialize(defaultData);
			try { File.WriteAllText(configFilePath, defaultJson); }
			catch { /* ignore write failure in headless mode */ }
		}

		if (File.Exists(configFilePath))
		{
			var json = File.ReadAllText(configFilePath);
			Data = JsonSerializer.Deserialize<JSONConfigData>(json) ?? new JSONConfigData();
		}
		else
		{
			Data = new JSONConfigData();
		}
	}

	public static void Save()
	{
		var json = JsonSerializer.Serialize(Data);
		Directory.CreateDirectory(AppDataPaths.Directory);
		File.WriteAllText(ConfigFilePath, json);
	}
}
