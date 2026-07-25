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
