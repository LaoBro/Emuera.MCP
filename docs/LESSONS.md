# 失败教训

## Windows 终端 ANSI 转义默认不可用

**场景**：按钮选择模式提示行用 `\x1b[2K\x1b[1G` 清除整行。

**结果**：转义序列被原样输出为 `[2K[1G`，未生效。

**原因**：Windows 控制台默认未启用 `ENABLE_VIRTUAL_TERMINAL_PROCESSING`，需 P/Invoke `SetConsoleMode` 才能支持 ANSI 转义。

**解决**：`Program.TrySetupWindowsConsole()` 在启动时通过 `SetConsoleMode` 启用虚拟终端处理，成功后设置 `Program.AnsiEnabled = true`。`AgentCliProtocol` 根据 `AnsiEnabled` 选择 ANSI 序列或 `SetCursorPosition` fallback。Linux/macOS 终端默认支持 ANSI，无需额外处理。

**教训**：在 Windows 终端中不要假设 ANSI 转义可用。必须先检测并启用虚拟终端支持，并保留 `SetCursorPosition` 作为 fallback。

## 用 Console.WindowWidth 个空格清除行会导致换行

**场景**：用 `\r` + `new string(' ', Console.WindowWidth)` + `\r` 清除提示行。

**结果**：每次按键提示行下移一行，终端输出逐行下推。

**原因**：写入 `WindowWidth` 个字符填满整行后，终端自动换行（光标移到下一行行首），第二个 `\r` 只能回到当前行（已在新行）。

**教训**：不要用填满整行的方式清除行。用 `SetCursorPosition` 定位到行首，只写实际需要的空格数覆盖旧内容，避免触及行尾触发自动换行。

## GetDisplayWidth 对非 CJK 宽字符计算不准

**场景**：按钮提示包含 `↑↓切换 Enter确认`，用 `GetDisplayWidth` 计算显示宽度后用等量空格覆盖。

**结果**：覆盖不干净，行尾残留 `认` 字符。

**原因**：`↑` (U+2191) 和 `↓` (U+2193) 不在 CJK 范围内，`GetDisplayWidth` 将其计算为宽度 1，但 Windows 终端实际显示为宽度 2。宽度偏差累积导致空格数不足。

**教训**：终端中需要精确计算显示宽度时，避免使用非 ASCII 宽字符（如箭头符号）。用纯 ASCII 替代（如 `Up/Dn`），或使用 ANSI `\x1b[2K` 清除整行绕过宽度计算。启用虚拟终端处理后，`\x1b[2K` 是最可靠的清行方式。

## Ambiguous Width 字符在终端中的宽度判定

**场景**：游戏地图使用 Box Drawing（━┃┏┗等）和 Block Elements（░▒▓█）字符，终端输出时严重对不齐，线条穿入右侧文字区域。

**结果**：地图完全变形，灰度字符区域宽度翻倍，线条越界。

**原因**：这些字符属于 Unicode "Ambiguous Width" 类别——在 CJK 终端（MS Gothic 字体）中渲染为全角（2列），在非 CJK 终端中渲染为半角（1列）。原 `IsWideChar` 只覆盖了 CJK 汉字和全角 ASCII 变体范围，完全遗漏了 Ambiguous 字符，导致宽度计算与终端渲染不一致。

**关键发现**：游戏脚本中 Block Elements（░▒▓）是按**半角**设计的——连续多个 `░` 用于填充海洋区域，每个占1个字符位。而 Box Drawing（━┃┏等）和 Geometric Shapes（●■等）在游戏中是按**全角**使用的。同属 Ambiguous Width 类别，但游戏对它们的宽度预期不同。

**解决**：
1. `IsWideChar` 中将 Box Drawing (U+2500-257F)、Geometric Shapes (U+25A0-25FF)、Miscellaneous Symbols (U+2600-26FF) 标记为全角（2列），与游戏设计一致。
2. Block Elements (U+2580-259F) 不标记为宽字符（半角1列），因为游戏按半角使用。
3. 输出时用 `ReplaceForTerminal` 将 ░▒▓█ 替换为盲文点阵字符（░→⠒ ▒→⠶ ▓→⠿ █→⣿），因为 ░▒▓█ 在 CJK 终端中强制全角渲染，无法通过 `IsWideChar` 标记改变其实际显示宽度，只能替换为半角字符。盲文点阵字符在 cmd/PowerShell 中渲染为半角且视觉效果接近原始灰度，优于 ASCII 字符（`.` `:` `#`）。注意 █ 不能替换为 ■，因为 ■ 也是全角字符，而游戏中 █ 按半角使用。
4. 宽度计算基于原始文本（与游戏内部一致），替换只在最终输出时进行。

**教训**：
- Unicode Ambiguous Width 字符在不同终端/字体下宽度不同，不能统一处理。
- 即使同属 Ambiguous 类别，游戏脚本对它们的宽度预期也可能不同（线条=全角，灰度=半角），需要逐类分析。
- 终端中字符的**实际渲染宽度**由字体决定，`IsWideChar` 只影响宽度计算，无法改变渲染结果。当计算宽度与渲染宽度不一致时，唯一可靠的方法是替换字符。
- `·` (U+00B7 Middle Dot) 在 MS Gothic 中是全角，不是可靠的半角字符。盲文点阵字符（U+2800-28FF，如 ⠒⠶⠿⣿）在 cmd/PowerShell 中渲染为半角且视觉效果好，是替换灰度字符的最佳选择。ASCII 字符（`.` `:` `#`）是兜底方案。
- 居中对齐应基于游戏配置宽度（`Config.DrawableWidth`），而非 `Console.WindowWidth`，因为游戏窗口宽度由配置文件决定。

## Ambiguous Width 字符在 cmd/PowerShell 中渲染为半角

**场景**：上述修复后，记事本中地图完美对齐，但 cmd 和 PowerShell 中 Box Drawing 字符（━┃┏┗等）仍然穿插错位。

**结果**：地图边框比预期短一半，文字位置偏移，线条穿入右侧区域。

**原因**：cmd/PowerShell 将 Box Drawing 字符渲染为**半角**（1列），而记事本（MS Gothic 字体）渲染为**全角**（2列）。之前的修复假设终端渲染为全角，`IsWideChar` 返回 true 但终端只占1列，宽度计算与渲染不一致。

**关键发现**：同为 Ambiguous Width 类别，不同字符在 cmd/PowerShell 中的渲染宽度不同：
- Box Drawing (U+2500-257F)：渲染为**半角**（1列）
- Geometric Shapes (U+25A0-25FF)：渲染为**全角**（2列），如 ●■◆▲
- Miscellaneous Symbols (U+2600-26FF)：渲染为**全角**（2列），如 ★☆

不能按整个 Ambiguous 类别统一处理，必须逐范围区分。

**解决**：
1. 启动时用 `DetectAmbiguousWidth()` 运行时检测终端实际渲染宽度——写入 `━` 字符后读取光标位移，delta=1 为半角，delta≥2 为全角。
2. `IsWideChar` 对 Ambiguous 字符**始终返回 true**（全角），保持宽度计算与游戏设计一致。
3. 当终端渲染为半角时，`ReplaceForTerminal` 在每个 Box Drawing 字符后插入半角空格（`━ `），使视觉宽度=字符1列+空格1列=2列，与游戏设计匹配。
4. Geometric Shapes 和 Misc Symbols 在 cmd/PowerShell 中已是全角，无需补空格。

**教训**：
- 同一 Unicode 类别（Ambiguous Width）中不同范围的字符，在同一终端中的渲染宽度也可能不同，必须逐范围验证。
- 宽度计算应始终与游戏设计保持一致（全角=2列），终端渲染差异通过输出时补空格来弥补，而非修改宽度计算逻辑。
- 运行时检测（光标位移法）是判断终端实际渲染宽度的可靠手段，比硬编码终端类型更健壮。
- 补空格策略：只对确认在当前终端渲染为半角的字符范围补空格，避免对已正确渲染为全角的字符造成多余间距。

## 字体检测与自动设置在新型终端中失效

**场景**：上述方案在 cmd/PowerShell（老式 conhost）中工作良好，但在 Windows Terminal（PowerShell 预设）和 Git Bash 中，字体检测恒返回"新宋体"，`★` 和 `●` 从全角变成了半角。

**结果**：无论终端实际设置什么字体，`GetCurrentConsoleFontEx` 返回的字体名都是"新宋体"（占位值），导致 `isCjkFont` 判断失效；`SetCurrentConsoleFontEx` 的调用被静默忽略，字体根本没变。Box Drawing/Geometric/Misc Symbols 三组的实际渲染宽度与检测不符，对齐再次错乱。

**原因**：三层 Windows API 假设在新型终端中全部不成立：

| API | conhost (cmd/旧PS) | Windows Terminal (conpty) | Git Bash (mintty/winpty) |
|---|---|---|---|
| `SetCurrentConsoleFontEx` | 生效 | 被忽略（WT 用自己的字体配置） | 无效 |
| `GetCurrentConsoleFontEx` | 返回真实字体名 | 恒返回占位值（"新宋体"） | 返回 winpty 的假值 |
| 光标位移探测（`CursorLeft` delta） | 准确 | **准确**（conpty 回传真实视觉宽度） | winpty 按 wcwidth 估算，不一定准 |

**关键发现**：在所有能交互的终端里，**光标位移探测法远比字体名检测可靠**。Windows Terminal 通过 conpty 把"终端实际让光标走了几格"如实回传给进程——字体名是假的，但位移是真的。因此应彻底抛弃字体名这条信息线，全面转向"按字符组独立探测实际渲染宽度"。

**解决**（重构为分组探测 + 数据驱动查表）：
1. 抛弃单一 `AmbiguousIsWide` 布尔，改为 4 个独立布尔：`BoxDrawingIsWide`、`GeometricIsWide`、`MiscSymbolsIsWide`、`BlockElementsIsWide`，默认全 false（非 CJK）。
2. `DetectCharWidths()` 对 4 个代表字符（`━ ● ★ █`）各做一次光标位移探测，独立判定每组的渲染宽度。
3. `ReplaceForTerminal` 改为纯数据驱动查表：

   | 组 | 游戏期望 | 实测全角 | 实测半角 |
   |---|---|---|---|
   | Box Drawing | 2 | 不处理 | 补空格 |
   | Geometric | 2 | 不处理 | 补空格 |
   | Misc Symbols | 2 | 不处理 | 补空格 |
   | Block Elements | 1 | 替换盲文 | 不处理 |

4. 删除 `TrySetConsoleFont()`（`SetCurrentConsoleFontEx`）和 `isCjkFont` 逻辑——前者只对 conhost 有效且造成跨终端效果不一致，后者依赖不可靠的字体名。
5. 新增 `PrintTerminalGuidance()`：基于探测结果打印精准提示（哪组半角、已自动补偿、建议换 CJK 字体）。
6. 探测不可用的环境（mintty 直连、Unix、输出重定向）→ 默认非 CJK + 提供 `--term-width-hint=cjk|latin|auto` CLI 开关覆盖。
7. 修正历史 bug：原 `IsWideChar` 遗漏了 Geometric (U+25A0–25FF) 和 Misc Symbols (U+2600–26FF)，导致 `★` `●` 在游戏内部宽度计算本就是错的（算成 1 列而非 2 列）。重构后三组统一归入全角。

**首要不变量**：**一切换行以游戏本身的逻辑为准。** `ReplaceForTerminal` 只做逐字符的替换/补空格，绝不插入换行符、改变 `IsLineEnd`、增减行数或重排折行。补空格/替换的目的是把"终端实际视觉宽度"**校准回**"游戏期望宽度"（由 `IsWideChar` 定义），从而使游戏内部的换行点在终端里精确呈现。校准前后，字符串的行结构、按游戏列计的长度完全一致。

**教训**：
- **跨平台优先**：新式终端（Windows Terminal、mintty、Linux 终端）才是日后的首选目标。不要为迁就老式 conhost 而依赖只有它支持的 API（字体设置/字体名读取）。
- **探测优先于检测**：字体名是属性，位移是行为。行为探测（光标位移法）在 conhost 和 Windows Terminal 中都可靠，是唯一可移植的判定手段。
- **自动设置字体是反模式**：自动设字体只在部分终端有效，会掩盖问题、造成"在我机器上正常"。改为"提前暴露问题 + 给出清晰提示"更健壮，也尊重用户对终端环境的控制权。
- **数据驱动查表优于条件分支**：把"字符组 × 游戏期望 × 实测宽度 → 处理方式"做成查表，新增字符组只改数据不改逻辑。
- **半角时统一补空格**：Box Drawing/Geometric/Misc Symbols 三组在终端渲染为半角时，一律在字符后补一个半角空格（字符1列+空格1列=2列）。测试中曾出现"手动设置等宽字体后渲染是全角但实际占位是半角导致字符重叠"的情况，补空格正好对齐。不用"复制字符"（会破坏连续线条对齐），也不用替换成 ASCII（视觉损失大）。

## Windows Terminal 中字体占位宽度与字形宽度解耦

**场景**：上述方案在 Windows Terminal 中实测，无论在 WT 设置中将字体改为 MS Gothic、Cascadia Mono、还是任何其他字体，光标位移探测结果始终为四组全半角：`BoxDrawing=half Geometric=half MiscSymbols=half BlockElements=half`。而在默认终端（conhost）中，Geometric 和 Misc Symbols 测得全角（`BoxDrawing=half Geometric=wide MiscSymbols=wide BlockElements=half`），与 MS Gothic 字体的实际行为一致。

**关键发现**：原版 Emuera 使用的 MS Gothic 字体的渲染行为是——Box Drawing 全角、Geometric 全角、Misc Symbols 全角、Block Elements 半角。游戏输出复制粘贴到记事本并设置 MS Gothic 字体后完美对齐，证实游戏布局就是基于这套宽度设计的。

但在 **Windows Terminal** 中，字体设置**不影响字符的占位宽度（cell allocation）**，只影响字形的绘制宽度（glyph width）。Ambiguous Width 字符在 WT 中始终按半角占位，即使选择了 MS Gothic 也不会变成全角占位。这两者的脱节会导致两类视觉问题：
- **占位半角 + 字形全角**（如 WT + MS Gothic 的 `█`）：字形溢出 cell 边界，与相邻字符重叠
- **占位 2 列（含补空格）+ 字形半角**（如 WT 的 `━ `）：补的空格列是空白的，连续线条中间出现明显空缺

因此改字体仍然有意义——目的是让**字体字形宽度**与**终端占位列数**匹配，而非改变占位列数本身。理想字体是各字符组的字形宽度恰好等于终端分配的 cell 数。

**教训**：
- **终端的字符占位宽度不由字体决定，由终端实现决定。** conhost 用字体属性决定占位宽度，Windows Terminal 用 conpty 固定的 wcwidth 逻辑决定占位宽度。同一个 MS Gothic 字体，在两个终端里对同一字符的占位列数不同。补空格/替换解决的是占位层的对齐，字体解决的是字形层的匹配——两者缺一不可。
- **"建议换字体"仍然正确，但理由要说清楚。** 不是因为换字体能改变占位宽度（在 WT 中不可能），而是因为字体字形宽度需要匹配终端的占位。提示应具体说明哪类问题对应哪种字体特征（字形全角 vs 字形半角），而非笼统说"换 CJK 字体"。
- **conhost 仍然是"字体=宽度"模型的唯一终端。** 只有在老式 conhost 中，`SetCurrentConsoleFontEx` 和字体选择才同时影响字形和占位宽度。这也是为什么弃用自动设字体是正确的——用只有 1/3 终端支持的 API 来解决通用问题，是根本方向性的错误。
- **实测数据比理论推演重要。** 理论上"MS Gothic 应该让 Geometric 全角"是正确的，但在 WT 中实测并非如此。光标位移探测的价值正在于此——它告诉你"这个终端实际做了什么"，而不是"这个字体应该做什么"。
