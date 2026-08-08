# NativeAOT 发布 失败教训

> **TL;DR**：AOT 下 JSON 反射是硬禁用（匿名类型必崩）、库项目 minimal API 必须开请求委托生成器、匿名类型不可序列化、ILC 增量产物可能不更新（改代码后清 bin-aot/obj-aot 全量重建）、输出目录重定向必须补 `DefaultItemExcludes`。

## 教训清单

| # | 标题 | 一句话 | 日期 |
|---|---|---|---|
| 1 | 无 VS C++ workload 也能跑 NativeAOT | `IlcUseEnvironmentalTools` + 手动 CppLinker + LIB 环境变量 | 2026-08-07 |
| 2 | NativeAOT 三座大山 | JSON 反射禁用 / RequestDelegateGenerator / 匿名类型序列化 | 2026-08-07 |
| 3 | ILC 增量发布产物不更新 | 清 bin-aot/obj-aot 全量重建；grep 二进制用 UTF-16LE | 2026-08-07 |
| 4 | 消 ILC 警告不能只换序列化入口 | 显式 context 丢 converter 多态；用 JsonTypeInfo 重载 | 2026-08-07 |
| 5 | 壳层反射 JSON 是 NativeAOT 白屏根因 | 进程存活的白屏 = 托管异常被吞；IL3050 运行时是 JsonSerializerIsReflectionDisabled | 2026-08-08 |
| 6 | 目录隔离的 CS0579 根治 | 重定向 Base*OutputPath 后必须补 DefaultItemExcludes | 2026-08-08 |

---

## 1. 无 VS C++ workload 也能跑 win-x64 NativeAOT——IlcUseEnvironmentalTools + 手动 CppLinker

**场景**：3.2 引擎 NativeAOT 验证（`nativeaot-verify` 分支）。本机无 VS C++ 桌面 workload：VS 18 只装了 MSVC bin（lib 只有 onecore 子集），Windows Kits 无 Lib，vswhere 也认不出 VS 18 的 VC.Tools 组件（findvcvarsall 报 "Platform linker not found"）。

**解决**（全部写死，避免重查）：
1. 装 Windows SDK 库：`winget install Microsoft.WindowsSDK.10.0.26100`（提供 um/x64 + ucrt/x64 的 kernel32.lib/ucrt.lib）。
2. `-p:IlcUseEnvironmentalTools=true` 跳过 findvcvarsall（_VCVarsAllFound 为空 → 不再触发 linker-not-found error）。
3. `-p:CppLinker="...\MSVC\14.51.36231\bin\Hostx64\x64\link.exe"` + `-p:CppLibCreator=...\lib.exe`（Windows 路径，Git Bash 的 /c/... 风格 link.exe 不认）。
4. `LIB` 环境变量 = MSVC `lib/onecore/x64` + Windows SDK `um/x64` + `ucrt/x64`。

**教训**：
- **lld-link 不是 link.exe 的 drop-in**：不认 `/NOEXP`、`/SOURCELINK` 语义不同，`could not open '/NOEXP'` 直接失败——Windows NativeAOT 链接直接用 MSVC `link.exe`，别在 lld 上浪费时间。
- 微软文档写的"需要 Desktop Development for C++ workload"是充分条件不是必要条件；`onecore/x64` 的 CRT 库（libcmt/libcpmt/oldnames）+ link.exe 就够。
- 排查顺序：先确认 `vswhere -requires VC.Tools.x86.x64` 是否命中——组件注册缺失时，任何"装 VS"的自动化都不如手动 CppLinker 快。

## 2. NativeAOT 三座大山：JSON 反射禁用、minimal API 委托生成器、匿名类型序列化

**场景**：AOT 发布成功（ILC 链接通过）但 server 模式启动即崩/全端点 500。三个问题层层叠叠，每个都是"编译通过、运行炸"。

**教训一（System.Text.Json 反射是硬禁用）**：AOT 下 `JsonSerializer.Deserialize<JSONConfigData>(json)`（无 options/context）直接抛 `JsonSerializerIsReflectionDisabled`——不是慢，是**崩**。所有 JSON 路径必须源生成 context（`[JsonSerializable]` + `JsonSerializerContext`）；`TurnJsonOptions`/`JsonOpts` 这类 options 加 `TypeInfoResolver = context` 即可让既有 `Serialize(value, options)` 调用零改动走源生成（converter 递归 `Serialize((object)value, options)` 经 resolver 按运行时类型解析——前提是 context 显式注册了全部多态叶子类型）。

**教训二（库项目的 Map* 必须开请求委托生成器）**：minimal API 的 `MapGet/MapPost` 在 `PublishAot=true` 时 SDK 只对 **web 项目**自动启用静态委托生成；**库项目（如 Emuera.Headless.Server）必须显式 `<EnableRequestDelegateGenerator>true</EnableRequestDelegateGenerator>`**，否则 AOT 下 RequestDelegateFactory 走反射路径，**所有端点 500（连 404 都 500 是强烈信号）**。顺带加 `<IsAotCompatible>true</IsAotCompatible>` 激活分析器。

**教训三（匿名类型在 AOT 下不可序列化）**：`Results.Json(new { ... })` 响应全部要替换——统一改 `JsonObject`（JsonNode 内建 converter）+ `ConfigureHttpJsonOptions(TypeInfoResolverChain.Insert(0, context))` 让 Results.Json 的默认 options 能解析 JsonObject。**别用 `Results.Json(value, jsonTypeInfo)` 显式注入 TypeInfo**：context 的源生成 options 无 encoder 属性（.NET 10），TypeInfo 走默认 encoder 会把 `'` 转义成 `\u0027` 破坏 wire；走 HttpJsonOptions（默认 UnsafeRelaxedJsonEscaping）才与托管时代逐字节一致。wire 回归靠 xUnit 端点断言抓（`Assert.Contains("Missing 'value' field", body)` 直接命中 \u0027）。

**教训四（AOT 下 Kestrel 异常无处可查）**：500 无 body 无日志时，先在管道最前加全局 try-catch 中间件 `Console.Error.WriteLine(ex)`——本次正是靠它抓到 `JsonTypeInfo metadata for type 'JsonObject' was not provided` 才定位到 resolver 问题。排查完移除。

**教训五（托管/AOT 对照定位法）**：同一请求托管 200 / AOT 500 = AOT 特有；全端点共同失败（含 404）→ 管道/路由层；单端点失败 → handler 内。逐层缩小比瞎猜快得多。

## 3. ILC 增量发布产物不更新——改代码后必须清 bin-aot/obj-aot 全量重建

**场景**：修完代码重跑 `dotnet publish`（同 -o 目录）成功，但复现时行为没变；`grep -c "新字符串" exe` 返回 0，以为代码没进去。

**真相**：两个陷阱叠加——
1. **ILC 增量缓存**：obj-aot 下的 ILC 中间产物（native/*.obj 等）在输入 dll 时间戳判断下可能复用旧编译，exe 不含新代码。`dotnet clean` 清不掉自定义 BaseOutputPath（bin-aot/obj-aot），要手动删目录强制全量。
2. **grep 二进制搜不到 .NET 字符串**：程序集字符串字面量是 **UTF-16LE** 存储，`grep "UNEXPECTED"`（ASCII）永远 0——用 `python` 搜 `'UNEXPECTED'.encode('utf-16-le')` 才准。**先确认代码真在产物里，再开始排查运行时行为**。

**教训**：AOT 验证期改代码 → publish → 复现，若现象不变，先查"产物是否真的新"（二进制搜新字符串 UTF-16 + 时间戳 + 清目录全量重建），别在旧产物上反复烧时间。

## 4. 消 ILC 警告不能只换序列化入口——显式 context 会丢 converter 多态

**场景**：`test_snapshot.py` 稳定 1 failed：turn diff 序列化成 `{"lineOps":[{"type":"append"}]}` 空壳（`AppendLinesOp.newLines` 全丢）。托管/AOT/stash 后均复现；xUnit 685 全绿但未覆盖该路径，成了漏网之鱼。

**原因**：为消 IL2026/IL3050，把三处 `Serialize(turn, TurnJsonOptions)` 改成显式 `EmueraJsonContext.Default.TurnRecord`。但 context 的 `JsonSourceGenerationOptions` **不含 Converters**——`LineOpConverter`/`TurnOpConverter`（按运行时类型装箱 Write 的多态序列化）不参与 → 抽象基类 `LineOp` 直接序列化只输出 type 鉴别符，子类字段全丢。`TurnJsonOptions`（带 Converters + TypeInfoResolver）才是正确形态。

**教训**：
1. **源生成 context 序列化抽象基类/接口成员时不会自动多态 dispatch**——多态必须靠 `[JsonPolymorphic]`（会引入 `$type` 字段破坏 wire）或自定义 converter（挂在 options.Converters）。换序列化入口（context TypeInfo vs options）会静默改变 wire，务必有内容级断言兜底。
2. 消 `Serialize(T, JsonSerializerOptions)` 警告的正解是 **JsonTypeInfo 重载**：`Serialize(turn, (JsonTypeInfo<TurnRecord>)TurnJsonOptions.GetTypeInfo(typeof(TurnRecord)))`——`GetTypeInfo(Type)` 与 TypeInfo 重载均无 RUC/RDC 标注（反射验证），converter 链完整保留，wire 逐字节一致。别用"换 context"这种看似等价的手段。
3. **e2e 的文本内容断言能抓到单元测试漏掉的 wire 回归**——xUnit 685 绿 ≠ diff 内容正确；test_snapshot 的 diff 内容断言是这条防线的最后一道。

## 5. 壳层反射 JSON 是 NativeAOT 白屏根因，且异常传播缺陷掩盖了崩溃

**场景**：生产 `Emuera.Maui` 开 `PublishAot=true` 构建成功（26.7MB 纯 NativeAOT），但真机/模拟器**白屏**。实验项目（最小壳）NativeAOT 正常——差异在壳层代码。

**取证**（MuMu x64 + logcat）：
- 进程存活（`pidof` 有值）但 `dumpsys activity top` 无 WebView 视图行；`edge://inspect` 看不到条目。
- logcat 抓到 **`FATAL EXCEPTION: System.InvalidOperationException: JsonSerializerIsReflectionDisabled`**，堆栈：`BridgeHost.HandleScanGames` → `JsonSerializer.Serialize(new {...})`（匿名类型反射序列化）→ JNI 回调 `BridgeClient.ShouldOverrideUrlLoading`。
- 前端 `scanGames` 已正常到达 C#（bridge 链路好的），崩溃发生在 C# 侧回包序列化。

**原因**（两层叠加）：
1. **NativeAOT 禁用反射序列化**：`JsonSerializer.Serialize(匿名类型)` 抛 `JsonSerializerIsReflectionDisabled`（IL3050 警告的运行时形态）。壳层 BridgeHost 有 18 处匿名类型，Core 层已治理过（EmueraJsonContext），壳层漏了。
2. **NativeAOT 异常传播缺陷掩盖崩溃**：异常从 WebView JNI 回调抛出时，`mono.android.Runtime.propagateUncaughtException` 的 JNI 导出在 NativeAOT 下**不存在**（logcat: `No implementation found for ... propagateUncaughtException`）→ 异常无法正常终止进程/打印崩溃框 → **UI 线程挂起** → 表现是"白屏+进程存活"，而不是崩溃退出。这就是"pidof 有值 + 无 crash 日志 + 无 console 报错"矛盾的根因。

**修复**：
1. 新增 `Emuera.Maui/Json/MauiJsonContext.cs`：全部壳层消息改具名 record + `[JsonSourceGenerationOptions(PropertyNamingPolicy = CamelCase)]` 源生成上下文，wire 与匿名类型逐字节一致（camelCase 字段名、null 仍写出）。
2. `BridgeHost.cs` 15 处 + `MainPage.xaml.cs` 1 处匿名类型 → `MauiJsonContext.Default.Xxx`。TurnRecord 不动（走 Core `TurnJsonOptions`，已挂 EmueraJsonContext）。
3. 验证：0 FATAL，进入游戏选择界面。

**教训**：
1. **NativeAOT 排查白屏，先查"进程是否存活"**：存活=托管层异常被吞/挂起（JNI 回调内异常 + propagateUncaughtException 缺失），崩溃退出=Java 层/链接问题。logcat 搜 `FATAL EXCEPTION` 能直接定位。
2. **"构建成功 ≠ 可运行"**：NativeAOT 的 IL 警告（IL2026/IL3050）是运行时崩溃的预告——**IL3050 的运行时形态就是 `JsonSerializerIsReflectionDisabled` 抛异常**。凡是 `JsonSerializer.Serialize(new {...})` 匿名类型/未注册类型，AOT 下必崩，代码评审就要拦。
3. **实验项目验证通过 ≠ 生产项目可过**：最小壳无桥接消息/无复杂 JSON，NativeAOT 差异全在壳层业务代码。门控验证必须用真实生产路径（scanGames → HandleScanGames → gamesScanned 回包）。
4. 壳层 JSON 与 Core 同模式治理：新增消息类型必须走 `MauiJsonContext`（壳层）/`EmueraJsonContext`（Core），禁止匿名类型。

## 6. NativeAOT 目录隔离的 CS0579 根治——重定向后必须补 DefaultItemExcludes

**场景**：`Emuera.Maui/Directory.Build.props` 在 `PublishAot=true` 时重定向 `BaseOutputPath=bin-aot\`、`BaseIntermediateOutputPath=obj-aot\`。构建报连环 `CS0579 特性重复`（AssemblyInfo/.NETCoreApp/TargetPlatform 等），且**构建期间并发清理会变成文件锁 Access denied**。

**原因**：SDK 默认 `DefaultItemExcludes` 含 `$(BaseIntermediateOutputPath)/**`——重定向后变成 `obj-aot/**`，**旧的 `obj/` 目录不再被排除**，其中残留的普通构建生成物（`obj/Debug/net10.0-windows.../App.g.cs`、`obj/Release/.../AssemblyInfo.cs` 等）全部回流进编译 glob，与新目录生成物重复 → CS0579/MAUIX2000/CS0234 连环报错。命令行全局传 `-p:BaseIntermediateOutputPath` 还会传染 ProjectReference（Core）同病。

**修复**：`Directory.Build.props` 的 PublishAot 分支显式补回排除：
```xml
<DefaultItemExcludes>$(DefaultItemExcludes);obj/**;bin/**</DefaultItemExcludes>
```
一次性根治，之后 AOT 构建无需每次手动清 `obj/`。

**教训**：
1. **重定向 `Base*OutputPath` 后必须同步补 `DefaultItemExcludes`**，否则 SDK 对旧目录的默认排除失效——这是"输出目录重定向"方案的必配项，不是可选项。
2. **命令行全局传 `Base*OutputPath` 是毒药**：全局属性传染整个项目图（ProjectReference 全中招）。目录级隔离必须放 `Directory.Build.props`（仅本目录项目生效），并保留"构建前清残留"的兜底。
3. **构建期间不要并发清理中间产物**：`dotnet publish` 运行中删除 `obj/` 下文件会与构建写文件竞争 → `UnauthorizedAccessException: Access to the path ... denied`（MSB3491）。等构建完全结束（进程退干净）再清理，或依赖 DefaultItemExcludes 根治后不再需要清理。
4. 此类"中间文件污染"的排障顺序：先看错误是编译期（CS0579 重复源）还是文件系统（Access denied 锁），前者清残留+修排除，后者查并发/残留进程。
