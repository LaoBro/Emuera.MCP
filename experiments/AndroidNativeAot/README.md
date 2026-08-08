# Android Native AOT 实验项目

这是一个独立的 .NET MAUI Android Native AOT 冒烟项目。它不替换 `Emuera.Maui`，用于隔离验证以下边界：

- `Microsoft.Android` workload 是否能选择 Native AOT runtime 并完成原生链接；
- MAUI Activity 是否能在 Native AOT APK 中启动；
- Android WebView 是否能渲染第一帧；
- MAUI 文件选择器是否能拉起 SAF，并返回文件结果；
- `Emuera.Headless.Core` 是否能被同一个 Native AOT 应用引用。

Android Native AOT 仍是实验性功能。这个项目通过构建和静态 APK 检查，不代表生产兼容性。当前已在实体 arm64 设备完成最小启动、WebView 和 SAF 冒烟；真正的生产门槛仍包括完整游戏、输入、存档和性能回归。

## 环境要求

- .NET SDK 10.x；当前仓库验证版本为 `10.0.302`；
- Android workload，包含 `Microsoft.Android.Runtime.NativeAOT.36.android-arm64`；
- Android SDK/NDK；设置 `ANDROID_HOME` 或 `ANDROID_SDK_ROOT`；
- 一台启用 USB 调试的 Android arm64 设备，或 arm64 Android 模拟器；x86_64 模拟器不能安装本实验 APK；
- `adb` 在 `PATH` 中。

检查 workload：

```powershell
dotnet workload list
```

## 构建 APK

从仓库根目录执行：

```powershell
dotnet restore .\experiments\AndroidNativeAot\Emuera.AndroidNativeAot.csproj

dotnet publish .\experiments\AndroidNativeAot\Emuera.AndroidNativeAot.csproj `
  -f net10.0-android `
  -r android-arm64 `
  -c Release `
  -p:PublishAot=true `
  -p:RunAOTCompilation=false `
  -p:AndroidPackageFormats=apk `
  -p:PublishDir=artifacts\nativeaot\android-arm64\
```

`PublishAot=true` 是 Native AOT；`RunAOTCompilation=true` 是 Mono Full AOT，不能混用。实验构建默认允许分析警告输出，不能把这个配置直接当作生产发布配置。

产物位于：

```text
experiments/AndroidNativeAot/artifacts/nativeaot/android-arm64/com.emuera.androidnativeaot-Signed.apk
```

检查 APK 是否确实走 Native AOT：

```powershell
$apk = '.\experiments\AndroidNativeAot\artifacts\nativeaot\android-arm64\com.emuera.androidnativeaot-Signed.apk'

$androidSdk = $env:ANDROID_HOME
if ([string]::IsNullOrWhiteSpace($androidSdk)) {
  $androidSdk = $env:ANDROID_SDK_ROOT
}
if ([string]::IsNullOrWhiteSpace($androidSdk)) {
  throw 'Set ANDROID_HOME or ANDROID_SDK_ROOT before checking the APK.'
}

$buildTools = Get-ChildItem (Join-Path $androidSdk 'build-tools') -Directory |
  Sort-Object Name -Descending |
  Select-Object -First 1
if ($null -eq $buildTools) {
  throw 'No Android build-tools directory was found.'
}

& (Join-Path $buildTools.FullName 'apksigner.bat') verify --verbose $apk
& (Join-Path $buildTools.FullName 'zipalign.exe') -c -P 16 4 $apk
```

包内应包含 `lib/arm64-v8a/libEmuera.AndroidNativeAot.so`，不应依赖应用程序集目录来运行托管代码。

## 安装和冒烟

连接设备后执行：

```powershell
adb devices
adb install -r $apk
adb logcat -c
adb shell am force-stop com.emuera.androidnativeaot
adb shell monkey -p com.emuera.androidnativeaot 1
adb logcat -d -s AndroidRuntime Mono AndroidNativeAot
```

启动后应看到 `WebView OK`。点击“选择文件（SAF）”并选取任意文件，页面状态应显示选中的文件名。记录以下结果：

1. 冷启动是否进入页面；
2. WebView 是否完成首帧渲染；
3. SAF 是否能打开、取消并返回结果；
4. 是否出现 `FATAL EXCEPTION`、JNI、ILLink 或 WebView 错误。

真机验证结果（2026-08-08）：APK 成功启动，WebView 显示 `WebView OK`；SAF 可以打开文件选择器、取消并返回；选择文件后页面能显示文件名。该结果只覆盖本实验项目的最小 Android interop 冒烟，不等同于生产壳完整流程验证。

## 与生产壳的关系

生产应用仍是 `Emuera.Maui`。本实验项目只验证 workload 和关键 Android interop，避免直接改变生产壳的 `RunAOTCompilation=true` Mono Full AOT 基线。实验通过后，再把同样的 `PublishAot` 配置迁移到生产壳，并执行完整游戏、SAF、WebView 和性能回归。

## 运维脚本（2026-08-08 白屏取证沉淀）

生产壳 NativeAOT 白屏排查中沉淀的 3 个脚本，可直接复用：

### `collect_whitescreen_logs.bat <apk路径>`
一键取证：清 logcat → 安装 APK → 启动应用 → 等 8 秒 → 抓过滤日志到 `logcat_whitescreen.txt`（过滤 `EmueraMaui EmueraWV chromium AndroidRuntime MonoDroid Dotnet FATAL`）。
用于快速复现/采集 NativeAOT 白屏或崩溃现场。

### `clean_build_artifacts.js`
删除 **Core 项目**（`Emuera.Headless.Core`）的 `obj/`、`obj-aot/`、`bin-aot/` 下全部 `.cs`/`.xaml` 生成物——CS0579 中间文件污染（旧目录 AssemblyInfo 回流编译）的清理手段。
> 注意：自 2026-08-08 起 `Emuera.Maui/Directory.Build.props` 已用 `DefaultItemExcludes` 无条件排除 `obj-aot/**;bin-aot/**`（PublishAot 时另补 `obj/**;bin/**`），**Maui 项目无需再手动清理**；本脚本仅用于 Core 或历史残留兜底。

### `clean_maui_obj.js`
删除 **Maui 项目**（`Emuera.Maui`）`obj/`、`bin/` 下所有文件（不含 `obj-aot/bin-aot`）——清理普通构建残留用。
> 同样已由 `Directory.Build.props` 的 `DefaultItemExcludes` 取代（PublishAot 分支排除 `obj/**;bin/**`），仅在极端残留场景兜底。

**脚本用途边界**：均为**构建期维护工具**，不参与运行时；删除对象全部为可再生成的构建中间产物（`obj/`、`bin/`、`obj-aot/`、`bin-aot/`），不含源码。

