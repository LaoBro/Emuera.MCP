## TODO 列表

> 维护规则：TODO 列表只保留未实现或仍需推进的目标；已完成目标应移动到 `docs/DONE.md`（补充实际实现方案与验收结论），避免把完成项继续当作待办.
>
> 当前目标筛选：最终目标按模式拆分——
>
> - CLI 模式：只实现文字相关 timer。
> - JSONL stdin 管道模式：忽视所有 timer，直接阻塞等待输入。
> - server 模式：未来应为前端提供足够数据以实现接近 WinForms 窗口的功能；本次暂不实现 server 相关 timer 功能。

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

### T-015：CLI CLEARLINE 擦除行避免整行空格触发换行

- 状态：未实现，需修复
- 范围：`Emuera.Headless/Agent/AgentCliProtocol.cs`
- 说明：`EraseTerminalRows()` 当前使用 `new string(' ', Console.WindowWidth)` 覆盖旧行。Windows 终端写入整行宽度空格可能触发自动换行，导致终端内容下移，和 `docs/LESSONS.md` 中记录的风险一致。
- 纳入范围：
  - ANSI 可用时优先使用 `\x1b[2K` 清除当前行。
  - fallback 使用 `SetCursorPosition` 定位后写入 `Math.Max(Console.WindowWidth - 1, 1)` 个空格。
  - 保持 `FlushBuffer()` 中先擦除旧行、再输出新内容的顺序。
- 验收：
  - `CLEARLINE` / `deleteLine()` 后终端不额外下移一行。
  - `changeLastLine()` 仍能原地替换最后一行。
  - ANSI 不可用的 Windows fallback 不残留旧文本。

### T-017：CLI 终端清行 fallback 边界防御

- 状态：未实现，需修复
- 范围：`Emuera.Headless/Agent/AgentCliProtocol.cs`
- 说明：`ClearButtonPrompt()` 的非 ANSI fallback 使用 `Console.WindowWidth - 1`，未处理窗口宽度为 0 或 1 的极端情况。`EraseTerminalRows()` 同样依赖终端宽度，应在 fallback 路径做最小宽度防御。
- 纳入范围：
  - 所有 `new string(' ', ...)` 的长度使用 `Math.Max(width, 1)` 或等价保护。
  - 对 `Console.WindowWidth` 抛异常的场景继续使用 80 作为 fallback。
- 验收：
  - 极小终端窗口下清除按钮提示行不抛异常。
  - ANSI 不可用时按钮提示行能被清空且光标回到原位置。

### T-020：CLI 鼠标悬浮高亮

- 状态：未实现（后续增强）
- 范围：`Emuera.Headless/Agent/AgentCliProtocol.cs`
- 说明：开启 XTerm 1002（按钮事件追踪）或 1003（任意事件追踪），接收鼠标移动事件。当鼠标悬浮在按钮区域上时，用 ANSI 样式（反色/下划线）高亮该按钮。鼠标离开时恢复。
- 前置：T-019。

### T-021：RunEmueraProgram 脚本死循环保护

- 状态：未实现（中期，依赖 I-01）
- 范围：`Emuera/Runtime/Script/Process.cs`、`Emuera/Runtime/Script/Process.ScriptProc.cs`
- 说明：`Process.DoScript()` 内部指令循环若遇到 ERB 死循环（如 `WHILE 1 \n WEND` 或无限递归），永不返回。所有调用 `RunEmueraProgram` 的路径（JSONL `StepAsync`/`SubmitTimeoutAsync`、CLI `HandleTimeout`/`ProcessChar`/`DispatchMouseClick`、Server `Session.GameLoopAsync`、WinForms `MainWindow`）都会卡死。
  - JSONL 修复（I-08）的 `WaitForInputAsync` 30s 超时实际救不了死循环——`SubmitTimeoutAsync` 第一行 `console.SubmitTimeout()` 同步调 `RunEmueraProgram` 卡住，根本到不了 `await`。
  - CLI 模式下主线程卡死，Ctrl+C 也无法响应（VT raw mode 下走 0x03 检测，主线程不读键盘）。
  - Server 模式下工作线程被卡，长轮询 25s 后客户端拿 204，再发 input 永远拿不到 turn，session 形同僵尸；HTTP server 主线程还在跑，但 `_session` 单字段会 409 阻塞新会话。
- 不纳入范围（短期）：
  - 在共享源码 glob（I-01 未完成）状态下，`Process` 改动会同时影响 WinForms 主项目，风险高于收益。
  - 隐患 B 是低频灾难场景，没有用户报告。
- 中期目标（依赖 I-01）：
  - 抽取 `Emuera.Core` 类库后，给 `Process.DoScript` / `Process.ScriptProc` 加 `CancellationToken` 参数。
  - 指令循环每 N 条指令检查一次 `ct.IsCancellationRequested`，超时抛 `ScriptTimeoutException`。
  - WinForms 模式下 token 由 `MainWindow` 的关闭事件触发；Headless 模式下由 `AgentProtocolBase.StopToken` 注入。
  - 阈值建议：单次 `RunEmueraProgram` 默认 30s 可取消，可通过 `--script-timeout` CLI 选项覆盖。
- 验收：
  - 故意构造 `WHILE 1 \n WEND` ERB 脚本，CLI/JSONL/Server 模式都能在阈值时间内退出并给出错误信息。
  - 正常长脚本不误触上限。
- 参考：评估报告 I-05/I-06（async 化 Session 与长轮询）。
