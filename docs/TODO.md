# 单线程化改造 TODO

> 所属规格：[SPEC_SINGLE_THREAD.md](./SPEC_SINGLE_THREAD.md)  
> 维护规则：每次制定或修订新计划前，先更新本文件；新计划应吸收本文件中的未完成项，并在完成后回写状态。

---

## 当前状态

- 规格版本：v1.1
- 最后更新：2026-06-11
- 总体状态：核心实现已完成，WinForms 视觉回归待人工确认

---

## TODO 列表

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

### T-006：拆分 SPEC 与 PLAN 职责

- 状态：未实现
- 范围：`docs/single-thread/`
- 说明：当前 `SPEC_SINGLE_THREAD.md` 是规格书，`PLAN_SINGLE_THREAD.md` 是实施计划。后续计划修订时应保持二者职责清晰。
- 建议：
  - `SPEC_SINGLE_THREAD.md`：目标、边界、协议、状态机、验收标准。
  - `PLAN_SINGLE_THREAD.md`：阶段、步骤、文件改动、验证命令。
  - 本 `TODO.md`：跨计划维护的未完成项。
- 关联规格：[SPEC_SINGLE_THREAD.md §1](./SPEC_SINGLE_THREAD.md#1-修订摘要)

### T-007：清理 `_agentBufferLock`

- 状态：未实现
- 范围：`EmueraConsole.AgentBuffer.cs`、`AgentProtocolBase`
- 说明：单线程化后 `_agentBufferLock` 可能不再需要，但需要确认所有构建路径，尤其是非 HEADLESS 构建。
- 验收：
  - `dotnet build Emuera.Headless/Emuera.Headless.csproj -c Debug` 通过。
  - `dotnet build Emuera/Emuera.csproj -c Debug-NAudio` 通过。
  - WinForms smoke 通过。
- 关联规格：[SPEC_SINGLE_THREAD.md §15](./SPEC_SINGLE_THREAD.md#15-文件改动总览)

---

## 新计划更新记录

| 日期 | 计划/规格版本 | 更新内容 |
|------|---------------|----------|
| 2026-06-10 | SPEC v1.1 | 从 v1.1 规格中提取 TODO，建立本维护文件。 |
| 2026-06-10 | PLAN v1.2 | 修订实施计划：明确 WinForms UI 线程语义、HEADLESS `need_settimer`、server 单会话和路由加锁策略。 |
| 2026-06-10 | TODO v1.1 | 更新 T-004 / T-005：server 单会话与 TINPUT timeout 自动化测试已实现并验证通过。 |
| 2026-06-10 | TODO v1.1 | 删除 `tests/test_buttons.py`，新增 `tests/run_all.py` 统一入口；CLI 仅覆盖非管道启动 smoke。 |
| 2026-06-11 | TODO v1.2 | 删除已完成的 T-004 / T-005；保留 server HTTP 事件驱动化、CLI TINPUT timeout、`_agentBufferLock` 清理等未完成项。 |
