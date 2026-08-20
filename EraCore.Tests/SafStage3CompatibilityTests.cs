using System;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Runtime.Utils;
using MinorShift.Emuera.Runtime.Utils.EvilMask;
using MinorShift.Emuera.Runtime.Utils.PluginSystem;
using Xunit;

namespace MinorShift.Emuera.Tests;

[Collection("LoaderTests")]
public sealed class SafStage3CompatibilityTests
{
	private const string Root =
		"content://com.android.externalstorage.documents/tree/primary%3Aemuera/document/primary%3Aemuera%2FStage3";

	[Fact]
	public void Saf_update_key_is_stable_and_tracks_logical_file_set()
	{
		var accessor = new SafCompatContentUriTests.InMemoryContentDirAccessor(Root);
		using var scope = new GamePathsScope(Root, accessor);
		var erbDir = SafCompat.ResolveSubPath(Root, "erb");
		var csvDir = SafCompat.ResolveSubPath(Root, "csv");
		SafCompat.CreateDirectory(erbDir);
		SafCompat.CreateDirectory(csvDir);
		accessor.Seed(SafCompat.CombinePath(erbDir, "MAIN.ERB"), [1]);
		accessor.Seed(SafCompat.CombinePath(csvDir, "GAMEBASE.CSV"), [2]);

		var method = typeof(ConfigData).GetMethod(
			"getSafUpdateKey", BindingFlags.Static | BindingFlags.NonPublic)
			?? throw new MissingMethodException(typeof(ConfigData).FullName, "getSafUpdateKey");
		var first = (long)method.Invoke(null, [SearchOption.AllDirectories])!;
		var second = (long)method.Invoke(null, [SearchOption.AllDirectories])!;

		Assert.Equal(first, second);
		accessor.Seed(SafCompat.CombinePath(erbDir, "NEW.ERB"), [3]);
		var changed = (long)method.Invoke(null, [SearchOption.AllDirectories])!;
		Assert.NotEqual(first, changed);
	}

	[Fact]
	public void Saf_update_key_failure_does_not_block_startup()
	{
		var accessor = new SafCompatContentUriTests.InMemoryContentDirAccessor(Root);
		using var scope = new GamePathsScope(Root, accessor);
		GamePaths.Current.DirAccessor = null!;

		var method = typeof(ConfigData).GetMethod(
			"getSafUpdateKey", BindingFlags.Static | BindingFlags.NonPublic)
			?? throw new MissingMethodException(typeof(ConfigData).FullName, "getSafUpdateKey");
		var key = (long)method.Invoke(null, [SearchOption.TopDirectoryOnly])!;

		Assert.Equal(0, key);
	}

	[Fact]
	public void Saf_once_update_sets_the_runtime_reduction_flag()
	{
		var accessor = new SafCompatContentUriTests.InMemoryContentDirAccessor(Root);
		using var scope = new GamePathsScope(Root, accessor);
		using var globals = GlobalStatic.OpenScope(new ConfigData());
		var config = ConfigData.Current!;
		SafCompat.CreateDirectory(SafCompat.ResolveSubPath(Root, "erb"));
		SafCompat.CreateDirectory(SafCompat.ResolveSubPath(Root, "csv"));
		accessor.Seed(SafCompat.CombinePath(SafCompat.ResolveSubPath(Root, "erb"), "MAIN.ERB"), [1]);

		config.GetConfigItem(ConfigCode.ReduceArgumentOnLoad).SetValue(ReduceArgumentOnLoadFlag.ONCE);
		Assert.True(config.CheckUpdate());
		Assert.True(config.NeedReduceArgumentOnLoad);
	}

	[Fact]
	public void Saf_language_generation_writes_through_the_game_accessor()
	{
		var accessor = new SafCompatContentUriTests.InMemoryContentDirAccessor(Root);
		using var scope = new GamePathsScope(Root, accessor);

		Lang.GenerateDefaultLangFile();

		var langDir = SafCompat.ResolveSubPath(Root, "lang");
		var generated = SafCompat.CombinePath(langDir, "emuera-default-lang.xml");
		Assert.True(accessor.FileExists(generated));
		Assert.Contains("<name>日本語</name>",
			System.Text.Encoding.UTF8.GetString(accessor.ReadAllBytes(generated)!));
	}

	[Fact]
	public async Task Saf_preload_caches_erd_and_als_files()
	{
		var accessor = new SafCompatContentUriTests.InMemoryContentDirAccessor(Root);
		using var scope = new GamePathsScope(Root, accessor);
		var erbDir = SafCompat.ResolveSubPath(Root, "erb");
		SafCompat.CreateDirectory(erbDir);
		var erd = SafCompat.CombinePath(erbDir, "DVAR.erd");
		var als = SafCompat.CombinePath(erbDir, "DVAR.als");
		accessor.Seed(erd, Encoding.UTF8.GetBytes("DVAR\n"));
		accessor.Seed(als, Encoding.UTF8.GetBytes("1,alias\n"));

		Preload.Clear();
		try
		{
			await Preload.Load(erbDir, accessor);
			Assert.Contains(erd, Preload.GetAllCachedKeys());
			Assert.Contains(als, Preload.GetAllCachedKeys());
		}
		finally
		{
			Preload.Clear();
		}
	}

	[Fact]
	public void Saf_plugins_are_rejected_with_an_explicit_message()
	{
		var accessor = new SafCompatContentUriTests.InMemoryContentDirAccessor(Root);
		using var scope = new GamePathsScope(Root, accessor);
		var pluginDir = SafCompat.ResolveSubPath(Root, "Plugins");
		SafCompat.CreateDirectory(pluginDir);
		accessor.Seed(SafCompat.CombinePath(pluginDir, "sample.dll"), [0x4D, 0x5A]);

		var error = Assert.Throws<ExeEE>(() => PluginManager.GetInstance().LoadPlugins());
		Assert.Contains("does not support loading game DLL plugins", error.Message, StringComparison.Ordinal);
	}

	private sealed class GamePathsScope : IDisposable
	{
		private readonly GamePaths previous = GamePaths.Current;

		public GamePathsScope(string root, IGameDirAccessor accessor)
			=> _ = GamePaths.Resolve(root, accessor);

		public void Dispose() => GamePaths.SetCurrent(previous);
	}
}
