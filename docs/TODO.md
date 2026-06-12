## TODO 列表

> 维护规则：TODO 列表只保留未实现或仍需推进的目标；已完成目标应从本文件删除，避免把完成项继续当作待办。
>
> 当前目标筛选：最终目标按模式拆分——
>
> - CLI 模式：只实现文字相关 timer。
> - JSONL stdin 管道模式：忽视所有 timer，直接阻塞等待输入。
> - server 模式：未来应为前端提供足够数据以实现接近 WinForms 窗口的功能；本次暂不实现 server 相关 timer 功能。

### T-005：CLI 模式 DisplayTime 动态倒计时

- 状态：已实现
- 说明：TINPUT 带 `DisplayTime` 时，CLI 终端通过 `SetCursorPosition` 原地覆盖倒计时行，超时后替换为 `TimeUpMes`。

### T-006：CLI 模式 CLEARLINE 删行后终端同步

- 状态：已实现
- 说明：`deleteLine()` 中添加 HEADLESS 分支，先尝试从 `_agentBuffer` 移除（行仍在缓冲区时），缓冲区空则累加 `_pendingEraseRows`。`AgentCliProtocol.FlushBuffer()` 中调用 `EraseTerminalRows()` 用光标上移 + 空格覆盖擦除终端行。`REUSELASTLINE`（`PrintTemporaryLine`）依赖的 `deleteLine(1)` 同样受益。

### T-007：CLI 模式 REDRAW 输出抑制与强制刷新

- 状态：未实现
- 范围：`EmueraConsole.SetRedraw()`、`WriteAlignedLine()`、`AgentCliProtocol`
- 说明：`REDRAW 0` 抑制画面刷新，`REDRAW 2` 强制刷新。当前 CLI 模式完全忽略 REDRAW——`WriteAlignedLine()` 无条件写入 `_agentBuffer`，`FlushBuffer()` 无条件输出到终端。`REDRAW 0` 期间的中间输出也会立即显示。
- 纳入范围：
  - `WriteAlignedLine()` 中检查 `redraw == ConsoleRedraw.None`，跳过终端输出（仍写入 `displayLineList` 内存）。
  - `REDRAW 2` 时设置 `_needFullRefresh`，触发终端清屏 + 重绘 `displayLineList` 中累积的行。
- 不纳入范围：
  - `REDRAW 0` 期间 `SKIPDISP` 的交互（`SKIPDISP` 在 Process 层跳过整个指令，不进入 `addDisplayLine`，两者不冲突）。
- 验收：
  - `REDRAW 0` 期间终端不输出中间内容。
  - `REDRAW 2` 后终端一次性显示最终内容。
  - `REDRAW 1`（默认）行为不变。

### T-008：CLI 模式 SKIPDISP / NOSKIP 输出抑制

- 状态：未实现
- 范围：`Process.ScriptProc`、`WriteAlignedLine()`
- 说明：`SKIPDISP 1` 设置 `skipPrint = true`，Process 层跳过所有 `IsPrint()` 指令，不进入 `addDisplayLine`。`NOSKIP` 临时恢复输出，`ENDNOSKIP` 恢复抑制。当前 CLI 下 `SKIPDISP` 在 Process 层已正确跳过输出，但 `SKIPDISP 0` 恢复输出时不会触发终端刷新——如果 `SKIPDISP` 期间 `displayLineList` 被 `CLEARLINE` 等修改，终端不会同步。
- 纳入范围：
  - `SKIPDISP 0`（恢复输出）时检查 `displayLineList` 是否与终端一致，不一致则设置 `_needFullRefresh`。
  - 或更简单：`SKIPDISP` 状态变化时始终设置 `_needFullRefresh`。
- 不纳入范围：
  - `SKIPDISP` 核心逻辑（Process 层已正确实现）。
- 验收：
  - `SKIPDISP 1` 期间终端不输出。
  - `SKIPDISP 0` 恢复后终端显示与 `displayLineList` 一致。

### T-009：CLI 模式 changeLastLine 终端同步

- 状态：已实现
- 说明：`changeLastLine()` = `deleteLine(1)` + `PrintSingleLine()`。T-006 的 `EraseTerminalRows()` 在 `FlushBuffer()` 中先擦除终端旧行（光标回到旧行位置），再输出新行，实现原地替换。`WriteAlignedLine` 根据 `IsLineEnd` 控制换行，`RemoveLastLineFromAgentBuffer()` 能正确处理不换行行的移除。DisplayTime 倒计时场景由 T-005 单独处理。

### T-010：CLI 模式文字样式与颜色提示

- 状态：未实现
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
- 验收：
  - `SETCOLOR` 后的文字在终端上显示对应颜色。
  - `FONTBOLD` 后的文字在终端上显示粗体。
  - 样式重置后恢复正常显示。

### T-011：CLI 模式 PRINTBUTTON 按钮标记

- 状态：未实现
- 范围：`PRINTBUTTON`、`PRINTBUTTONC`、`PRINTBUTTONLC`
- 说明：WinForms 下 `PRINTBUTTON` 创建可点击按钮，按钮有标签和对应输入值。CLI 终端没有可点击按钮，但可以显示按钮标记帮助用户识别可选项。JSONL 协议已在 `CollectVisibleButtons()` 中采集按钮数据，CLI 模式目前只显示按钮文本，不区分按钮与普通文字。
- 纳入范围：
  - 在 `WriteAlignedLine()` 中识别按钮部分，用 `[label]` 或 `label(value)` 格式标记。
  - 或在输入提示行显示可用按钮列表。
- 不纳入范围：
  - 终端可点击按钮（不可能实现）。
  - `HTML_PRINT` 中的按钮（见 T-012）。
- 验收：
  - 用户能识别哪些文字是可点击按钮及其对应输入值。

### T-012：CLI 模式 HTML_PRINT 纯文本降级

- 状态：未实现
- 范围：`HTML_PRINT`、`HtmlManager`
- 说明：`HTML_PRINT` 解析 HTML 标签生成富文本行（含按钮、图片、对齐等）。当前 CLI 下 `HTML_PRINT` 的输出经过 `Html2DisplayLine()` 生成 `ConsoleDisplayLine`，`WriteAlignedLine()` 只取 `line.ToString()` 纯文本，HTML 标签效果丢失。部分标签（如 `<b>`、`<i>`、`<align>`）可以降级为 ANSI 转义或纯文本对齐；图片标签（`<img>`）无法降级。
- 纳入范围：
  - `WriteAlignedLine()` 已处理 `line.Align` 对齐，`<align>` 标签效果已保留。
  - `<b>` / `<i>` 标签可结合 T-010 的 ANSI 样式输出。
  - `<button>` 标签可结合 T-011 的按钮标记。
- 不纳入范围：
  - `<img>` / `<img src>` 图片标签（终端无法显示）。
  - `<shape>` / `<rect>` 图形标签。
- 验收：
  - `HTML_PRINT` 的文字内容正确显示。
  - 对齐、粗体、斜体等样式降级但不丢失语义。

### T-004：server timer 数据契约暂缓

- 状态：暂缓，不纳入本次实现
- 范围：`HttpGameServer`、`Session`、`AgentJsonlProtocol`
- 说明：server 模式未来需要为前端提供足够数据，以便前端实现接近 WinForms 窗口的功能，例如倒计时刷新、动画帧、按钮状态等；本次只聚焦 CLI 文字 timer 与 JSONL 阻塞策略。
- 当前不实现：
  - server 端图形动画 timer 数据。
  - `SETANIMETIMER` / `SpriteAnime` 前端同步。
  - server HTTP 层进一步事件驱动化。
- 后续再议：
  - 定义 turn 中 timer metadata。
  - 定义前端刷新间隔或动画帧数据。
  - 保持 server worker thread 与游戏步进边界清晰。
