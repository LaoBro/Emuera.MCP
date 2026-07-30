using System;
using System.IO;
using System.Linq;
using System.Reflection;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Runtime.Config.JSON;
using MinorShift.Emuera.Runtime.Script;
using MinorShift.Emuera.Runtime.Utils;
using MinorShift.Emuera.GameView;
using MinorShift.Emuera.Terminal.Platform;
using MinorShift.Emuera.UI.Game;
using Xunit;

namespace MinorShift.Emuera.Tests;

[Collection("LoaderTests")]
public class SafStage2StorageTests
{
	private const string Root =
		"content://com.android.externalstorage.documents/tree/primary%3Aemuera/document/primary%3Aemuera%2FTK";
	private const string OtherRoot =
		"content://com.android.externalstorage.documents/tree/primary%3Aemuera/document/primary%3Aemuera%2FOther";

    [Fact]
	public void Config_and_macro_saves_write_to_the_SAF_game_root()
    {
        var accessor = new SafCompatContentUriTests.InMemoryContentDirAccessor(Root);
        using var gamePaths = new GamePathsScope(Root, accessor);
        using var globals = GlobalStatic.OpenScope(new ConfigData());
        using var macro = new KeyMacroScope();

		Assert.True(ConfigData.Current!.SaveConfig());
		Assert.True(accessor.FileExists(SafCompat.CombinePath(Root, "emuera.config")));
		using var console = new EmueraConsole(new HeadlessConsole(), new PosixTerminalSetup());
		Assert.True(console.OutputLog("logs/output.log", hideInfo: true));
		Assert.True(accessor.FileExists(SafCompat.TryResolvePathUnderGameRoot("logs/output.log", false, out var outputLogPath)
			? outputLogPath
			: throw new InvalidOperationException("Expected log path to resolve.")));

		KeyMacro.SetMacro(0, 0, "PRINTL saved through SAF");
        Assert.True(KeyMacro.SaveMacro());
		Assert.True(accessor.FileExists(SafCompat.CombinePath(Root, "macro.txt")));
	}

	[Fact]
	public void Loading_a_new_SAF_game_creates_its_missing_config_in_that_game_root()
	{
		var accessor = new SafCompatContentUriTests.InMemoryContentDirAccessor(Root);
		using var gamePaths = new GamePathsScope(Root, accessor);
		var config = new ConfigData();
		using var globals = GlobalStatic.OpenScope(config);

		_ = GamePaths.Resolve(OtherRoot, accessor);
		Assert.True(config.LoadConfig(OtherRoot));

		Assert.True(accessor.FileExists(SafCompat.CombinePath(OtherRoot, "emuera.config")));
		Assert.False(accessor.FileExists(SafCompat.CombinePath(Root, "emuera.config")));
	}

    [Fact]
    public void Log_path_resolves_nested_relative_names_under_the_SAF_game_root()
    {
        var accessor = new SafCompatContentUriTests.InMemoryContentDirAccessor(Root);
		using var gamePaths = new GamePathsScope(Root, accessor);

        Assert.True(SafCompat.TryResolvePathUnderGameRoot("logs/init.log", createParentDirectories: true, out var path));
        Assert.Equal("init.log", SafPath.GetLogicalFileName(path));
        Assert.True(accessor.DirectoryExists(SafCompat.ResolveSubPath(Root, "logs")));
        Assert.False(SafCompat.TryResolvePathUnderGameRoot("../init.log", createParentDirectories: true, out _));
    }

    [Fact]
    public void Json_settings_are_persisted_in_app_private_storage()
    {
        using var appData = new TempDir();
        using var scope = new AppDataScope(appData.Root);
        var previous = JSONConfig.Data;
        try
        {
            JSONConfig.Data = new JSONConfigData();
            JSONConfig.Save();

            Assert.True(File.Exists(Path.Combine(appData.Root, "setting.json")));
            JSONConfig.Load();
            Assert.NotNull(JSONConfig.Data);
        }
        finally
        {
            JSONConfig.Data = previous;
        }
    }

    private sealed class GamePathsScope : IDisposable
    {
        private readonly GamePaths previous = GamePaths.Current;

		public GamePathsScope(string root, IGameDirAccessor accessor)
		{
			_ = GamePaths.Resolve(root, accessor);
        }

        public void Dispose() => GamePaths.SetCurrent(previous);
    }

    private sealed class AppDataScope : IDisposable
    {
        private readonly string previous = AppDataPaths.Directory;

        public AppDataScope(string path) => AppDataPaths.Configure(path);

        public void Dispose() => AppDataPaths.Configure(previous);
    }

    private sealed class KeyMacroScope : IDisposable
    {
        private static readonly FieldInfo Macro = GetField("macro");
        private static readonly FieldInfo MacroName = GetField("macroName");
        private static readonly FieldInfo GroupName = GetField("groupName");
        private static readonly FieldInfo IsChanged = GetField("isMacroChanged");

        private readonly string[] macro = ((string[])Macro.GetValue(null)!).ToArray();
        private readonly string[] macroName = ((string[])MacroName.GetValue(null)!).ToArray();
        private readonly string[] groupName = ((string[])GroupName.GetValue(null)!).ToArray();
        private readonly bool isChanged = (bool)IsChanged.GetValue(null)!;

        public void Dispose()
        {
            Macro.SetValue(null, macro);
            MacroName.SetValue(null, macroName);
            GroupName.SetValue(null, groupName);
            IsChanged.SetValue(null, isChanged);
        }

        private static FieldInfo GetField(string name) => typeof(KeyMacro).GetField(
            name, BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(typeof(KeyMacro).FullName, name);
    }

    private sealed class TempDir : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "emuera-stage2-" + Guid.NewGuid().ToString("N"));

        public TempDir() => Directory.CreateDirectory(Root);

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Root))
                    Directory.Delete(Root, recursive: true);
            }
            catch { }
        }
    }
}
