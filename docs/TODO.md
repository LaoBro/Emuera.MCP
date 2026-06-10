## TODO 列表

> 维护规则：TODO 列表只保留未实现或仍需推进的目标；已完成目标应从本文件删除，避免把完成项继续当作待办。

### T-001：普通 stdin 管道模式支持可靠 `ReadLine(timeoutMs)`

- 状态：未实现
- 范围：`ConsoleOutIO`
- 说明：v1.1 明确不支持普通 stdin 管道的可靠 timeout。禁止使用 `Task.Run(() => Console.ReadLine())` 后 `Task.Wait(timeout)` 的实现，因为超时 task 可能吞掉后续输入。
- 后续方向：
  - Windows native `PeekNamedPipe` / `CancelSynchronousIo`；
  - 或可取消的 `ReadAsync` 包装；
  - 或单独设计 stdin 非阻塞读取层。
- 关联规格：[SPEC_SINGLE_THREAD.md §5.3](./SPEC_SINGLE_THREAD.md#53-stdin-管道超时边界)

### T-002：CLI 模式支持 TINPUT timeout

- 状态：未实现
- 范围：`AgentCliProtocol`、`Program.RunHeadless()`
- 说明：v1.1 只保证 CLI 交互路径同步化，不处理 TINPUT timeout。
- 验收：
  - CLI 非管道启动后可正常输入、退格、回车、清空。
  - TINPUT 超时后能执行默认输入路径。
  - 超时后继续输入不串轮。
- 关联规格：[SPEC_SINGLE_THREAD.md §7.3](./SPEC_SINGLE_THREAD.md#73-cli-模式)

### T-003：server HTTP 层进一步事件驱动化

- 状态：未实现
- 范围：`HttpGameServer`、`Session`
- 说明：v1.1 只要求 server 单会话，不要求 HTTP 层单线程；当前仍允许 worker thread / `Task.Run` 请求处理。
- 后续方向：
  - 减少 session worker thread；
  - 将游戏推进、输入 enqueue、turn dequeue 改为事件驱动；
  - 明确 async 边界，避免阻塞 HTTP 长轮询。
- 关联规格：[SPEC_SINGLE_THREAD.md §5.2](./SPEC_SINGLE_THREAD.md#52-server-模式边界)

