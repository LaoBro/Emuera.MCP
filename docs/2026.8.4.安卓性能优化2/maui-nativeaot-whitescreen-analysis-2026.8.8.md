# MAUI 壳 NativeAOT 白屏根因分析（3.4 门控中期）

> 日期：2026-08-08
> 前置：`nativeaot-device-validation-2026.8.8.md`（实验项目最小冒烟通过）→ 本记录分析**生产 `Emuera.Maui` 开启 NativeAOT 后真机白屏**的原因。
> 状态：构建已成功复现（26.7MB 纯 NativeAOT APK）；白屏根因锁定为**壳层 JNI/前端加载链路在 NativeAOT 下的差异**，附真机验证路径。

---

## 1. 结论摘要

1. **构建层问题已解决**：生产 `Emuera.Maui` 的 NativeAOT APK 已在本机成功构建（`PublishAot=true` + `RunAOTCompilation=false` + `android-arm64`）。产物：`Emuera.Maui/bin-aot/Release/net10.0-android/android-arm64/com.emuera.maui-Signed.apk`（26.7MB，**纯 NativeAOT**——包内 0 个托管 dll，仅 `lib/arm64-v8a/libEmuera.Maui.so`，wwwroot 前端资源 58 项完整打包，index.html 及其引用的 js/css 均在）。
2. **白屏不是构建产物缺失**：wwwroot 打包完好，排除"前端资源没进 APK"这一假设。
3. **白屏根因**：壳层 WebView 加载/桥接链路在 NativeAOT 下的行为差异。按嫌疑排序：
   - **(A) `ConfigureAndroidWebView` 的 `WebViewHandler.Mapper.AppendToMapping` 在 NativeAOT 下未生效或 PlatformView 类型不匹配** → `AllowFileAccessFromFileURLs`/`AllowUniversalAccessFromFileURLs` 缺失 → `file://` 加载 Vite ES Module/CSS 被 CORS 拦截 → **白屏**（LESSONS 已记录的历史坑，NativeAOT 下重现路径不同）。
   - **(B) `AndroidJsBridge.Attach` 的自定义 JNI 绑定在 NativeAOT 下失败**：`AddJavascriptInterface`（`Bridge : Java.Lang.Object` + `[JavascriptInterface]`）、`WebViewAssetLoader.Builder()`（AndroidX.WebKit）、`SetWebViewClient(BridgeClient)`、`GameAssetPathHandler`（`Java.Lang.Object, WebViewAssetLoader.IPathHandler`）——NativeAOT 的 Android Java interop 为 experimental（"no built-in Java interop"），自定义 `Java.Lang.Object` 子类的 ACW 注册在 AOT 下可能失效 → `Attach` 抛异常或 bridge 静默不可用。
   - **(C) `AndroidEnableMarshalMethods=false`（XAGNM7009 workaround）与 NativeAOT JNI 通道冲突**：android-perf-2.md §3.3 已登记此风险点。实验项目同开关但 JNI 面窄（仅 MAUI 官方封装的 WebView/FilePicker）；生产项目有大量自定义 JNI 绑定，受影响面大。
4. **警告来源已定位**：NativeAOT 构建多出的警告分两层——Core 层（IL2026×15/IL3050×10/IL207x×5，与验证报告 §5.2 已登记清单一致）与 **MAUI 壳层新增**（BridgeHost.cs 18 处匿名类型 `JsonSerializer.Serialize(new {...})` 全部触发 IL2026/IL3050，程序集级 IL2104/IL3053 汇总）。壳层警告此前未治理。

---

## 2. 构建复现（隔离目录方案）

### 2.1 问题：中间文件污染（CS0579）

首次构建暴露了用户警告的"目录污染"问题：

```text
error CS0579: "System.Reflection.AssemblyCompanyAttribute"特性重复
  Emuera.Headless.Core\obj\Release\net10.0\...AssemblyInfo.cs   ← 旧普通构建残留
  Emuera.Headless.Core\obj-aot\Release\net10.0\...AssemblyInfo.cs ← 旧 AOT 验证残留
```

**机制**：命令行 `-p:BaseIntermediateOutputPath=obj-aot/` 是**全局 MSBuild 属性**，会传染给 ProjectReference（Core）。重定向后 SDK 默认 `DefaultItemExcludes` 从 `obj/**`、`bin/**` 变为 `obj-aot/**`、`bin-aot/**`——**旧的 `obj/` 目录不再被排除**，其中所有 `.cs` 生成物（AssemblyInfo、App.g.cs、XamlTypeInfo.g.cs 等）回流进编译 glob，与新目录生成的重复 → CS0579/MAUIX2000/CS0234 连环报错。

### 2.2 解决：`Emuera.Maui/Directory.Build.props`（与 CLI 同模式）

```xml
<Project>
  <PropertyGroup Condition="'$(PublishAot)' == 'true'">
    <BaseOutputPath>bin-aot\</BaseOutputPath>
    <BaseIntermediateOutputPath>obj-aot\</BaseIntermediateOutputPath>
  </PropertyGroup>
</Project>
```

- 只对 `Emuera.Maui` 项目生效，不传染 Core（Core 走默认 obj/）。
- 仅 `PublishAot=true` 时重定向——普通 Release（Mono Full AOT）与 Debug 不受影响。
- **前置条件**：构建前清理旧 `obj/`、`obj-aot/` 残留（本次构建前已清理 Core/Maui 的旧中间产物）。

### 2.3 可复现构建命令

```bash
dotnet publish Emuera.Maui/Emuera.Maui.csproj -f net10.0-android -r android-arm64 -c Release \
  -p:PublishAot=true -p:RunAOTCompilation=false \
  -p:AndroidPackageFormats=apk \
  -p:PublishDir=artifacts/nativeaot/maui-android-arm64/ \
  -p:TreatWarningsAsErrors=false \
  -p:SkipVueBuild=true
```

（`SkipVueBuild=true` 复用已构建的 `wwwroot/`；若需重建前端则去掉该参数。）

---

## 3. ILC 警告清单（"多出来很多相关警告"的出处）

### 3.1 壳层新增警告（此前未治理，NativeAOT 门控必须处理）

**A. BridgeHost.cs 匿名类型 JSON 反射序列化（18 处）**——全部触发 IL2026+IL3050：

| 位置 | 代码形态 |
|---|---|
| `BridgeHost.cs:408/416/440/451/479/501/516/585/619/654/695/717/770/776/953/978` 等 | `JsonSerializer.Serialize(new { type = ..., ... })` |

**风险**：NativeAOT 运行时**反射序列化被禁用**（`JsonSerializerIsReflectionDisabled`），这些调用在 AOT 下**运行时直接抛异常**。尤其 `Start()` 内的 `PushLayoutMessage()`/`PushConfigMessage()`（`BridgeHost.cs:953/978`）——`HandleReady` → `Start` 时首帧即触发，游戏循环启动即崩。

**修复方向**：与 Core 的 3.3 迁移同模式——为 MAUI 壳层新增源生成上下文（如 `MauiJsonContext`），覆盖全部匿名类型（改为具名 record + `[JsonSerializable]`）。

**B. 程序集级汇总**：

```text
IL2104: Assembly 'Emuera.Maui' produced trim warnings
IL3053: Assembly 'Emuera.Maui' produced AOT analysis warnings
IL3053: Assembly 'Mono.Android' produced AOT analysis warnings
```

### 3.2 Core 层警告（与验证报告 §5.2 已登记清单一致，非新增）

- `TurnRecord.cs:191/207` IL2026/IL3050（`Serialize(T, options)` 重载，JsonTypeInfo 重载已在运行时使用，静态分析保守标记）
- `EraBinaryDataWriter/Reader`、`Creator.Method.cs` DataTable XML 系列 IL2026/IL3050（`test_datatable_aot.py` 13/13 实测可用，豁免附证据）
- `PluginManager.cs:292/293/305` IL2026/IL2072（Android/SAF 路径禁用，豁免）
- `Lang.cs:1514/1531` IL2070/IL2075（queryManagedClass 反射，待评估）

---

## 4. 白屏根因分析

### 4.1 实验项目（成功） vs 生产项目（白屏）的差异

| 维度 | 实验 `AndroidNativeAot`（✅ 真机通过） | 生产 `Emuera.Maui`（❌ 白屏） |
|---|---|---|
| WebView 内容 | `HtmlWebViewSource`（内存 HTML 字符串） | `file:///android_asset/wwwroot/index.html`（APK assets + ES Module + CSS） |
| 自定义 JNI 绑定 | 无 | `Bridge : Java.Lang.Object` + `[JavascriptInterface]`、`BridgeClient : WebViewClient`、`GameAssetPathHandler : Java.Lang.Object, IPathHandler`、`SafResultCallback` |
| WebViewAssetLoader | 无（不引用 AndroidX.WebKit） | `WebViewAssetLoader.Builder()` + `SetWebViewClient`（AndroidX.WebKit 1.9.0） |
| Handler 定制 | 无 | `WebViewHandler.Mapper.AppendToMapping("emueraWebView", ...)`（CORS 放行设置所在） |
| DI/时序 | 无 DI | `App.CreateWindow` DI 解析 `MainPage`；`MainPage` 构造早于 `MainActivity.OnCreate` |
| 启动编排 | 极简 | `BridgeHost` 编排 + `Start()` 首帧即匿名类型 JSON 序列化 |

### 4.2 白屏触发链路（嫌疑排序）

**嫌疑 A（最可能）：`file://` CORS 放行设置未生效 → ES Module/CSS 被拦截**

- 历史 LESSONS 已记录：`file://` 下 Vite ES Module + 独立 CSS 被 CORS 拦截 → 白屏；解决靠 `ConfigureAndroidWebView` 里 `AllowFileAccessFromFileURLs=true` + `AllowUniversalAccessFromFileURLs=true`。
- 该设置在 `WebViewHandler.Mapper.AppendToMapping` 回调内（`MauiProgram.cs:131-147`）。**NativeAOT 下 MAUI handler mapper 的注册链若被裁剪或回调未触发**，设置缺失 → 白屏。
- 实验项目用 `HtmlWebViewSource` 完全绕开此路径，故成功。

**嫌疑 B：`AndroidJsBridge.Attach` JNI 链路在 NativeAOT 下失败**

- `AddJavascriptInterface`/`WebViewAssetLoader`/`SetWebViewClient` 全为 JNI；NativeAOT 的 Java interop experimental。若 `Attach` 抛异常（`OnWebViewHandlerChanged` 是 `async void`，异常传播到 SynchronizationContext）或 bridge 静默不可用，Vue 的 `postInput` 走 `bridge://` iframe fallback，交互失效。
- `AndroidEnableMarshalMethods=false`（csproj:36，XAGNM7009 workaround）与 NativeAOT JNI 通道的冲突是 §3.3 已登记风险，进入 3.4 前必须重评。

**嫌疑 C：`Start()` 首帧匿名类型 JSON 序列化运行时崩溃**

- `HandleReady` → `Start()` → `PushLayoutMessage()`（`JsonSerializer.Serialize(new {...})`）——NativeAOT 下反射序列化禁用 → 抛异常 → 游戏循环启动失败。虽不直接造成 WebView 白屏，但会造成"进了应用却无游戏画面/功能全挂"。

### 4.3 真机验证路径（区分嫌疑 A/B/C）

连接设备后：

```bash
adb install -r Emuera.Maui/bin-aot/Release/net10.0-android/android-arm64/com.emuera.maui-Signed.apk
adb logcat -c
adb shell monkey -p com.emuera.maui 1
adb logcat -d -s EmueraMaui EmueraWV AndroidRuntime Mono MonoDroid-Debug
```

判读：
- 若 `AndroidJsBridge.Attach`/`ConfigureAndroidWebView` 日志缺失或报 JNI 异常 → **嫌疑 B**（JNI 链路）或 **嫌疑 A**（mapper 未触发）。
- 若 WebView `Navigated` 显示 `Success` 但页面空白 + WebView console 有 CORS 报错（`chrome://inspect` 或 `EmueraWV` tag）→ **嫌疑 A**（file:// CORS）。
- 若点击游戏后无 turn 输出、`[bridge] Starting game loop` 后立刻异常 → **嫌疑 C**（JSON 反射）。
- 启动期无 `[maui] MauiProgram.CreateMauiApp completed` → MAUI 框架层初始化问题（XAML/DI）。

---

## 5. 建议（3.4 门控收尾顺序）

1. **壳层 JSON 源生成迁移**（P0，必做）：BridgeHost 18 处匿名类型 → 具名 record + `MauiJsonContext` 源生成上下文，消灭 IL2026/IL3050，修掉"进游戏即崩"的运行时隐患。
2. **重评 `AndroidEnableMarshalMethods=false`**（P0）：与 NativeAOT JNI 通道冲突风险已登记两年轮，进入壳层门控前必须给结论（开/关/条件化）。
3. **真机取证**（P0）：按 §4.3 抓 logcat，确认白屏落在嫌疑 A 还是 B，再定向修（A→调整 WebView 配置注入方式；B→重写 JNI 绑定或等 .NET 11 CoreCLR）。
4. **保留 `Directory.Build.props` 隔离方案**（已落地）：NativeAOT 构建固定走 `bin-aot/obj-aot`，普通版本不受污染；构建前需清旧残留（可封装为脚本）。
5. **警告治理记录**：壳层警告入 `nativeaot-verify-report.md` §5.2 同表，逐条豁免或清零，不许静默 suppress。

---

## 6. 产物与关联

- 构建产物：`Emuera.Maui/bin-aot/Release/net10.0-android/android-arm64/com.emuera.maui-Signed.apk`（26.7MB）
- 目录隔离：`Emuera.Maui/Directory.Build.props`（PublishAot 条件重定向）
- 关联文档：`nativeaot-device-validation-2026.8.8.md`（实验项目）、`nativeaot-verify-report.md`（3.2/3.3）、`android-perf-2.md` §3.4（壳层门控）

---

## 7. 真机取证实证（2026-08-08 晚，MuMu 模拟器）——白屏根因最终确认

> 前置：用户反馈"构建出的签名 APK 依旧白屏"→ 用 MuMu 模拟器（x86_64，WHPX 加速）装 NativeAOT x64 版取证。

### 7.1 取证过程

| 步骤 | 命令 | 结果 |
|---|---|---|
| 安装 | `adb install -r com.emuera.maui-Signed.apk`（x64 NativeAOT） | ✅ Success |
| 启动 | `adb shell monkey -p com.emuera.maui ...` | 白屏复现 |
| 进程存活 | `adb shell pidof com.emuera.maui` | **PID 存活**（非崩溃退出） |
| 顶层 Activity | `adb shell dumpsys activity top` | MainActivity 前台，**无 WebView 视图行** |
| WebView 调试 | `edge://inspect` | **无 com.emuera.maui 条目** |
| logcat | `adb logcat -d -v time` | **抓到 FATAL EXCEPTION（见下）** |

### 7.2 铁证：FATAL EXCEPTION 堆栈

```text
FATAL EXCEPTION: main
net.dot.jni.internal.JavaProxyThrowable: System.InvalidOperationException: JsonSerializerIsReflectionDisabled
    at System.Text.Json.ThrowHelper.ThrowInvalidOperationException_JsonSerializerIsReflectionDisabled()
    at System.Text.Json.JsonSerializerOptions.ConfigureForJsonSerializer()
    at System.Text.Json.JsonSerializer.GetTypeInfo(JsonSerializerOptions, Type)
    at Emuera.Maui.BridgeHost.HandleScanGames(JsonElement root) + 0x275
    at Emuera.Maui.BridgeHost.OnInputFromJs(String message) + 0x55e
    at AndroidJsBridge.BridgeClient.ShouldOverrideUrlLoading(...) + 0x181
```

**白屏完整链路**（此前嫌疑 C 命中，A/B 排除）：

1. ✅ WebView 加载 `file:///android_asset/wwwroot/index.html` 成功，Vue 前端 JS 正常执行；
2. ✅ Vue 启动发 `{"type":"scanGames"}`，经 `BridgeClient`（JNI）正常到达 C# `OnInputFromJs`；
3. ❌ `HandleScanGames` 内 `JsonSerializer.Serialize(new {...})` **匿名类型反射序列化**在 NativeAOT 下被禁用 → 抛 `JsonSerializerIsReflectionDisabled`；
4. ❌ 异常在 WebView JNI 回调内传播时，`mono.android.Runtime.propagateUncaughtException` 的 JNI 导出在 NativeAOT 下不存在 → **异常无法正常终止进程，UI 线程挂起** → 白屏（进程存活、无 console 报错、edge://inspect 无条目——全部现象得到解释）。

**关键排除**：`ConfigureAndroidWebView completed`、`AndroidJsBridge.Attach completed: emueraBridge registered`、`Setting WebView URL` 全部成功——**嫌疑 A（CORS 配置未生效）与嫌疑 B（JNI 桥接失败）均排除**。真正根因是壳层 JSON 反射序列化 + NativeAOT 异常传播缺陷的叠加。

### 7.3 修复：`MauiJsonContext` 源生成（2026-08-08 落地）

- 新增 `Emuera.Maui/Json/MauiJsonContext.cs`：全部壳层消息 DTO（FolderPicked / SafDirectoryPicked / GamesScanned / DirectoriesListed / GameExited / PermissionStatus / GameThreadStatus / AgentLog / Layout / Config / BackButtonPressed）+ `[JsonSourceGenerationOptions(PropertyNamingPolicy = CamelCase)]`——wire 契约与匿名类型逐字节一致（字段名 camelCase、null 仍写出）。
- `BridgeHost.cs` 15 处 + `MainPage.xaml.cs` 1 处匿名类型 → 具名 record + `MauiJsonContext.Default.Xxx`。
- `TurnRecord`（error turn）不动——已走 Core `AgentJsonlProtocol.TurnJsonOptions`（`TypeInfoResolver = EmueraJsonContext.Default`）。

### 7.4 附带根治：`Directory.Build.props` 补 DefaultItemExcludes

CS0579 中间文件污染从"每次手动清理"升级为**构建配置根治**：

```xml
<PropertyGroup Condition="'$(PublishAot)' == 'true'">
  <BaseOutputPath>bin-aot\</BaseOutputPath>
  <BaseIntermediateOutputPath>obj-aot\</BaseIntermediateOutputPath>
  <DefaultItemExcludes>$(DefaultItemExcludes);obj/**;bin/**</DefaultItemExcludes>
</PropertyGroup>
```

重定向 Base*OutputPath 后 SDK 默认排除 `obj/**` 失效，旧普通构建残留回流编译；显式补回排除后，AOT 构建不再受 `obj/` 污染（CS0579 根治，无需每次清理）。

### 7.5 修复后验证（MuMu x64 NativeAOT）

```text
ConfigureAndroidWebView completed
OnWebViewHandlerChanged fired → AndroidJsBridge.Attach completed: emueraBridge registered
Setting WebView URL: file:///android_asset/wwwroot/index.html
BridgeClient post: {"type":"scanGames"}        ← 前端正常
[bridge] OnInputFromJs: {"type":"scanGames"}
[bridge] HandleScanGames: rootDir=             ← 无异常！
```

**0 个 FATAL**，应用成功进入游戏选择界面——白屏修复确认。

### 7.6 待办（后续门控）

1. 游戏加载链路（loadGame → EmueraRuntimeInitializer → PushLayoutMessage）在 NativeAOT 下仍需真机验证——`PushLayoutMessage`/`PushConfigMessage` 已源生成，但游戏初始化路径的其余反射点（Core 层 IL2026/IL3050 已登记豁免）未实测；
2. 重复上述修复模式检查 Core 层剩余反射序列化点（TurnRecord 已走源生成；DataTable/PluginManager 反射为 IL2026 豁免清单，非崩溃路径）；
3. `AndroidEnableMarshalMethods=false` 重评仍待结论（本次取证证明 JNI 桥接本身可用，但自定义 Java.Lang.Object 子类的 ACW 在 AOT 下的行为未做压力验证）。
