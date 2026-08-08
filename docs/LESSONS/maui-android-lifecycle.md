# MAUI Android 生命周期 失败教训

> **TL;DR**：MAUI Android 的 Application 与 Activity 生命周期分离——`MainPage` 构造早于 `MainActivity.OnCreate`，平台单例必须延迟到 `OnAppearing` 读取；`RegisterForActivityResult` 必须 `super.onCreate()` 之后调用；白屏三连（splash 资源、MainApplication、状态栏）各有一套成因。

## 教训清单

| # | 标题 | 一句话 | 日期 |
|---|---|---|---|
| 1 | Console.WriteLine 被 mono-stdout 淹没 | Android 用 `Android.Util.Log` 自定义 tag，不用 Console | — |
| 2 | 状态栏遮挡 MAUI WebView | 全屏 WebView 需手动处理安全区域（状态栏高度 margin） | — |
| 3 | 白屏：缺 MauiSplashScreen/MauiIcon | splash 过渡依赖声明，缺失则卡白屏 | — |
| 4 | 白屏：缺 MainApplication.cs | MAUI 永不初始化，静默白屏 | — |
| 5 | MainPage 构造早于 MainActivity.OnCreate | 平台单例读取必须延迟到 OnAppearing | — |
| 6 | RegisterForActivityResult 时序 | 必须在 super.onCreate() 之后、onStart() 之前 | — |

---

## 1. MAUI Android 调试：Console.WriteLine 被 mono-stdout 淹没

**场景**：在 MAUI Android 上用 `Console.WriteLine("[maui] ...")` 输出诊断日志。

**结果**：日志完全被 Mono 运行时内部输出（GC、JIT、程序集加载）淹没，即使 `grep` 也很难找到目标行。

**原因**：Android 上 `Console.WriteLine` 输出到 logcat tag `mono-stdout`，该 tag 包含大量 Mono 运行时日志。

**解决**：改用 `Android.Util.Log.Info("EmueraMaui", message)` 输出到自定义 tag `EmueraMaui`，用 `adb logcat EmueraMaui:V *:S` 零噪音过滤。`System.Diagnostics.Debug.WriteLine` 输出到 `debug` tag 噪音次之，可作备选。

**教训**：MAUI Android 调试日志不要用 `Console.WriteLine`，必须用平台原生的 `Android.Util.Log` 指定独立 tag。`adb logcat -s Tag` 是精确过滤的正确语法，`*:V` 会覆盖 `-s` 效果导致噪音重回。

## 2. Android 状态栏遮挡 MAUI WebView 内容

**场景**：MAUI Android 全屏 WebView 加载 Vue SPA，顶部按钮无法点击。

**结果**：状态栏（时间、电量等）覆盖在 WebView 内容之上，被遮住的按钮点不到。

**原因**：MAUI `ContentPage` 默认不处理安全区域（Safe Area），WebView 从屏幕最顶部开始绘制，被系统状态栏遮住。

**解决**：在 `MainPage` 构造函数中添加 Android 专用方法，读取系统资源获取状态栏实际高度，转为 MAUI DIP 单位后设为 WebView 的 `Margin.Top`：
```csharp
int resourceId = Android.Content.Res.Resources.System.GetIdentifier(
    "status_bar_height", "dimen", "android");
int statusBarHeightPx = resourceId > 0
    ? Android.Content.Res.Resources.System.GetDimensionPixelSize(resourceId)
    : 0;
double density = DeviceDisplay.MainDisplayInfo.Density;
MainWebView.Margin = new Thickness(0, statusBarHeightPx / density, 0, 0);
```

**教训**：MAUI 全屏 `VerticalOptions="Fill"` 在 Android 上会绘制到系统栏下方。不要假设 ContentPage 会自动处理安全区域——状态栏高度必须通过平台 API 获取并手动应用为 margin/padding。不同设备的 status_bar_height 不同（有无挖孔屏、导航手势等），不能用静态值。`Resource.GetIdentifier` + `GetDimensionPixelSize` 是获取状态栏高度的标准跨版本方式。

## 3. MAUI Android 白屏——缺少 MauiSplashScreen / MauiIcon 导致 splash 过渡失败

**场景**：最小 MAUI Android 项目（`UseMaui=true`），Activity 使用 `Theme = "@style/Maui.SplashTheme"`。

**结果**：app 安装后打开白屏，无任何 UI 渲染。

**原因**：`Maui.SplashTheme` 在 `MauiAppCompatActivity` 启动时渲染 splash screen 然后过渡到 MAUI `Page`。这个过渡依赖 `MauiSplashScreen`（及可选 `MauiIcon`）在 csproj 中声明并由 MAUI build 生成的资源。如果 csproj 中没有 `MauiSplashScreen` 项，splash 资源缺失，过渡永远无法完成，UI 停留在白屏。

**解决**：在 csproj 中添加 `<MauiIcon>` 和 `<MauiSplashScreen>`：
```xml
<ItemGroup>
    <MauiIcon Include="Resources\AppIcon\appicon.svg" />
    <MauiSplashScreen Include="Resources\Splash\splash.svg" Color="#1E1E1E" BaseSize="128,128" />
</ItemGroup>
```
并提供对应的最小 SVG 文件（即使只是一个纯色矩形）。

**教训**：MAUI 项目即使不使用自定义图标，也需要提供 `MauiSplashScreen`。`Maui.SplashTheme` 构建链强依赖 splash 资源——没有它 splash 过渡失败且不会报明显错误（app 直接卡在白屏）。最小 MAUI 项目的最低资源要求是 splash SVG + csproj 声明。

## 4. MAUI Android 白屏——缺少 MainApplication.cs 导致 MAUI 永不初始化

**场景**：手动创建最小 MAUI Android 项目（非 `dotnet new maui` 模板），包含 `MauiProgram.cs` + `App.xaml` + `MainPage.xaml` + `MainActivity.cs`。

**结果**：app 安装后白屏，`adb logcat` 无 MAUI 相关日志。

**原因**：MAUI Android 需要两个入口类：
1. `MainActivity : MauiAppCompatActivity` — 承载 Platform View 的 Activity
2. `MainApplication : MauiApplication` — Android `Application` 子类，`[Application]` 属性注册，重写 `CreateMauiApp()` 调用 `MauiProgram.CreateMauiApp()`

缺少 `MainApplication`，Android 系统启动 `MauiAppCompatActivity` 时找不到 MAUI 宿主，MAUI 框架永不初始化，页面永不渲染，显示为白屏。`Maui.SplashTheme` 也无法完成 splash → app 过渡。

**解决**：添加 `Platforms/Android/MainApplication.cs`：
```csharp
[Application]
public class MainApplication : MauiApplication
{
    public MainApplication(IntPtr handle, JniHandleOwnership ownership)
        : base(handle, ownership) { }

    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
}
```
同时需要 `Platforms/Android/Resources/values/colors.xml`（MAUI 构建链生成主题时引用其中的颜色）。

**教训**：MAUI Android 项目必须同时有 `MainActivity` + `MainApplication` 两个入口类。`dotnet new maui` 模板自动生成这两个文件，手动建项目容易遗漏。缺少 `MainApplication` 时不会报编译错误或运行时异常——app 静默白屏。

## 5. MAUI Android 启动时序——MainPage 构造早于 MainActivity.OnCreate

**场景**：在 `MainPage` 构造或字段初始化器中访问 `MainActivity` 中初始化的静态单例（如 `SafService.Current`）。

**结果**：永远读到 `null`，后续在 `OnAppearing` 中读取则正确。

**原因**：MAUI Android 启动时序：
1. `MainApplication.OnCreate` → `CreateMauiApp()` → `MauiProgram.CreateMauiApp()` → `builder.Build()`
2. `App` 构造 → `MainPage = new MainPage()` — **此时 MainPage 构造执行**
3. 之后 Android 才启动 `MainActivity.OnCreate` — **此处初始化平台相关单例**

`MainPage` 构造函数（含字段初始化器）执行时，`MainActivity.OnCreate` 尚未运行，所有在 `MainActivity.OnCreate` 中初始化的静态字段均为 `null`。

**解决**：平台单例的读取必须延迟到 `OnAppearing()` 或事件处理器中（用户交互时），不能在构造函数/字段初始化器中读取。
```csharp
// ❌ 错误——构造时 MainActivity 还没跑
private readonly SafService? _saf = SafService.Current;

// ✅ 正确——OnAppearing 时 Activity 已就绪
protected override void OnAppearing()
{
    var saf = SafService.Current;
    if (saf != null) { ... }
}
```

**教训**：MAUI Android 的 `Application` 和 `Activity` 生命周期是分离的——`Application.OnCreate` 在 `Activity.OnCreate` 之前完成。任何依赖 `Activity` 上下文的初始化（如 `RegisterForActivityResult`、`SafService` 等）不能在 `MainPage` 构造阶段消费。

## 6. .NET Android `RegisterForActivityResult` 必须在 `super.onCreate()` 之后调用

**场景**：在 `MainActivity.OnCreate` 中，`base.OnCreate(savedInstanceState)` 调用之前注册 `ActivityResultLauncher`。

**结果**：`RegisterForActivityResult` 可能抛异常或返回无效 launcher（具体行为因 AndroidX 版本而异）。

**原因**：AndroidX `ComponentActivity.registerForActivityResult()` 标准调用时序是 `super.onCreate()` → `register`。在 `super.onCreate()` 之前调用，Activity 尚未进入 `CREATED` 状态，注册可能失败。

**解决**：
```csharp
protected override void OnCreate(Bundle? savedInstanceState)
{
    base.OnCreate(savedInstanceState); // 先调 super

    var launcher = RegisterForActivityResult(  // 再注册
        new ActivityResultContracts.OpenDocumentTree(),
        new SafResultCallback());
    ...
}
```

**教训**：`RegisterForActivityResult` 必须在 `super.onCreate()` 之后、`onStart()` 之前调用。标准 AndroidX 时序不可颠倒。
