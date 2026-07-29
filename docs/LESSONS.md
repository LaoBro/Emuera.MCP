# 失败教训

## Windows 终端 ANSI 转义默认不可用

**场景**：按钮选择模式提示行用 `\x1b[2K\x1b[1G` 清除整行。

**结果**：转义序列被原样输出为 `[2K[1G`，未生效。

**原因**：Windows 控制台默认未启用 `ENABLE_VIRTUAL_TERMINAL_PROCESSING`，需 P/Invoke `SetConsoleMode` 才能支持 ANSI 转义。

**解决**：`Program.TrySetupWindowsConsole()` 在启动时通过 `SetConsoleMode` 启用虚拟终端处理，成功后设置 `Program.AnsiEnabled = true`。`AgentCliProtocol` 根据 `AnsiEnabled` 选择 ANSI 序列或 `SetCursorPosition` fallback。Linux/macOS 终端默认支持 ANSI，无需额外处理。

**教训**：在 Windows 终端中不要假设 ANSI 转义可用。必须先检测并启用虚拟终端支持，并保留 `SetCursorPosition` 作为 fallback。

## 用填满整行的方式清除行会触发自动换行

**场景**：用 `\r` + `new string(' ', Console.WindowWidth)` + `\r` 清除提示行。

**结果**：每次按键提示行下移一行，终端输出逐行下推。

**原因**：写入 `WindowWidth` 个字符填满整行后，终端自动换行（光标移到下一行行首），第二个 `\r` 只能回到当前行（已在新行）。

**教训**：不要用填满整行的方式清除行。用 `SetCursorPosition` 定位到行首，只写实际需要的空格数覆盖旧内容，避免触及行尾触发自动换行。启用虚拟终端处理后，`\x1b[2K` 是最可靠的清行方式。

## 终端 Ambiguous Width 字符渲染宽度的多层问题

**核心问题**：游戏地图使用 Box Drawing（━┃┏┗）、Block Elements（░▒▓█）、Geometric Shapes（●■）、Miscellaneous Symbols（★☆）等 Unicode Ambiguous Width 字符，终端输出时严重对不齐。

### 问题一：游戏内部宽度计算遗漏

`IsWideChar` 最初只覆盖了 CJK 汉字和全角 ASCII 变体，完全遗漏了 Ambiguous 字符。更关键的是，游戏脚本对不同组的 Ambiguous 字符宽度预期不同：

| 字符组 | 游戏期望宽度 | 说明 |
|---|---|---|
| Box Drawing (U+2500-257F) | 2（全角） | 连续线条按全角拼接 |
| Geometric Shapes (U+25A0-25FF) | 2（全角） | ●■◆▲ 等 |
| Misc Symbols (U+2600-26FF) | 2（全角） | ★☆ 等 |
| Block Elements (U+2580-259F) | 1（半角） | ░▒▓█ 用于填充海洋，按半角设计 |

**解决**：`IsWideChar` 将前三组标记为全角，Block Elements 保持半角。

### 问题二：终端实际渲染宽度与游戏期望不一致

不同终端对 Ambiguous Width 字符的渲染宽度不同，且同一终端内不同字符组的渲染宽度也可能不同。

**关键发现**：
- **conhost（cmd/旧PS）**：Box Drawing 半角，Geometric 全角，Misc Symbols 全角，Block Elements 半角。
- **Windows Terminal（conpty）**：四组全部半角——WT 的字符占位宽度由 conpty 固定的 wcwidth 逻辑决定，不受字体选择影响。
- **光标位移探测法**（写入字符后读 `CursorLeft` delta）在所有能交互的终端中都可靠，是唯一可移植的判定手段。

**解决**（分组探测 + 数据驱动查表）：
1. 4 个独立布尔：`BoxDrawingIsWide`、`GeometricIsWide`、`MiscSymbolsIsWide`、`BlockElementsIsWide`，默认全 false。
2. `DetectCharWidths()` 对 4 个代表字符（`━ ● ★ █`）各做一次光标位移探测。
3. `ReplaceForTerminal` 纯数据驱动查表：

   | 组 | 游戏期望 | 实测全角 | 实测半角 |
   |---|---|---|---|
   | Box Drawing | 2 | 不处理 | 补空格（字符1列+空格1列=2列） |
   | Geometric | 2 | 不处理 | 补空格 |
   | Misc Symbols | 2 | 不处理 | 补空格 |
   | Block Elements | 1 | 替换盲文（░→⠒ ▒→⠶ ▓→⠿ █→⣿） | 不处理 |

4. 探测不可用的环境（mintty 直连、Unix、输出重定向）→ 默认非 CJK + 提供 `--term-width-hint=cjk|latin|auto` CLI 开关覆盖。

**关于 Block Elements 替换**：░▒▓█ 在 CJK 终端中强制全角渲染，无法通过 `IsWideChar` 改变，只能替换为半角的盲文点阵字符。盲文在 cmd/PowerShell 中渲染为半角且视觉效果接近原始灰度，优于 ASCII 字符（`.` `:` `#`）。注意 `·` (U+00B7) 在 MS Gothic 中是全角，不可靠。

**关于字体设置**：
- 已删除 `TrySetConsoleFont()`（`SetCurrentConsoleFontEx`）和 `isCjkFont` 逻辑。该 API 只对 conhost 有效，在 Windows Terminal 中被静默忽略，`GetCurrentConsoleFontEx` 恒返回占位值"新宋体"。
- 改为 `PrintTerminalGuidance()`：基于探测结果打印精准提示，尊重用户对终端环境的控制权。

**关于 Windows Terminal 的字体与占位**：WT 中字体只影响字形绘制宽度，不影响字符占位宽度。Ambiguous 字符在 WT 中始终半角占位，即使选 MS Gothic 也不变。因此改字体的目的是让字形宽度匹配占位列数，而非改变占位。补空格解决占位层对齐，字体解决字形层匹配——两者缺一不可。

**首要不变量**：一切换行以游戏本身的逻辑为准。`ReplaceForTerminal` 只做逐字符的替换/补空格，绝不插入换行符、改变 `IsLineEnd`、增减行数或重排折行。校准前后，字符串的行结构、按游戏列计的长度完全一致。

**教训**：
- **行为探测优先于属性检测**：字体名是属性（在新式终端不可靠），光标位移是行为（在所有终端可靠）。不要用只有部分终端支持的 API 解决通用问题。
- **数据驱动查表优于条件分支**：把"字符组 × 游戏期望 × 实测宽度 → 处理方式"做成查表，新增字符组只改数据不改逻辑。
- **同属 Ambiguous 类别的字符需逐组区分**：不同范围在同一终端的渲染宽度可能不同，游戏对它们的宽度预期也可能不同（线条=全角，灰度=半角）。
- **宽度计算始终与游戏设计一致，终端差异在输出层弥补**：`IsWideChar` 定义游戏期望宽度，`ReplaceForTerminal` 通过补空格/替换把终端实际视觉宽度校准回游戏期望。
- **自动设字体是反模式**：改为"暴露问题 + 清晰提示"更健壮，也尊重用户控制权。
- **实测数据比理论推演重要**：理论上"MS Gothic 让 Geometric 全角"在 conhost 中正确，但在 WT 中并非如此。探测告诉你"实际做了什么"，而非"应该做什么"。

## Win32 P/Invoke 结构体中 BOOL 必须用 int 而非 bool

**场景**：`KEY_EVENT_RECORD.bKeyDown` 声明为 `bool`，`FOCUS_EVENT_RECORD.bSetFocus` 同理。

**结果**：所有键盘事件被当作释放事件（`bKeyDown=false`）忽略，键盘输入完全失灵。

**原因**：Win32 `BOOL` 是 4 字节 `int`（0/非0），C# `bool` marshalling 语义不同。`bool` 在结构体中的布局和对齐可能与 Win32 `BOOL` 不一致，导致后续字段偏移错位，读取到错误值。

**解决**：将 `bKeyDown`、`bSetFocus` 等 Win32 BOOL 字段改为 `int`，判断时用 `!= 0`。

**教训**：P/Invoke 结构体中 Win32 `BOOL` 一律用 `int`，不要用 `bool`。`bool` 只适用于 Win32 API 参数（P/Invoke 会自动 marshalling），不适用于嵌套在结构体中的字段。

## CHAR_UNION 嵌套结构体导致 marshalling 偏移错位

**场景**：`KEY_EVENT_RECORD.uChar` 声明为 `CHAR_UNION`（含 `UnicodeChar` 和 `AsciiChar` 两个 `byte` 字段的 union）。

**结果**：`uChar` 读取到错误值，`uChar==0` 的过滤条件把方向键等控制键全部跳过。

**原因**：`CHAR_UNION` 作为显式布局结构体嵌套在 `KEY_EVENT_RECORD` 中，字段偏移计算容易出错。Win32 原始定义中 `uChar` 是一个 `char`（2 字节 Unicode），直接映射更简单可靠。

**解决**：去掉 `CHAR_UNION`，`uChar` 直接声明为 `char`。

**教训**：P/Invoke 结构体尽量与 Win32 原始布局一一对应，避免不必要的嵌套结构体。能用简单类型直接映射的就不要用 union 模拟。

## IME 过滤条件不能基于 uChar==0

**场景**：键盘事件过滤中，`uChar==0 && !IsModifierKeyCode(vk)` 被当作 IME 中间态跳过。

**结果**：方向键、PgUp/PgDn、Home/End、Insert/Delete、F1-F24 等控制键的 `uChar` 都是 `\0`，全部被错误跳过。

**原因**：IME 组合输入的中间态特征是 `wVirtualKeyCode == VK_PROCESSKEY (0xE5)`，不是 `uChar==0`。控制键有合法的 `wVirtualKeyCode` 但 `uChar` 为空。

**解决**：只过滤 `vk == VK_PROCESSKEY`，其他所有按键事件都放行给 `ProcessKey`。

**教训**：过滤条件必须精确匹配目标特征，不能凭直觉扩大范围。`uChar==0` 是控制键的正常属性，不是异常状态。

## C# 静态字段按声明顺序初始化

**场景**：`s_logEnabled` 在 `s_logPath` 之后声明，`s_logPath` 的初始化调用 `ResolveLogPath()` 读取 `s_logEnabled`。

**结果**：`s_logEnabled` 还是默认值 `false`，`ResolveLogPath()` 直接返回 `null`，日志功能不生效。

**原因**：C# 静态字段按声明顺序初始化。`s_logPath` 先初始化，此时 `s_logEnabled` 尚未被赋值，仍是 `false`。

**解决**：交换声明顺序，`s_logEnabled` 在 `s_logPath` 之前。

**教训**：有依赖关系的静态字段必须按依赖顺序声明。被依赖的字段必须先声明。或者改用静态构造函数/方法避免初始化顺序问题。

## GetDisplayWidth 不处理 ANSI 转义码，不能用于计算居中偏移

**场景**：用 `GetDisplayWidth(formattedLine)` 计算 `formattedWidth`，再算 `leadingOffset = (formattedWidth - segmentsTotalWidth) / 2`。

**结果**：居中偏移计算错误，按钮列范围与屏幕实际位置对不上。

**原因**：`FormatLineForTerminal` 在 ANSI 模式下生成带 `\x1b[...m` 转义码的串，`GetDisplayWidth` 逐字符计算宽度时把转义码的每个字符都算作 1 列，导致 `formattedWidth` 虚高。而居中 padding 是 `FormatLineForTerminal` 内部基于纯文本宽度计算的，不含 ANSI 码字符。

**解决**：直接数 `formattedLine` 的前导空格字符数（`CountLeadingSpaces`）。`FormatLineForTerminal` 对 CENTER 行生成 `new string(' ', pad) + styledText`，前导空格在 ANSI 码之前，直接数即可得到屏幕上的起始列。

**教训**：涉及终端显示宽度的计算必须排除 ANSI 转义码。如果宽度计算函数不处理转义码，就不能用于含转义码的字符串。对于居中偏移这种简单场景，直接数前导空格比通用宽度计算更可靠。

## PressEnterKey 会按 \\n 拆分输入，不能传 "\n"

**场景**：鼠标点击在非按钮模式下派发 `DispatchInput("\n")` 推进游戏。

**结果**：点一下推进两次，相当于按了两次回车。

**原因**：`PressEnterKey` 中 `input.Split(["\\n", "\r\n", "\n", "\r"])` 把 `"\n"` 拆成 `["", ""]` 两个空字符串，循环执行两次 `RunEmueraProgram("")`，每次推进一个 AnyKey 状态。

**解决**：`DispatchInput("")` 传空字符串。空串不被 split，只产生一个元素，只推进一次。

**教训**：调用 `PressEnterKey`/`DispatchInput` 时，输入值会被宏解析系统 split 处理。`"\n"` 是宏分隔符，不能作为"回车"语义使用。AnyKey/EnterKey 等待下空字符串即可推进。

## 鼠标事件 dwEventFlags 过滤不能忽略 DOUBLE_CLICK

**场景**：鼠标事件过滤条件 `dwEventFlags != 0`，只接受单击。

**结果**：双击的第二次点击 `dwEventFlags=DOUBLE_CLICK(0x0002)` 被忽略，快速点击时第二下被吞掉。

**原因**：conhost 对双击的第二次按下设置 `dwEventFlags=DOUBLE_CLICK` 而非 0，但该事件仍然是有效的左键按下，应该响应。

**解决**：过滤条件改为 `dwEventFlags != 0 && dwEventFlags != DOUBLE_CLICK`，允许单击和双击，只忽略移动/释放/滚轮。

**教训**：`dwEventFlags` 的值不只区分事件类型，还标记同一类型的不同交互方式（单击 vs 双击）。过滤时要明确哪些 flag 值需要保留，不能简单地 `!= 0` 一刀切。

## MAUI Android 调试：Console.WriteLine 被 mono-stdout 淹没

**场景**：在 MAUI Android 上用 `Console.WriteLine("[maui] ...")` 输出诊断日志。

**结果**：日志完全被 Mono 运行时内部输出（GC、JIT、程序集加载）淹没，即使 `grep` 也很难找到目标行。

**原因**：Android 上 `Console.WriteLine` 输出到 logcat tag `mono-stdout`，该 tag 包含大量 Mono 运行时日志。

**解决**：改用 `Android.Util.Log.Info("EmueraMaui", message)` 输出到自定义 tag `EmueraMaui`，用 `adb logcat EmueraMaui:V *:S` 零噪音过滤。`System.Diagnostics.Debug.WriteLine` 输出到 `debug` tag 噪音次之，可作备选。

**教训**：MAUI Android 调试日志不要用 `Console.WriteLine`，必须用平台原生的 `Android.Util.Log` 指定独立 tag。`adb logcat -s Tag` 是精确过滤的正确语法，`*:V` 会覆盖 `-s` 效果导致噪音重回。

## MAUI Android WebView 远程调试需显式启用

**场景**：app 白屏，需要用 Chrome DevTools 检查 Vue 前端 JS 运行时错误。

**结果**：`chrome://inspect` 看不到 `com.emuera.maui` 的页面。

**原因**：Android WebView 默认关闭远程调试，需要代码中显式调用 `Android.Webkit.WebView.SetWebContentsDebuggingEnabled(true)`。

**解决**：在 `MauiProgram.ConfigureAndroidWebView()`（`MauiProgram.cs`）中添加该静态调用。此外，`edge://inspect` 比 `chrome://inspect` 连接更稳定——Chrome 有时返回 HTTP 404。

**教训**：WebView 远程调试不是默认开启的。只要用到 MAUI WebView + Android，必须在初始化阶段显式启用。Edge DevTools 是比 Chrome 更稳定的备用方案。

## file:// 协议下 WebView 被 CORS 策略拦截 ES Module 和 CSS

**场景**：MAUI Android 用 `file:///android_asset/wwwroot/index.html` 加载 Vite 构建的 Vue SPA。

**结果**：白屏，WebView Console 报错：
- `Access to script at 'file:///...js' from origin 'null' has been blocked by CORS policy`
- `Access to CSS stylesheet at 'file:///...css' from origin 'null' has been blocked by CORS policy`

**原因**：Vite 默认构建产物使用 ES Module（`<script type="module">`）和独立 CSS 文件。Android WebView 在 `file://` 协议下默认禁止跨源加载（`origin: null` 无法加载 `file://` 资源）。

**解决**：在 `ConfigureAndroidWebView()` 中设置：
```csharp
wv.Settings.AllowFileAccessFromFileURLs = true;
wv.Settings.AllowUniversalAccessFromFileURLs = true;
```
两行分别放行 `file:// → file://` 和 `file:// → 任意源` 的跨域请求。

**替代方案**：也可让 Vite 构建为 IIFE 格式 + 内联 CSS（`vite-plugin-singlefile`），但修改 WebView 设置更简单，且不改变前端构建流程。

**教训**：`file://` 协议有严格的跨域限制，混合 Vite ES Module 构建产物时必须在 WebView 设置中显式放行。这是 MAUI + Vite 组合在 Android 上的必踩坑。

## Android 状态栏遮挡 MAUI WebView 内容

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

## MAUI Android 白屏——缺少 MauiSplashScreen / MauiIcon 导致 splash 过渡失败

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

## MAUI Android 白屏——缺少 MainApplication.cs 导致 MAUI 永不初始化

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

## MAUI Android 启动时序——MainPage 构造早于 MainActivity.OnCreate

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

## .NET Android `RegisterForActivityResult` 必须在 `super.onCreate()` 之后调用

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

## MAUI Android——AddJavascriptInterface 桥接在 WebView 中不可靠

**场景**：MAUI Android WebView 中通过 `AddJavascriptInterface` 注册 C# 对象供 JS 调用。
启动时 `window.emueraBridge.postMessage()` 能正常工作，用户交互后静默失效——JS 端对
象存在、调用不抛异常，但 C# 方法永不被触发。

**结果**：点击按钮无任何反应，C# 端收不到消息。`adb logcat` 也无 `Bridge.PostMessage` 日志。

**原因**：Android WebView 的 `AddJavascriptInterface` 在 MAUI 壳中存在可靠性问题——
JS 引擎线程与 UI 线程间的 Java bridge 可能因 WebView 进程重启、JS context 重建或
线程安全问题而静默断开。JS 端仍看到 `window.emueraBridge` 对象（来自旧绑定缓存），
调用却不再触发 C#。

**解决**：使用 `WebViewClient.ShouldOverrideUrlLoading` + 隐藏 iframe 作为 JS→C# 备用通道：
```csharp
// C# 端——WebViewClient 拦截 bridge:// URL
platformView.SetWebViewClient(new BridgeClient(this));

class BridgeClient : WebViewClient
{
    public override bool ShouldOverrideUrlLoading(WebView view, IWebResourceRequest request)
    {
        if (request?.Url?.Scheme == "bridge")
        {
            var path = request.Url.Host + request.Url.Path;
            BridgeUrlReceived?.Invoke(path);
            return true;
        }
        return base.ShouldOverrideUrlLoading(view, request);
    }
}
```

```typescript
// JS 端——隐藏 iframe 发送 bridge:// URL
function sendBridgeUrl(action: string): void {
  const iframe = document.createElement('iframe');
  iframe.style.display = 'none';
  iframe.src = `bridge://${action}`;
  document.body.appendChild(iframe);
  setTimeout(() => document.body.removeChild(iframe), 500);
}
```

**教训**：`AddJavascriptInterface` 不应作为 MAUI WebView 的唯一 JS→C# 通道。
`ShouldOverrideUrlLoading` + URL scheme 是 Android 文档推荐的标准备选方案，
比 Java bridge 更底层、更可靠。二者可并存——启动初期走 bridge，备用 URL 拦截兜底。

## bridge:// URL 拦截——不要忘记 Query 参数

**场景**：`WebViewClient.ShouldOverrideUrlLoading` 中手动拼接 URL 组件构建消息路径。

**结果**：`bridge://post?msg=...` 的 `?msg=...` 部分被丢弃，收到的消息只有 `"post"`（不含数据）。

**原因**：`request.Url.Host + request.Url.Path` 只含域名和路径，不含 Query 字符串。
`bridge://post?msg=...` 解析后：`Host="post"`, `Path=""`, `Query="?msg=..."`。
需同时拼接 `Query` 或用 `Android.Net.Uri.GetQueryParameter("msg")` 直接取参数。

**教训**：URL 解析用 SDK 的 `Uri.GetQueryParameter()`，不要手动拼接 `Host+Path+Query`。

## SAF——`BuildChildDocumentsUriUsingTree` 第一个参数必须是原始树 URI

**场景**：`SafGameDirAccessor` 中把任意传入的路径（可能是子文档 URI）当树 URI 传给
`DocumentsContract.BuildChildDocumentsUriUsingTree(firstParam, parentDocId)`。

**结果**：子目录导航失败，`ResolveSubPath` 退化为 `basePath`（返原值），`Validate()` 校验失败。

**原因**：`BuildChildDocumentsUriUsingTree` 的第一个参数**必须是 `ACTION_OPEN_DOCUMENT_TREE`
返回的原始树 URI**（如 `content://.../tree/primary%3Aemuera`），不能是子文档 URI
（如 `content://.../tree/primary%3Aemuera/document/primary%3Aemuera%2Fcsv`）。

**解决**：缓存 `_treeAndroidUri` 字段（原始树 URI），所有 `BuildChildDocumentsUriUsingTree`
调用统一用它做第一个参数，第二个参数从传入路径提取文档 ID。

## SAF——`DirAccessor` 必须按路径选择，不能全局替换

**场景**：`MainActivity.OnCreate` 无条件将 `GamePaths.Current.DirAccessor` 替换为
`SafGameDirAccessor`。

**结果**：内置 `test_game`（本地文件路径）走 `SafGameDirAccessor` → 所有目录存在性检查
返回 false → `Preload.Load` 认为目录不存在 → 文件逐 I/O 慢路径 → 极其缓慢 + GAMEBASE.CSV
被跳过 → 空壳游戏。

**解决**：`OnReloadGame` 和 `ScanAndPushGames` 等所有使用 DirAccessor 的地方**按路径判断**：
```csharp
var dirAccessor = path.StartsWith("content://")
    ? (IGameDirAccessor?)SafGameDirAccessor.Instance
    : new FileSystemGameDirAccessor();
```
`SafGameDirAccessor` 只用于 SAF content URI 路径，本地文件路径始终走 `FileSystemGameDirAccessor`。

**教训**：DirAccessor 的选择是**路径属性**，不是**全局属性**。一个应用中可能同时存在本地测试游戏
和 SAF 外部游戏，不能全局替换。

## `Preload` 文件编码检测——必须用字节级 BOM 检测，不能直接 `ReadAllText` + `Split`

**场景**：`Preload.ReadAllLinesViaAccessor` 初版用 `dirAccessor.ReadAllText(path)` + `Split('\n')`。

**结果**：ERA 游戏的 ERB 文件通常是 SHIFT-JIS 编码。UTF-8 `StreamReader`（默认）解码 SHIFT-JIS
字节流时，`\r`/`\n` 字节可能被当作多字节日文字符的一部分被"吞掉"，换行符丢失，两行合成一行，
解析器报错"无法解析的行"。

**解决**：加 `ReadAllBytes` 到 `IGameDirAccessor`，`Preload` 调用 `dirAccessor.ReadAllBytes`
获取原始字节数组 → 传入 `EncodingHandler.ReadAllLinesFromBytes`（BOM 检测 → UTF-8 尝试 →
SHIFT-JIS 回退 → `StringReader.ReadLine` 分行）。

**教训**：文件读取必须保留原始字节级别的编码检测路径。`ReadAllText` 预设单一编码不够，
必须以字节数组为中介，复用原有的 `BOM → UTF-8 → SHIFT-JIS` 级联检测。

## SAF——`GetDocumentId` vs `GetTreeDocumentId` 不能混用

**场景**：在 `SafGameDirAccessor` 中统一用 `DocumentsContract.GetDocumentId(docUri)` 提取文档 ID。

**结果**：
- 对树 URI（`.../tree/...`）抛异常——`GetDocumentId` 只对文档 URI 有效
- 对含 `%2F`（编码的 `/`）的文档 URI（`.../document/...%2Ftest_game`），只返回第一段（如 `...:emuera`，丢掉了 `/test_game`）
- `DirectoryExists` 检查了错误的目录但返回 True（因为根目录存在）→ 假阳性

**解决**：
```csharp
private static string ResolveDocId(Android.Net.Uri docUri)
{
    var uriStr = docUri.ToString();
    // 文档 URI（.../document/...）：手动从路径提取完整文档 ID
    var posDoc = uriStr.LastIndexOf("/document/");
    if (posDoc >= 0)
        return Uri.UnescapeDataString(uriStr[(posDoc + 10)..]);
    // 树 URI（.../tree/...）：用标准的 GetTreeDocumentId
    if (uriStr.Contains("/tree/"))
        return DocumentsContract.GetTreeDocumentId(docUri);
    return DocumentsContract.GetDocumentId(docUri);
}
```

**教训**：
- `GetDocumentId` 只适用于文档 URI，对树 URI 无效
- `GetTreeDocumentId` 对文档 URI 返回树根的文档 ID（不是你想要的子文档 ID）
- 含 `%2F` 的文档 URI 不能依赖 `GetDocumentId`——它只返回编码 `%2F` 之前的部分
- 路径中同时含 `/tree/` 和 `/document/` 的 URI（SAF 常见格式），优先检查 `/document/`

## SAF——`CombinePath` 不能字符串拼接 content URI

**场景**：`SafGameDirAccessor.CombinePath` 实现为 `$"{basePath.TrimEnd('/')}/{filename}"`。

**结果**：`content://.../document/...:emuera/test_game/csv/ABL.CSV` → Android 解析时忽略
`/ABL.CSV`（因为文档 URI 只识别 `/document/` 后的第一个路径段），实际打开的是 csv 目录
本身 → `OpenInputStream` 抛 `EISDIR (Is a directory)`。

**解决**：用 `DocumentsContract.BuildDocumentUriUsingTree(treeUri, docId + "/" + filename)`：
```csharp
var baseUri = Android.Net.Uri.Parse(basePath);
var docId = ResolveDocId(baseUri);
var childUri = DocumentsContract.BuildDocumentUriUsingTree(_treeAndroidUri!, $"{docId}/{filename}");
return childUri.ToString();
```

**教训**：SAF content URI 不是文件系统路径——不能用字符串拼接构造子文件路径。必须用
`BuildDocumentUriUsingTree` 把父文档 ID 和文件名组合成完整子文档 ID 再构造 URI。

## `EraStreamReader.Open` 必须走 DirAccessor，不能直接 `File.ReadAllBytes`

**场景**：`EraStreamReader.Open(path)` → `EncodingHandler.ReadAllLinesWithDetection(filepath)`
→ `File.ReadAllBytes(path)`。对 SAF content URI 路径，`File.ReadAllBytes` 阻塞或失败。

**解决**：让 `Open` 委托给 `OpenOnCache`（缓存未命中时走 `DirAccessor.ReadAllBytes`→
`EncodingHandler.ReadAllLinesFromBytes`），从内容源级别统一所有文件读取路径：
```csharp
public bool Open(string path, string name)
{
    return OpenOnCache(path, name);
}
public bool OpenOnCache(string path, string name)
{
    filepath = path; filename = name;
    curNo = 0; nextNo = 0;
    // 先查 Preload 缓存
    var cached = Preload.TryGetFileLines(path);
    if (cached != null) { _fileLines = cached; return true; }
    // 缓存未命中：走 DirAccessor（SAF content URI）
    var dirAccessor = GamePaths.Current?.DirAccessor;
    if (dirAccessor != null)
    {
        var bytes = dirAccessor.ReadAllBytes(path);
        if (bytes != null)
        { _fileLines = EncodingHandler.ReadAllLinesFromBytes(bytes); return true; }
    }
    // 最后 fallback 原始 File.ReadAllBytes
    try { _fileLines = EncodingHandler.ReadAllLinesWithDetection(filepath); return true; }
    catch { Dispose(); return false; }
}
```

**教训**：`EncodingHandler.ReadAllLinesWithDetection(path)` 是原始 I/O 漏斗——
任何地方传入 content URI 都会阻塞。`EraStreamReader` 是 ERA 引擎所有文件读取的
中心入口，必须在**这一层**切换到 DirAccessor，而不是在调用方逐个修补。

## SAF `ResolveSubPath` 必须大小写不敏感（Android ext4）

**场景**：`ResolveSubPath(gamePath, "csv")` 用 `string.Equals(name, subDir, StringComparison.Ordinal)`
进行大小写敏感匹配。

**结果**：真实游戏目录名是 `CSV`（大写），`ResolveSubPath` 返回 fallback `gamePath`，
`GamePaths.CsvDir` 被设为游戏根目录而非 `.../CSV` → 所有 CSV 文件查不到 → 空壳游戏。

**修复**：改用 `OrdinalIgnoreCase`：
```csharp
if (string.Equals(name, subDir, StringComparison.OrdinalIgnoreCase))
```

**教训**：Android 文件系统（ext4/f2fs）是大小写敏感的，ERA 游戏目录命名约定不统——
有的用 `CSV/ERB`（大写），有的用 `csv/erb`（小写）。SAF 路径下的子目录查找必须
大小写不敏感。

## Android——`NavigationPage.Navigated` 对 `file:///android_asset/` URL 不触发

**场景**：在 `OnWebViewNavigated` 事件中触发自动启动内置游戏。

**结果**：事件永不被触发，因为 Android 上 `file:///android_asset/wwwroot/index.html`
的加载不走 MAUI 的 `Navigated` 事件路径。

**解决**：改用 `WebView.HandlerChanged` 事件（在 `OnPageFinished` 之后的时机）触发，
或直接在 `Page.OnAppearing` 中延迟执行。

**教训**：Android `file:///android_asset/` 导航事件在 MAUI 中不可靠（`Navigated` 不触发，
`Navigating` 也可能不触发）。需要自动触发的逻辑应放在 `HandlerChanged`（必触发）或
`OnAppearing` 中加延迟执行。

## SAF URI——URL 编码 `%2F` 导致 `StartsWith` 前缀匹配失效

**场景**：`GetErbFilesFromCache` 用 `dirPath.TrimEnd('/') + "/"` 构造前缀，
再用 `cacheKey.StartsWith(prefix)` 过滤 Preload 缓存中的文件。

**结果**：永远匹配到 0 个文件。`ResolveSubPath` 返回的 URI 中 `/` 编码为 `%2F`
（如 `...%2FERB`），而构造的前缀用字面 `/`（`...%2FERB/`）。缓存键中的路径分隔符
也编码为 `%2F`（如 `...%2FERB%2Ffile.ERB`），所以 `StartsWith("...%2FERB/")` 永远
不匹配 `"...%2FERB%2Ffile.ERB"`。

**解决（初版，已过时）**：不依赖 raw URI 前缀匹配，改用扩展名过滤缓存键。  
**后续修正**：`Path.GetFileName(contentUri)` 会得到**整段 documentId**，不能当相对路径 Key  
（见下文「逻辑文件名 / SafPath」）。正确做法是 Unescape documentId 后做前缀比较与相对路径：
```csharp
// SafPath.IsUnderRoot(dir, k) + SafPath.GetRelativePathFromRoot(dir, k)
var erbFiles = Preload.GetAllCachedKeys()
    .Where(k => k.EndsWith(".ERB", StringComparison.OrdinalIgnoreCase)
        && SafPath.IsUnderRoot(dirPath, k))
    .Select(k => new KeyValuePair<string, string>(
        SafPath.GetRelativePathFromRoot(dirPath, k), k))
    .ToList();
```

**教训**：SAF URI 中的路径分隔符是 `%2F`（URL 编码的 `/`），不是字面 `/`。
不要用 `TrimEnd('/')` + `"/"` 构造 SAF URI 的目录前缀，更不要用 `StartsWith`
做目录范围过滤。扩展名过滤只能解决「找得到文件」，**相对路径 Key 仍须走 documentId 语义**。

## `Process.Initialize` 返回 false 时缺少异常诊断

**场景**：`Process.Initialize` 返回 false，`ConsoleStateManager` 设
`ConsoleState.Error` 状态，游戏显示"错误：未知错误"。

**结果**：Exception 被 `Initialize` 的 catch 块捕获，调用 `handleException`
输出到游戏控制台（可能被清空或覆盖），开发者在 adb 日志中看不到异常详情。

**原因**：`Initialize` 的 catch 块：
```csharp
catch (Exception e)
{
    handleException(e, null!, true);
    console.PrintSystemLine(trsl.ErhLoadingError.Text);
    return false;
}
```
`handleException` 把异常信息写入 `console`（游戏输出缓冲区），但这个缓冲区
后续可能被 `OutputLog`/`RefreshStrings` 清空或覆盖，不会输出到 `Console.WriteLine`
能到达的 logcat DOTNET tag。

**解决**：在 catch 块中加 `Console.WriteLine` 日志：
```csharp
catch (Exception e)
{
    Console.WriteLine($"[proc] Initialize exception: {e}");
    handleException(e, null!, true);
    ...
}
```

**教训**：任何在 MAUI/Android 游戏内部被 catch 的异常，默认不会出现在
`adb logcat` 可见的日志流中。`handleException` 把错误写入游戏输出缓冲区，
但该缓冲区可能在后续操作中被覆盖。必须显式加 `Console.WriteLine` 才能让
异常信息出现在 logcat DOTNET tag。

## 运行时脚本 `File.*` 调用是全量迁移的最后一块

**场景**：`Creator.Method.cs`（10 处）和 `VariableEvaluator.cs`（26 处）
中有大量 `File.Exists`/`Directory.Exists`/`File.ReadAllText`/`FileStream`
等原始 System.IO 调用。初始化路径的 `File.*` 调用已迁移，但运行时脚本路径
（ERB `GETDIR`、`EXIST`、`SAVEDATA`、`LOADDATA` 等指令）还有大量未迁移。

**解决**：新增 `SafCompat` 辅助类，包装 `File.*`/`Directory.*` 调用：
```csharp
internal static bool FileExists(string path)
{
    if (!path.StartsWith("content://")) return File.Exists(path);
    return GamePaths.Current?.DirAccessor?.FileExists(path) ?? false;
}
```
在脚本函数中逐处替换。SAF 写路径（`SAVEDATA`/`FileStream`）暂不支持，
静默跳过并打出警告。

**教训**：SAF 文件读取迁移不是一次性的——它分布在初始化路径和运行时脚本路径
两个层次。初始化路径（Preload/EraStreamReader/Loader）是核心，运行时脚本路径
（Creator.Method/VariableEvaluator）是 ERB 脚本执行时触发的第二层。
后者同样重要但易被忽略。

## SAF——禁止对 content URI 使用 `Path.GetFileName*`（逻辑文件名 / SafPath）

**场景（2026-07）**：TK 在 SAF 下加载后，警告路径是整段 documentId；`PrepareERDFileNames`
用 `Path.GetFileNameWithoutExtension(path)` 作 EE_ERD 键。

**结果**：
- Win：`...\DVAR.erd` → key=`DVAR`
- SAF：`content://.../document/...%2FDVAR.erd` → key≈**整段 URL-encoded documentId**
- `erdFileNames.TryGetValue("DVAR")` 永远 miss；警告文件名不可读

实测（.NET）：
```text
Path.GetFileName(contentUri)           → 整段 document 段
Path.GetFileNameWithoutExtension(...)  → 去掉 .erd 后的整段 documentId
Path.GetExtension(contentUri)          → 往往仍是 .erd（末尾扩展名碰巧可用，不可依赖）
```

**解决**：Core 侧 `SafPath`：
1. 取 `/document/` 之后整段并 `Uri.UnescapeDataString` 得到 documentId  
2. 按 `/` 取最后一段 → 逻辑短名（`DVAR.erd`）  
3. `GetRelativePathFromRoot` 用 documentId 前缀差 → `SYSTEM\...\a.ERB`（对齐 `Config.getFiles`）  
4. **禁止**再对完整 content URI 调用 `Path.GetFileName*` 当游戏逻辑文件名

`SafGameDirAccessor.GetFileName` / `Config.getFiles` / `EraStreamReader` 默认名 / Preload 扩展名过滤
一律走逻辑短名。

**教训**：SAF 的路径分隔符活在 documentId 的 `%2F` 里，不在 URI path 的 `/` 上。
`System.IO.Path` 按最后一个 `/` 切片，对 document URI 永远错。凡「文件名 / 相对路径 / 扩展名 /
ERD 键」都要先解出逻辑路径。

## C# 诊断日志插入 `if` 无大括号会改控制流

**场景**：`ErhLoader.LoadHeaderFiles` 原代码：
```csharp
if (!noError)
    break;
```
诊断时在 `if` 与 `break` 之间插入两行日志且**未加大括号**：
```csharp
if (!noError)
    Console.WriteLine(...);  // 仅此行受 if 控制
    Console.Out.Flush();     // 总是执行
    break;                   // 总是执行
```

**结果**：142 个 ERH **只加载第 1 个**就退出；`noError` 仍可为 True（那一个文件成功）。
日志显示 `files=142` 但实际只处理了 KOJO 类头文件 → `#DIM DVAR` / `#DEFINE カラー_*` 未注册 →
ERB 阶段海量「解释できない識別子」。修好括号后：`ERH loaded=142/142, dimlines=1692`。

**教训**：
- C# 只认大括号，不认缩进；往单行 `if` 体插日志必须写成 `if (...) { log; break; }`
- 「列表长度正确」≠「循环真的跑完」——验收要打 **实际 loaded 计数**（如 `loaded=142/142`）
- 诊断代码与生产控制流绑在一起时，宁可用大括号块，也不要依赖「暂时只加一行」

## SAF——`emuera.config` 不能用 `File.Exists` / 字符串拼接

**场景**：`ConfigData` 用 `ExeDir + "emuera.config"` 与 `File.Exists(confPath)` 加载配置。

**结果（Android SAF + TK）**：
1. 字符串拼接把 `emuera.config` 粘进 documentId（缺 `%2F` 分隔）或路径对 `File.*` 无效  
2. `File.Exists(content://...)` **恒 false** → 配置从未读入  
3. `SystemSaveInBinary` 保持默认 **false**  
4. 全部 `#DIM ... SAVEDATA CHARADATA` 抛 CodeEE（需二进制存档）→ `LoadHeaderFiles noError=False`
   → `soft-return reason=ErhLoadHeaderFilesFalse`（加载「提前失败、输出变短」）

Windows 同游戏正常，是因为本地 `File.Exists` 能读到 `emuera.config`。

**解决**：
- 路径：`DirAccessor.CombinePath(exeDir, "emuera.config")`（内部 `ResolveDocId`，见 CombinePath 课）  
- 存在性：`SafCompat.FileExists` / DirAccessor，禁止对 content URI 用 `File.Exists`  
- 缺失时不要在 content URI 上 `SaveConfig()`（写路径未支持）  
- 验收日志：`[Config] LoadConfig: main=True, SystemSaveInBinary=True`（以**游戏目录那一次**为准；
  启动时内置 `files/emuera` 可能先 Load 一次且 Binary=False）

**教训**：配置加载与脚本加载是同一 SAF 语义问题。默认配置「看起来能跑」会在真正解析
大游戏 `#DIM SAVEDATA` 时集中爆雷。桌面能过 ≠ Android 读到了同一份 config。

## `CombinePath` 必须用 `ResolveDocId`，不能用 `GetDocumentId`

**场景**：`SafGameDirAccessor.CombinePath` 一度写 `DocumentsContract.GetDocumentId(baseUri)`。

**结果**：含 `%2F` 的子目录 document URI 只得到第一段 ID，拼出的子文件 URI 落在错误层级
（与「GetDocumentId vs GetTreeDocumentId」课同一根因）。读 `emuera.config`、拼 sav 路径都会错。

**解决**：与枚举/DirectoryExists 一致，一律 `ResolveDocId`（手动取 `/document/` 后整段并 Unescape）。

**教训**：同一 accessor 里「有的 API 用 ResolveDocId、有的用 GetDocumentId」是隐性回归源。
路径构造入口应只保留一种 documentId 提取方式。

## 加载通了不等于可玩——`FileStream` 存档在 SAF 上必炸

**场景（2026-07-30）**：TK 已进标题、开局选项正常；点「接受」触发 `@SYSTEM_AUTOSAVE`：
```
保存全局数据时发生错误 / SAVEDATA命令在存档时发生了意料之外的错误
```

**原因**：`VariableEvaluator.SaveGlobal` / `SaveTo` 仍：
```csharp
Config.CreateSavDir(); // Directory.CreateDirectory(content://…) 无效
new FileStream(SavDir + "global.sav", FileMode.Create, FileAccess.Write); // 打不开 content URI
```
`SavDir` 派生自 `Program.ExeDir`（SAF 下为 content URI）。选目录权限目前只有
`GrantReadUriPermission`，即便走 DocumentsContract 写也未接通。

**状态**：已知未完成项（方案 A：App 私有目录重定向 SavDir；方案 B：完整 SAF 写 API）。  
加载修复 **不会** 自动修好存档。

**教训**：
- 初始化只读路径与运行时写路径是两层；只测到标题会漏掉自动存档  
- `SafCompat` 对写「静默跳过」与 `SaveGlobal` catch 转 CodeEE 表现不同——用户看到的是硬错误  
- 验收清单应含：标题 → 开局接受/自动存档 → 继续遊戲读档

## 诊断推进会「揭开」下一层失败，勿误判为回归

**场景**：修掉「只加载 1 个 ERH」后，游戏输出突然变短，出现大量
「バイナリ型セーブ」必須警告，并 `ErhLoadHeaderFilesFalse`。

**原因**：以前几乎没跑 `#DIM`；全量 1692 条 dim 后才触发「未读到 SystemSaveInBinary」的真实错误。
再修 config 后标题通；再点接受才暴露 FileStream 存档。

**教训**：分层推进时，下一层失败是**进度**，不是「越修越坏」。用阶段日志区分：
`ERH loaded` / `Config SystemSaveInBinary` / `Initialize OK` / 运行时 SAVEDATA。
过时 handoff（如「Initialize false 主因」）必须显式作废，避免下一会话退回旧假设。

## adb logcat 中文路径乱码 ≠ 内存中路径损坏

**场景**：`firstKey=鍙ｄ伉\...KOJO_K4.ERH` 一类 mojibake；同时游戏已成功加载上千 ERB。

**原因**：logcat / PowerShell 控制台编码与进程内 UTF-16 字符串不一致。  
真正坏路径通常伴随 `OpenOnCache FAIL`、0 files、或 `hasDVAR` 类逻辑键错误。

**教训**：优先信**结构化验收字段**（`loaded=142/142`、`hasDVAR`、`SystemSaveInBinary`、
相对路径警告是否为 `SYSTEM\...`），不要仅凭 adb 中文乱码判定文件名损坏。
