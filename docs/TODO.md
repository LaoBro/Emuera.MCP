## TODO 列表

> 维护规则：TODO 列表只保留未实现或仍需推进的目标；已完成目标应从本文件删除，避免把完成项继续当作待办。
>
> 当前目标筛选：最终目标按模式拆分——
>
> - CLI 模式：只实现文字相关 timer。
> - JSONL stdin 管道模式：忽视所有 timer，直接阻塞等待输入。
> - server 模式：未来应为前端提供足够数据以实现接近 WinForms 窗口的功能；本次暂不实现 server 相关 timer 功能。

### T-005：CLI 模式 DisplayTime 动态倒计时

- 状态：未实现
- 范围：`AgentCliProtocol.RunConsoleKeyLoop()`、终端 ANSI 转义输出
- 说明：TINPUT 带 `DisplayTime` 时，CLI 终端应动态更新倒计时文字（如"残り時間: 5.0"→"4.0"→…），而非只显示初始剩余时间。当前实现满足"至少不阻塞 timeout 语义"，但倒计时不递减。
- 纳入范围：
  - 在轮询中检测 `InputTimeoutMs` + `DisplayTime`，剩余秒数变化时更新终端倒计时行。
  - 用 ANSI 转义（`\x1b[A\r` 光标上移 + 覆盖写）替换最后一行，避免追加多行。
  - 超时时替换为 `TimeUpMes`。
- 不纳入范围：
  - WinForms `genericTimer` 驱动的精确 100ms 刷新；CLI 精度受轮询间隔（50ms）限制。
  - 终端宽度不足时的折行处理。
- 验收：
  - 带 `DisplayTime` 的 TINPUT 在 CLI 终端上显示递减的倒计时。
  - 倒计时行不重复追加，而是原地替换。
  - 超时后倒计时行替换为 `TimeUpMes`。
  - 倒计时刷新不干扰用户输入行。
- 关联：T-001（已完成的 timeout 核心逻辑）

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
