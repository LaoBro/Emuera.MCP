# DONE.md

## 已完成目标

### T-005：CLI 模式 DisplayTime 动态倒计时

- 状态：已实现
- 说明：TINPUT 带 `DisplayTime` 时，CLI 终端通过 `SetCursorPosition` 原地覆盖倒计时行，超时后替换为 `TimeUpMes`。

### T-006：CLI 模式 CLEARLINE 删行后终端同步

- 状态：已实现
- 说明：`deleteLine()` 中添加 HEADLESS 分支，先尝试从 `_agentBuffer` 移除（行仍在缓冲区时），缓冲区空则累加 `_pendingEraseRows`。`AgentCliProtocol.FlushBuffer()` 中调用 `EraseTerminalRows()` 用光标上移 + 空格覆盖擦除终端行。`REUSELASTLINE`（`PrintTemporaryLine`）依赖的 `deleteLine(1)` 同样受益。

### T-009：CLI 模式 changeLastLine 终端同步

- 状态：已实现
- 说明：`changeLastLine()` = `deleteLine(1)` + `PrintSingleLine()`。T-006 的 `EraseTerminalRows()` 在 `FlushBuffer()` 中先擦除终端旧行（光标回到旧行位置），再输出新行，实现原地替换。`WriteAlignedLine` 根据 `IsLineEnd` 控制换行，`RemoveLastLineFromAgentBuffer()` 能正确处理不换行行的移除。DisplayTime 倒计时场景由 T-005 单独处理。

### T-010：CLI 模式文字样式与颜色提示

- 状态：已实现
- 范围：`SETCOLOR`、`RESETCOLOR`、`FONTBOLD`、`FONTITALIC`、`FONTREGULAR`、`FONTSTYLE`、`SETFONT`
- 说明：WinForms 下这些指令改变文字颜色、粗体、斜体、字体。终端不支持富文本样式，但可以用 ANSI 转义序列模拟部分效果：
  - `SETCOLOR` → ANSI 256色/真彩色转义 `\x1b[38;5;Nm` 或 `\x1b[38;2;R;G;Bm`
  - `FONTBOLD` → ANSI `\x1b[1m`
  - `FONTITALIC` → ANSI `\x1b[3m`
  - `RESETCOLOR` / `FONTREGULAR` → ANSI `\x1b[0m`
  - `SETBGCOLOR` → ANSI `\x1b[48;2;R;G;Bm`
- 纳入范围：
  - 在 `WriteAlignedLine()` 中根据当前 `StringStyle` 的颜色/字体信息输出 ANSI 转义。
  - 每行结束后重置样式，避免影响后续行。
- 不纳入范围：
  - `SETFONT` 改变字体族（终端不支持）。
  - `SETBGIMAGE` / `CLEARBGIMAGE`（终端不支持背景图）。
  - `SETBGCOLOR` 背景色（会覆盖用户终端配色方案，且为背景色全局状态追踪复杂度高）。
- 实现说明：
  - 新增 `FormatLineWithAnsi()` 方法，逐段遍历 `ConsoleDisplayLine` 中的 `ConsoleStyledString`，提取 `StringStyle.Color`（真彩色 `\x1b[38;2;R;G;Bm`）和 `StringStyle.FontStyle`（粗体 `\x1b[1m`、斜体 `\x1b[3m`），仅在样式变化时输出转义码。
  - `WriteAlignedLine()` 和 `FormatLineForTerminal()` 均调用 `FormatLineWithAnsi()`，保证增量输出与全量刷新一致。
  - 宽度计算与对齐仍基于纯文本（ANSI 转义不占显示宽度）。
  - 受 `IsAnsiEnabled()`（`Program.AnsiEnabled || !OperatingSystem.IsWindows()`）控制，终端不支持时回退到纯文本。
- 验收：
  - `SETCOLOR` 后的文字在终端上显示对应颜色。
  - `FONTBOLD` 后的文字在终端上显示粗体。
  - 样式重置后恢复正常显示。

### T-011：CLI 模式 PRINTBUTTON 按钮选择模式

- 状态：已实现
- 说明：CLI 终端在 `WaitInput` 状态下按 `↑` 键进入按钮选择模式，提示行显示 `> [按钮]  ↑↓切换 Enter确认`，`↑↓` 循环切换按钮，`Enter` 确认提交按钮值，`Esc` 退出选择模式。`CollectCurrentButtons()` 采集 `Generation == LastButtonGeneration` 的有效按钮，排除过期按钮。

### T-013：CLI 按钮选择模式跨轮次状态同步

- 状态：已实现
- 说明：提取 `SyncButtonState()` 统一管理按钮模式的进入/退出/刷新，在 `RunConsoleKeyLoop` 的三个关键点（FullRefresh 后、超时后、每轮 `FlushBuffer()` 后）调用。`ConfirmButton()` 和 Timeout 路径不再手动清除按钮状态，全部由 `SyncButtonState()` 统一处理。新增 `ButtonListEquals()` 辅助方法检测按钮列表是否变化（按 Generation + Input 值比较），新增 `ClearInputBuffer()` 提取重复的输入缓冲区清除逻辑。

### T-012：CLI 模式 HTML_PRINT 纯文本降级

- 状态：已实现
- 范围：`HTML_PRINT`、`HtmlManager`、`EmueraConsole.AgentBridge`
- 说明：`HTML_PRINT` 解析 HTML 标签生成富文本行（含按钮、图片、对齐等）。CLI 下 `WriteAlignedLine()` 原先调用 `line.ToString()` 获取纯文本，非文本节点（`ConsoleImagePart`、`ConsoleSpacePart`、`ConsoleRectangleShapePart`、`ConsoleDivPart`）的 `ToString()` 返回完整 HTML 标签，导致文本过长、排版错位、自动换行后按钮失灵。
- 实现方案：新增 `BuildTerminalLine()` 方法逐节点构建终端友好文本，降级规则：`ConsoleStyledString`→原样文本；`ConsoleSpacePart`→像素宽度转空格数；`ConsoleImagePart`/`ConsoleRectangleShapePart`→跳过；`ConsoleDivPart`→递归子行。
- 验收：`HTML_PRINT` 文字内容正确显示，对齐/粗体/斜体样式降级但不丢失语义，非文本节点不再输出 HTML 标签文本。

### T-018：HEADLESS AgentBuffer 删除最后一行容错

- 状态：已实现
- 范围：`EmueraHeadless/UI/Game/EmueraConsole.AgentBuffer.cs`
- 说明：`deleteLine()` 调用 `RemoveLastLineFromAgentBuffer()` 时，若 `_agentBuffer` 内容为空或只有 1 个字符，`LastIndexOf` 可能因负数参数抛异常。
- 实现方案：在 `LastIndexOf` 调用前增加 `content.Length <= 1` 的前置判断，直接清空缓冲区并递减计数。
- 验收：缓冲区只有不换行单行时调用 `deleteLine()` 不抛异常，多行缓冲区删除最后一行后前序内容保留正确。

### T-014：CLI 模式游戏结束后主动退出

- 状态：已实现
- 范围：`Emuera.Headless/Agent/AgentCliProtocol.cs`
- 说明：`AgentCliProtocol.RunAgentLoop` 的 `while` 条件只看 `token.IsCancellationRequested`，从不看 `console.State`。游戏脚本同步执行完毕后 `console.State` 已被置为 `Quit` 或 `Error`，但主循环仍会继续 `Poll` 等待永远不会到来的输入：ConsoleKey 路径每轮 `WaitOne(50ms)` 忙等，VT 路径同样忙等，pipe 路径靠 stdin EOF 退出所以测试不暴露此 bug。
- 实现方案：在 `RunAgentLoop` 的 `while` 条件追加 `&& !IsGameExited()`，新增私有 helper `IsGameExited()` 判定 `console.State is ConsoleState.Quit or ConsoleState.Error`。检查放在循环顶部而非末尾 `FlushBuffer` 后，避免游戏结束后再做一次无意义的 `Poll`，并顺带覆盖 `Initialize` 失败（state 已是 Error）的首轮退出场景。未调用 `Stop()`，让循环条件自然失败，保留 `Stop()` 给 VT Ctrl+C 和 pipe EOF 路径专用。
- 验收：
  - 交互 CLI 下选择退出按钮后进程自然结束。
  - 游戏错误状态下 CLI 不继续等待输入。
  - pipe CLI 现有行为保持不变（`run_all.py` 4 套件全绿：JSONL+buttons 30/30、CLI 8/8、server single-session 24/24、TINPUT timeout 14/14）。

### T-021：RunEmueraProgram 脚本死循环保护

- 状态：已实现
- 范围：`Emuera/Runtime/Script/Process.cs`、`Emuera/Runtime/Script/Process.ScriptProc.cs`
- 说明：`Process.DoScript()` 内部指令循环遇到 ERB 死循环（如 `WHILE 1 \n WEND`）永不返回，所有调用 `RunEmueraProgram` 的路径卡死。原 `checkInfiniteLoop()` 依赖 `state.lineCount % 10000 == 0` 触发，但 `JumpTo()` 额外递增 `lineCount` 导致该条件永不成立；且 Headless 模式下 `HeadlessDialog.ShowPrompt()` 始终返回 false（auto-select: No），脚本无限继续。
- 实现方案：
  - `runScriptProc()` 中新增独立迭代计数器 `loopIterCount` 替代 `state.lineCount % 10000`，每 10000 次迭代调用 `checkInfiniteLoop()`
  - `checkInfiniteLoop()` 增加 `#if HEADLESS` 分支：超时后输出 `[script-timeout]` 日志到 stderr，抛出 `GameExitException` 终止脚本
  - `GameExitException` 沿已有传播链穿透至 `Session.GameLoopAsync`/`HeadlessRunner.RunAsync` 的 catch + finally，触发正常清理（BuildFinalTurn / IO.Close / GlobalStatic.Reset）
  - WinForms 路径保持原有 `Dialog.ShowPrompt()` 交互行为不变
- 超时阈值：`Config.InfiniteLoopAlertTime`（默认 5000ms），可通过 emuera.config 配置，设为 0 禁用
- 验收：
  - `WHILE 1 \n WEND` ERB 脚本在 Server 模式 ~5.6s 内终止，stderr 输出 `[script-timeout]` 日志
  - 全部 94 项回归测试通过（JSONL/CLI/Server/TINPUT/I-11）
  - WinForms 构建不受影响（`#if HEADLESS` 条件编译隔离）
