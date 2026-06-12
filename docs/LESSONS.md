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
