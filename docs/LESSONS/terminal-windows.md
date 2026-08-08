# Windows 终端渲染 失败教训

> **TL;DR**：Windows 控制台默认不支持 ANSI 转义（需 `SetConsoleMode` 启用）；清行/对齐/宽度计算必须考虑终端实际行为（自动换行、Ambiguous 宽度、转义码占位）；宽度语义以游戏逻辑为准，终端差异在输出层校准。

## 教训清单

| # | 标题 | 一句话 | 日期 |
|---|---|---|---|
| 1 | Windows 终端 ANSI 转义默认不可用 | 需 P/Invoke `SetConsoleMode` 启用 `ENABLE_VIRTUAL_TERMINAL_PROCESSING`，保留 `SetCursorPosition` fallback | — |
| 2 | 填满整行清行触发自动换行 | 写满 `WindowWidth` 字符会换行，第二个 `\r` 回不到原行；用 `SetCursorPosition` 或 `\x1b[2K` | — |
| 3 | Ambiguous Width 字符宽度多层问题 | 按字符组（Box/Geometric/Misc/Block）分组探测；数据驱动查表；终端差异在输出层补空格/替换 | — |
| 4 | GetDisplayWidth 不处理 ANSI 转义码 | 含转义码串的宽度计算虚高；居中偏移直接数前导空格 | — |
| 5 | PressEnterKey 会按 \n 拆分输入 | 传 `"\n"` 被 split 成两个空串推进两次；传 `""` | — |
| 6 | 鼠标 dwEventFlags 过滤不能忽略 DOUBLE_CLICK | `!=0` 一刀切吞掉双击第二次点击 | — |
| 7 | CLI 渲染增强：ANSI 占位空格在样式段之外 | 无样式占位空格输出在转义码之前；断言用 StartsWith/EndsWith 而非 Contains | 2026-08-07 |
| 8 | ERB FONTSTYLE 位契约与内部枚举相反 | 重构"魔法数字→枚举引用"前确认两套值域；4/8 语义翻转 | 2026-08-07 |

---

## 1. Windows 终端 ANSI 转义默认不可用

**场景**：按钮选择模式提示行用 `\x1b[2K\x1b[1G` 清除整行。

**结果**：转义序列被原样输出为 `[2K[1G`，未生效。

**原因**：Windows 控制台默认未启用 `ENABLE_VIRTUAL_TERMINAL_PROCESSING`，需 P/Invoke `SetConsoleMode` 才能支持 ANSI 转义。

**解决**：`Program.TrySetupWindowsConsole()` 在启动时通过 `SetConsoleMode` 启用虚拟终端处理，成功后设置 `Program.AnsiEnabled = true`。`AgentCliProtocol` 根据 `AnsiEnabled` 选择 ANSI 序列或 `SetCursorPosition` fallback。Linux/macOS 终端默认支持 ANSI，无需额外处理。

**教训**：在 Windows 终端中不要假设 ANSI 转义可用。必须先检测并启用虚拟终端支持，并保留 `SetCursorPosition` 作为 fallback。

## 2. 用填满整行的方式清除行会触发自动换行

**场景**：用 `\r` + `new string(' ', Console.WindowWidth)` + `\r` 清除提示行。

**结果**：每次按键提示行下移一行，终端输出逐行下推。

**原因**：写入 `WindowWidth` 个字符填满整行后，终端自动换行（光标移到下一行行首），第二个 `\r` 只能回到当前行（已在新行）。

**教训**：不要用填满整行的方式清除行。用 `SetCursorPosition` 定位到行首，只写实际需要的空格数覆盖旧内容，避免触及行尾触发自动换行。启用虚拟终端处理后，`\x1b[2K` 是最可靠的清行方式。

## 3. 终端 Ambiguous Width 字符渲染宽度的多层问题

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

## 4. GetDisplayWidth 不处理 ANSI 转义码，不能用于计算居中偏移

**场景**：用 `GetDisplayWidth(formattedLine)` 计算 `formattedWidth`，再算 `leadingOffset = (formattedWidth - segmentsTotalWidth) / 2`。

**结果**：居中偏移计算错误，按钮列范围与屏幕实际位置对不上。

**原因**：`FormatLineForTerminal` 在 ANSI 模式下生成带 `\x1b[...m` 转义码的串，`GetDisplayWidth` 逐字符计算宽度时把转义码的每个字符都算作 1 列，导致 `formattedWidth` 虚高。而居中 padding 是 `FormatLineForTerminal` 内部基于纯文本宽度计算的，不含 ANSI 码字符。

**解决**：直接数 `formattedLine` 的前导空格字符数（`CountLeadingSpaces`）。`FormatLineForTerminal` 对 CENTER 行生成 `new string(' ', pad) + styledText`，前导空格在 ANSI 码之前，直接数即可得到屏幕上的起始列。

**教训**：涉及终端显示宽度的计算必须排除 ANSI 转义码。如果宽度计算函数不处理转义码，就不能用于含转义码的字符串。对于居中偏移这种简单场景，直接数前导空格比通用宽度计算更可靠。

## 5. PressEnterKey 会按 \n 拆分输入，不能传 "\n"

**场景**：鼠标点击在非按钮模式下派发 `DispatchInput("\n")` 推进游戏。

**结果**：点一下推进两次，相当于按了两次回车。

**原因**：`PressEnterKey` 中 `input.Split(["\\n", "\r\n", "\n", "\r"])` 把 `"\n"` 拆成 `["", ""]` 两个空字符串，循环执行两次 `RunEmueraProgram("")`，每次推进一个 AnyKey 状态。

**解决**：`DispatchInput("")` 传空字符串。空串不被 split，只产生一个元素，只推进一次。

**教训**：调用 `PressEnterKey`/`DispatchInput` 时，输入值会被宏解析系统 split 处理。`"\n"` 是宏分隔符，不能作为"回车"语义使用。AnyKey/EnterKey 等待下空字符串即可推进。

## 6. 鼠标事件 dwEventFlags 过滤不能忽略 DOUBLE_CLICK

**场景**：鼠标事件过滤条件 `dwEventFlags != 0`，只接受单击。

**结果**：双击的第二次点击 `dwEventFlags=DOUBLE_CLICK(0x0002)` 被忽略，快速点击时第二下被吞掉。

**原因**：conhost 对双击的第二次按下设置 `dwEventFlags=DOUBLE_CLICK` 而非 0，但该事件仍然是有效的左键按下，应该响应。

**解决**：过滤条件改为 `dwEventFlags != 0 && dwEventFlags != DOUBLE_CLICK`，允许单击和双击，只忽略移动/释放/滚轮。

**教训**：`dwEventFlags` 的值不只区分事件类型，还标记同一类型的不同交互方式（单击 vs 双击）。过滤时要明确哪些 flag 值需要保留，不能简单地 `!= 0` 一刀切。

## 7. CLI 渲染增强：ANSI 占位空格在样式段之外；单一宽度源头让联动修正自动一致（2026-08-07）

**场景**：CLI 渲染增强（T-026）——`FormatLineWithAnsi` 补下划线/删除线 ANSI 码，`BuildTerminalLine`/`FormatLineWithAnsi` 对图片/矩形按 `node.Width / charWidth` 补空格占位。

**教训一（测试断言）**：ANSI 行不是"样式段内含所有字符"的扁平模型——**无样式占位空格（图片/矩形补的空格）输出在样式段之外、转义码之前**，颜色/装饰转义码只在有 `ConsoleStyledString` 的段首出现。第一次断言写 `Assert.Contains(13空格 + "T", ansi)` 失败，因为 13 空格后跟的是 `\x1b[38;2;...mT`。正确断言是 `StartsWith(13空格)` + `EndsWith("T\x1b[0m")`。

**教训二（架构红利）**：`BuildTerminalLine` 是 CLI 宽度的单一源头（对齐 padding、`ComputeAlignOffset` 按钮命中区列偏移、`RebuildButtonPositions` 按钮列位置都经它取 textWidth）。给图片/矩形补占位后，这些消费者**自动同步修正**，零额外改动——之前"图片后按钮列偏左"的隐患随之消除。增强共享宽度计算时，消费者联动是红利而非负担，前提是它们必须复用同一函数而不是各算各的。

**教训三（复用先例）**：`ConsoleSpacePart` 已有 `Math.Max(node.Width / charWidth, 0)` 空格占位先例，图片/矩形直接复用同公式即可，不需要为视觉节点设计新机制。判断"能否增强"先搜同层已有模式的同类节点。

## 8. ERB FONTSTYLE 位契约与内部枚举值域相反——重构"魔法数字→枚举引用"翻转 4/8 语义（2026-08-07）

**场景**：PRINT_SLIDER 滑条（昼主導度 等）在 headless/前端"下划线缺失"，连发三个提交：v10 协议加 `underline?` 传输渲染、空格段改背景线、最后把 FONTSTYLE 解析"重构"成引用枚举值后"下划线终于出现"。

**根因链**：
1. **ERB 契约 ≠ .NET 枚举**。原版 Emuera（EvilMask/emuera.em `Process.ScriptProc.cs`）FONTSTYLE 位契约是 `位1=粗体、位2=斜体、位4=删除线、位8=下划线`；而仓库内部 `EmuFontStyle` 枚举按 System.Drawing.FontStyle（.NET）定义 `Underline=4、Strikeout=8`——**恰好相反**。原版代码用魔法数字 4/8 做边界转换（正确），本仓库旧代码照搬（仍正确）。
2. **提交 3 重构翻车**：把魔法数字替换成 `EmuFontStyle.Underline.Value`（=4）后，`value&4 → Underline`，恰好与原版语义（4=删除线）相反，setter 与 `GGETFONTSTYLE` getter（`GraphicsImage.cs`：Strikeout→4、Underline→8）不再互逆。
3. **协议字段与真实语义错位**：PRINT_SLIDER 实际用 `FONTSTYLE 4`（**删除线**），而 v10 协议只加了 `underline?`。提交 3 靠"把删除线伪装成下划线传输"让前端出现了横线——位置/粗细自然与 winforms 有差（删除线在字符 ~60% 高度，下划线在底部）。

**取证方法**：
- **原版源码对照**：直接拉 `gitlab.com/EvilMask/emuera.em` 的 ScriptProc.cs / GraphicsImage.cs / ConsoleStyledString.cs / Config.cs，逐行确认契约与渲染路径。
- **GDI 渲染实测（winforms）**：用户 config 是 `描画インターフェース:TEXTRENDERER`。写临时 C# 程序用 TextRenderer/GDI+ 渲染空格+Strikeout/Underline + 像素扫描，得到线位置：GDI+ 对**空格不画任何装饰线**，TextRenderer 才画（Strikeout 在 60%、Underline 在 93%）。
- **Chromium headless 截图**：Edge `--headless --screenshot` 渲染 11 种空格/文本装饰线场景，全部画线。

**解决**：`ParseFontStyle` 恢复 ERB 位字面量（4→Strikeout、8→Underline），协议 v11 加 `strikeout?` 字段（对称 underline），前端统一 `text-decoration: underline line-through`。

**教训**：
- **重构"魔法数字→枚举引用"前必须确认两套值域是否一致**。此例 ERB 契约与 .NET 枚举恰好相反，引用枚举 = 翻转语义。位契约转换应使用独立常量（如 `ERB_FONT_STRIKEOUT = 4`），不要引用值域可能相反的枚举。
- **协议字段必须对齐真实脚本语义，不能凭函数名/注释猜**。PRINT_SLIDER 名字里有"slider"、注释写"下划线空格"，实际脚本用的是删除线。先读 ERB 源码确认 `FONTSTYLE 4` 的契约，再定协议字段。
- **setter/getter 互逆是契约自洽的最低检验**：`FONTSTYLE 4; GGETFONTSTYLE` 往返应得 4。改 setter 不动 getter 是隐蔽回归源。
- **恒等式单元测试固化错误**：`InlineData(4,4)/(8,8)` 只验证"位直通"，测不出 4/8 翻转；正确断言应含跨值域映射 `(4,8)/(8,4)`。测试名宣称 "preserves the ERB bit values" 与实际行为相反。
- **像素级渲染结论必须实测，肉眼不可靠**：第一版深底渲染图肉眼误判"GDI+ 删除线画在底部"，像素扫描证明 GDI+ 对空格**根本不画**。视觉问题先做"参考实现怎么渲染"的实测（含渲染模式配置），再下结论。
- **hack 前先验证**：提交 2 的"空格段背景线"假设浏览器不为纯空格画 text-decoration，Chromium/WebView2 实测可靠（11 场景全画线），该 hack 不必要，已简化回 text-decoration。为旧版 WebView 假设写的兼容代码，要在当前内核上重新验证后再保留。
- **修复方向的正确性独立于"现象是否消失"**：提交 3 让横线"出现"了，但方向是错的（把删除线当下划线）。"最后一个提交才成功"是碰巧对症（脚本用 4、解析变 Underline），不是正确性的证明。
