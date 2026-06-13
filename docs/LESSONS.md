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
3. 输出时用 `ReplaceForTerminal` 将 ░▒▓█ 替换为 ASCII 半角等价字符（░→`.` ▒→`:` ▓→`#` █→`#`），因为 ░▒▓█ 在 CJK 终端中强制全角渲染，无法通过 `IsWideChar` 标记改变其实际显示宽度，只能替换为确定半角的字符。注意 █ 不能替换为 ■，因为 ■ 也是全角字符，而游戏中 █ 按半角使用。
4. 宽度计算基于原始文本（与游戏内部一致），替换只在最终输出时进行。

**教训**：
- Unicode Ambiguous Width 字符在不同终端/字体下宽度不同，不能统一处理。
- 即使同属 Ambiguous 类别，游戏脚本对它们的宽度预期也可能不同（线条=全角，灰度=半角），需要逐类分析。
- 终端中字符的**实际渲染宽度**由字体决定，`IsWideChar` 只影响宽度计算，无法改变渲染结果。当计算宽度与渲染宽度不一致时，唯一可靠的方法是替换字符。
- `·` (U+00B7 Middle Dot) 在 MS Gothic 中是全角，不是可靠的半角字符。ASCII 字符（`.` `:` `#`）才是确保半角的唯一选择。
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
