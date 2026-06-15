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
