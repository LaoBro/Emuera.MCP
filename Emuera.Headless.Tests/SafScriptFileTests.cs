using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using MinorShift.Emuera.GameData.Function;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Runtime.Script.Statements.Expression;
using MinorShift.Emuera.Runtime.Script.Statements.Variable;
using MinorShift.Emuera.Runtime.Utils;
using Xunit;

namespace MinorShift.Emuera.Tests;

[Collection("LoaderTests")]
public class SafScriptFileTests
{
	private const string Root =
		"content://com.android.externalstorage.documents/tree/primary%3Aemuera/document/primary%3Aemuera%2FTK";

	private sealed class GamePathsScope : IDisposable
	{
		private readonly GamePaths previous = GamePaths.Current;

		public GamePathsScope(IGameDirAccessor accessor)
		{
			_ = GamePaths.Resolve(Root, accessor);
		}

		public void Dispose() => GamePaths.SetCurrent(previous);
	}

    [Fact]
	public void Relative_script_path_in_sav_tree_resolves_and_creates_each_parent_directory()
	{
		var accessor = new SafCompatContentUriTests.InMemoryContentDirAccessor(Root);
		using var gamePaths = new GamePathsScope(accessor);

        var resolved = SafCompat.TryResolveGameRelativePath("sav/profiles/note.txt", createParentDirectories: true, out var path);

        Assert.True(resolved);
        var savDir = SafCompat.ResolveSubPath(Root, "sav");
        var profilesDir = SafCompat.ResolveSubPath(savDir, "profiles");
        Assert.True(accessor.DirectoryExists(savDir));
        Assert.True(accessor.DirectoryExists(profilesDir));
		Assert.Equal("note.txt", SafPath.GetLogicalFileName(path));
	}

	[Theory]
	[InlineData("root.txt", "root.txt")]
	[InlineData(@"sav\profiles\note.txt", "note.txt")]
	public void Relative_script_path_allows_root_files_and_backslash_separators(string relativePath, string expectedFileName)
	{
		var accessor = new SafCompatContentUriTests.InMemoryContentDirAccessor(Root);
		using var gamePaths = new GamePathsScope(accessor);

		Assert.True(SafCompat.TryResolveGameRelativePath(relativePath, createParentDirectories: true, out var path));
		Assert.Equal(expectedFileName, SafPath.GetLogicalFileName(path));
	}

    [Theory]
    [InlineData("")]
    [InlineData("../escape.txt")]
    [InlineData("sav/../escape.txt")]
    [InlineData("sav//empty.txt")]
    [InlineData("erb/script.txt")]
    [InlineData("C:\\outside.txt")]
    [InlineData("/outside.txt")]
	public void Relative_script_path_rejects_unsafe_or_unsupported_locations(string input)
	{
		var accessor = new SafCompatContentUriTests.InMemoryContentDirAccessor(Root);
		using var gamePaths = new GamePathsScope(accessor);

        Assert.False(SafCompat.TryResolveGameRelativePath(input, createParentDirectories: false, out _));
    }

    [Fact]
	public void GetDatFiles_returns_logical_suffixes_for_content_uris()
	{
		var accessor = new SafCompatContentUriTests.InMemoryContentDirAccessor(Root);
		using var gamePaths = new GamePathsScope(accessor);
        var datDir = SafCompat.ResolveSubPath(Root, "dat");
        SafCompat.CreateDirectory(datDir);
        accessor.Seed(SafCompat.CombinePath(datDir, "var_01.dat"), [0x01]);
        accessor.Seed(SafCompat.CombinePath(datDir, "var_custom.dat"), [0x02]);
        accessor.Seed(SafCompat.CombinePath(datDir, "chara_alice.dat"), [0x03]);

        Assert.Equal(["01", "custom"], VariableEvaluator.GetDatFiles(charadat: false, "*").OrderBy(value => value));
        Assert.Equal(["alice"], VariableEvaluator.GetDatFiles(charadat: true, "*").OrderBy(value => value));
    }

    [Fact]
	public void SaveText_and_LoadText_support_content_uri_string_and_number_paths()
	{
		var accessor = new SafCompatContentUriTests.InMemoryContentDirAccessor(Root);
		using var gamePaths = new GamePathsScope(accessor);
        using var scope = GlobalStatic.OpenScope(new ConfigData());
        var saveText = FunctionMethodCreator.GetMethodList()["SAVETEXT"];
        var loadText = FunctionMethodCreator.GetMethodList()["LOADTEXT"];
        var originalSaveEncoding = Config.SaveEncode;
        Config.SaveEncode = EncodingHandler.shiftjisEncoding;
        try
        {
            Assert.Equal(1, saveText.GetIntValue(null!,
            [
                new SingleStrTerm("UTF-8 text"), new SingleStrTerm("sav/profiles/note"),
                new SingleLongTerm(0), new SingleLongTerm(1),
            ]));

            Assert.True(SafCompat.TryResolveGameRelativePath("sav/profiles/note.txt", false, out var namedPath));
            Assert.True(accessor.FileExists(namedPath));
			Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, accessor.ReadAllBytes(namedPath)![..3]);
			Assert.Equal("UTF-8 text", loadText.GetStrValue(null!, [new SingleStrTerm("sav/profiles/note.txt")]));
			Assert.True(SafCompat.TryResolveGameRelativePath("sav/no-bom.txt", false, out var noBomPath));
			accessor.Seed(noBomPath, EncodingHandler.UTF8Encoding.GetBytes("no BOM UTF-8: 日本語"));
			Assert.Equal("no BOM UTF-8: 日本語", loadText.GetStrValue(null!, [new SingleStrTerm("sav/no-bom.txt")]));

			Assert.Equal(1, saveText.GetIntValue(null!, [new SingleStrTerm("番号"), new SingleLongTerm(7)]));
            var numberedPath = SafCompat.CombinePath(Root, "txt07.txt");
            Assert.True(accessor.FileExists(numberedPath));
            Assert.Equal(EncodingHandler.shiftjisEncoding.GetBytes("番号"), accessor.ReadAllBytes(numberedPath));
            Assert.Equal("番号", loadText.GetStrValue(null!, [new SingleLongTerm(7)]));
        }
        finally
        {
            Config.SaveEncode = originalSaveEncoding;
        }
    }
}
