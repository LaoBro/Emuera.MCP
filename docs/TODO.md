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

### T-022：I-12 阶段 2 — Nullable enable + TreatWarningsAsErrors

- 状态：未实现（中期，依赖 I-01）
- 范围：`Emuera.Headless/Emuera.Headless.csproj`、仓库根 `.editorconfig`
- 说明：I-12 阶段 1（2026-07-01）已落地按路径分级的质量护栏：
  - `Emuera.Headless.csproj` 启用 `<EnableNETAnalyzers>true</EnableNETAnalyzers>` + `<AnalysisMode>Minimum</AnalysisMode>` + `<EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>`
  - 仓库根 `.editorconfig` 对 `[Emuera/**]` 路径下的历史共享源码抑制 22 个 CA 规则与 CS8981/CS0472/CS0649/CS0162/CS0164 等编译器警告，避免 Headless 构建被历史代码噪音淹没
  - `Emuera.Headless/**` 自有源码警告保持可见（当前基线 ~140 条，主要是 CS8632 nullable 注解、CA1822 static 成员、CA1416 平台兼容性）
- 中期目标（依赖 I-01 抽 `Emuera.Core` 类库）：
  - `Emuera.Core` 落地后，`Emuera.Headless.csproj` 不再通过 csproj glob 共享 `Emuera/` 源码，分析器只扫描 Headless 自有源码
  - 此时撤销根 `.editorconfig` 中 `[Emuera/**]` 段的所有 `dotnet_diagnostic.CAxxxx.severity = none`，让 CA 规则回归到 `Emuera.Core` 自己的责任范围
  - 把 `Emuera.Headless.csproj` 的 `<Nullable>` 从 `disable` 改为 `enable`，清零 CS8632/CS8600/CS8602 等 nullable 警告
  - 启用 `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>` + `<WarningsAsErrors>nullable</WarningsAsErrors>`，让 Headless 构建在引入新 warning 时直接失败
- 不纳入范围（短期）：
  - 在 I-01 完成前不启用 `TreatWarningsAsErrors`——会因 `Emuera/` 共享源码警告导致 Headless 构建失败，且修复会污染 WinForms 路径
  - 不在 `Emuera/**` 段加 `<TreatWarningsAsErrors>`，避免影响 Emuera 主项目构建
- 验收：
  - `dotnet build Emuera.Headless/Emuera.Headless.csproj -c Debug` 0 warning 0 error
  - 故意在 Headless 自有源码引入 `string? x = null;`（未启用 nullable 上下文）等典型问题，构建应失败
  - 根 `.editorconfig` 不再包含 `[Emuera/**]` 段的 CA 抑制
- 参考：
  - 复评报告 [I-12 — csproj 质量护栏缺失](2026.6.30.架构健壮性重构/Emuera.Headless%20架构健壮性复评报告.md)
  - 评估报告 [4.4 性能与发布配置（I-12, I-13）](2026.6.30.架构健壮性重构/Emuera.Headless%20架构健壮性评估报告.md)
