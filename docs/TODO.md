## TODO 列表

> 维护规则：TODO 列表只保留未实现或仍需推进的目标；已完成目标应从本文件删除，避免把完成项继续当作待办。
>
> 当前目标筛选：最终目标按模式拆分——
>
> - CLI 模式：只实现文字相关 timer。
> - JSONL stdin 管道模式：忽视所有 timer，直接阻塞等待输入。
> - server 模式：未来应为前端提供足够数据以实现接近 WinForms 窗口的功能；本次暂不实现 server 相关 timer 功能。

### T-001：CLI 模式支持文字相关 TINPUT / TWAIT timeout

- 状态：未实现
- 范围：`AgentCliProtocol`、`Program.RunHeadless()`、`EmueraConsole` HEADLESS timer 路径
- 说明：CLI 模式只处理会影响文字交互状态的 timer，不处理图形动画、FPS 刷新、剪贴板等 WinForms UI timer。
- 纳入范围：
  - `TINPUT` / `TINPUTS` / `TONEINPUT` / `TONEINPUTS` / `TWAIT` 的 `Timelimit` timeout。
  - timeout 后执行默认输入路径，显示 `TimeUpMes` 或剩余时间文案。
  - timeout 后继续输入不串轮。
- 不纳入范围：
  - `SETANIMETIMER` / `redrawTimer` 驱动的图形重绘动画。
  - `SpriteAnime` 动画帧切换。
  - `Config.FPS` / `_frameDeltaTimer` 的绘制节流。
  - `ClipboardProcessor.minTimer` 剪贴板防刷 timer。
  - `MainWindow.timerKeyMacroChanged` 宏组提示 UI timer。
- 验收：
  - CLI 非管道启动后可正常输入、退格、回车、清空。
  - TINPUT / TWAIT 超时后能执行默认输入路径。
  - 带 `DisplayTime` 的限时输入能更新文字倒计时，或至少不阻塞 timeout 语义。
  - 超时后继续输入不串轮。
- 关联规格：[SPEC_SINGLE_THREAD.md §7.3](./SPEC_SINGLE_THREAD.md#73-cli-模式)

### T-002：JSONL stdin 管道模式明确忽视所有 timer 并阻塞等待输入

- 状态：需保持现状并补充验证
- 范围：`ConsoleOutIO`、`Program.RunJsonlLoop()`
- 说明：JSONL stdin 管道模式不实现普通 stdin 的可靠 `ReadLine(timeoutMs)`；所有 timer 均不驱动游戏推进，主循环应继续阻塞读取输入。
- 当前期望行为：
  - `ConsoleOutIO.ReadLine(int timeoutMs)` 忽略 timeout，统一走 `Console.ReadLine()`。
  - `Program.RunJsonlLoop()` 使用 `ReadLine(-1)` 阻塞等待客户端输入。
  - 即使当前 `EmueraConsole.InputTimeoutMs` 有值，也不在 JSONL 管道模式中自动 `SubmitTimeout()`。
- 禁止行为：
  - 禁止使用 `Task.Run(() => Console.ReadLine())` 后 `Task.Wait(timeout)` 模拟 timeout，因为超时 task 可能吞掉后续输入。
  - 禁止在 JSONL 管道模式中引入后台 stdin reader 后丢弃已读输入。
- 验收：
  - JSONL 管道模式下 TINPUT timeout 不会自动推进。
  - 客户端不发送 input 时，进程保持等待输入。
  - 客户端发送 input 后，输入顺序不串轮。
- 关联规格：[SPEC_SINGLE_THREAD.md §5.3](./SPEC_SINGLE_THREAD.md#53-stdin-管道超时边界)、[SPEC_SINGLE_THREAD.md §7.1](./SPEC_SINGLE_THREAD.md#71-jsonl-管道模式)

### T-003：CLI 模式保留 WinmmTimer 作为时间源，但不引入 WinForms 绘制 timer

- 状态：未实现
- 范围：`WinmmTimer`、`EmueraConsole` HEADLESS timer 分支、`AgentCliProtocol`
- 说明：CLI 文字 timer 可以复用现有 `Stopwatch` / `WinmmTimer` 语义计算剩余时间，但不应启动 WinForms 的 `System.Timers.Timer` 或 `System.Windows.Forms.Timer` 来驱动绘制。
- 纳入范围：
  - 用单调时间源计算 TINPUT / TWAIT 剩余毫秒。
  - 在 CLI 主循环中周期性检查剩余时间并调用 `SubmitTimeout()`。
- 不纳入范围：
  - `redrawTimer` 刷新 `OnPaint()`。
  - `timerKeyMacroChanged` 控制 WinForms label 显示。
  - `Application.DoEvents()` 驱动的窗口消息循环。
- 验收：
  - CLI 模式下不依赖 WinForms 消息循环。
  - CLI 模式下不启动图形重绘 timer。
  - TINPUT / TWAIT timeout 与 WinForms 行为在文字结果上等价。

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
