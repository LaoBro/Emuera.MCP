using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace MinorShift.Emuera.Tests;

/// <summary>
/// 防止 Shared Runtime 新增未经审查的本地 I/O。
/// 已知遗留点在基线中按文件和数量登记；删除无需改基线，新增必须先作出明确的 SAF 设计决定。
/// </summary>
public class SafIoPolicyTests
{
    private static readonly Regex DirectIo = new(
        @"\b(?:File|Directory)\.[A-Za-z_][A-Za-z0-9_]*\b|\bnew\s+(?:(?:System\.IO\.)?FileStream|(?:System\.IO\.)?FileInfo|(?:System\.IO\.)?DirectoryInfo)\b",
        RegexOptions.Compiled);

    private static readonly IReadOnlyDictionary<string, string> KnownDirectIoFiles =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Config/ConfigData.cs"] = "阶段 2/3：配置写入与更新 key",
            ["Config/JSON/JSONConfig.cs"] = "阶段 2：迁移到 App 私有设置目录",
            ["Script/Data/ConstantData.cs"] = "仅 accessor 缺失时的本地回退",
            ["Script/Statements/Instraction.Child.cs"] = "阶段 3：声音文件访问",
            ["Utils/EncodingHandler.cs"] = "阶段 1：编码探测",
            ["Utils/SafCompat.cs"] = "本地路径分支的合法实现",
            ["Utils/Sys.cs"] = "进程工作目录，不解析游戏文件",
            ["Utils/EvilMask/Lang.cs"] = "阶段 3：自定义语言文件",
            ["Utils/EvilMask/Utils.cs"] = "非 HEADLESS 图像加载",
            ["Utils/PluginSystem/PluginManager.cs"] = "阶段 3：Android 插件策略",
        };

    // 行号基线是有意严格的：修改含裸 I/O 的代码时，必须重新确认其 SAF 归属。
    private static readonly IReadOnlyDictionary<string, int> KnownDirectIoCallSites =
        new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["Config/ConfigData.cs:879"] = 1,
            ["Config/ConfigData.cs:922"] = 1,
            ["Config/ConfigData.cs:923"] = 1,
            ["Config/ConfigData.cs:927"] = 1,
            ["Config/ConfigData.cs:930"] = 1,
            ["Config/ConfigData.cs:947"] = 1,
            ["Config/ConfigData.cs:954"] = 1,
            ["Config/ConfigData.cs:955"] = 1,
            ["Config/ConfigData.cs:961"] = 1,
            ["Config/ConfigData.cs:968"] = 1,
            ["Config/ConfigData.cs:969"] = 1,
            ["Config/ConfigData.cs:970"] = 1,
            ["Config/ConfigData.cs:972"] = 1,
            ["Config/ConfigData.cs:1150"] = 1,
            ["Config/JSON/JSONConfig.cs:17"] = 1,
            ["Config/JSON/JSONConfig.cs:19"] = 1,
            ["Config/JSON/JSONConfig.cs:23"] = 1,
            ["Config/JSON/JSONConfig.cs:27"] = 1,
            ["Config/JSON/JSONConfig.cs:31"] = 1,
            ["Config/JSON/JSONConfig.cs:33"] = 1,
            ["Config/JSON/JSONConfig.cs:45"] = 1,
            ["Script/Data/ConstantData.cs:221"] = 1,
            ["Script/Data/ConstantData.cs:1699"] = 1,
            ["Script/Statements/Instraction.Child.cs:2715"] = 1,
            ["Script/Statements/Instraction.Child.cs:2779"] = 1,
            ["Utils/EncodingHandler.cs:22"] = 1,
            ["Utils/EncodingHandler.cs:84"] = 1,
            ["Utils/SafCompat.cs:28"] = 1,
            ["Utils/SafCompat.cs:34"] = 1,
            ["Utils/SafCompat.cs:40"] = 1,
            ["Utils/SafCompat.cs:46"] = 1,
            ["Utils/SafCompat.cs:65"] = 1,
            ["Utils/SafCompat.cs:78"] = 1,
            ["Utils/SafCompat.cs:79"] = 1,
            ["Utils/SafCompat.cs:88"] = 2,
            ["Utils/SafCompat.cs:94"] = 1,
            ["Utils/SafCompat.cs:101"] = 1,
            ["Utils/SafCompat.cs:107"] = 1,
            ["Utils/SafCompat.cs:113"] = 1,
            ["Utils/SafCompat.cs:120"] = 1,
            ["Utils/SafCompat.cs:128"] = 1,
            ["Utils/SafCompat.cs:129"] = 1,
            ["Utils/Sys.cs:16"] = 1,
            ["Utils/EvilMask/Lang.cs:1352"] = 1,
            ["Utils/EvilMask/Lang.cs:1354"] = 1,
            ["Utils/EvilMask/Lang.cs:1466"] = 1,
            ["Utils/EvilMask/Lang.cs:1467"] = 1,
            ["Utils/EvilMask/Utils.cs:243"] = 1,
            ["Utils/PluginSystem/PluginManager.cs:271"] = 1,
            ["Utils/PluginSystem/PluginManager.cs:277"] = 1,
            ["Utils/PluginSystem/PluginManager.cs:278"] = 1,
        };

    [Fact]
    public void Shared_runtime_direct_IO_does_not_exceed_documented_SAF_baseline()
    {
        var runtimeDir = Path.Combine(FindRepositoryRoot(), "Emuera.Headless.Core", "Shared", "Runtime");
        var violations = new List<string>();
        foreach (var path in Directory.EnumerateFiles(runtimeDir, "*.cs", SearchOption.AllDirectories))
        {
            var lines = File.ReadAllLines(path);
            var relativePath = Path.GetRelativePath(runtimeDir, path).Replace('\\', '/');
            for (var index = 0; index < lines.Length; index++)
            {
                var line = lines[index].TrimStart();
                if (line.StartsWith("//", StringComparison.Ordinal)
                    || line.StartsWith("/*", StringComparison.Ordinal)
                    || line.StartsWith("*", StringComparison.Ordinal))
                    continue;
                var directIoCalls = DirectIo.Matches(line).Count;
                if (directIoCalls == 0)
                    continue;
                if (!KnownDirectIoFiles.TryGetValue(relativePath, out var reason))
                {
                    violations.Add($"{relativePath}:{index + 1}: {directIoCalls} new direct I/O call(s); no SAF classification exists.");
                    continue;
                }

                var callSite = $"{relativePath}:{index + 1}";
                if (!KnownDirectIoCallSites.TryGetValue(callSite, out var allowedCalls)
                    || directIoCalls > allowedCalls)
                {
                    violations.Add($"{callSite}: {directIoCalls} call(s) is outside the explicit baseline ({reason}).");
                }
            }
        }

        Assert.True(violations.Count == 0,
            "Direct I/O in Shared/Runtime exceeds the documented SAF baseline:\n" + string.Join('\n', violations));
    }

    private static string FindRepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Emuera.sln")))
                return dir.FullName;
        }
        throw new DirectoryNotFoundException("Could not locate repository root from test output directory.");
    }
}
