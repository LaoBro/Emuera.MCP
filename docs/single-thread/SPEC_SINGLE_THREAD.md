# Emuera.Headless 单线程化改造规格书 v1.1

> 版本：v1.1  
> 日期：2026-06-10  
> 配套实施计划：[PLAN_SINGLE_THREAD.md](./PLAN_SINGLE_THREAD.md)  
> 修订依据：v1.0 审核结论 + 技术范围确认

---

## 1. 修订摘要

v1.1 对 v1.0 做以下关键修订：

1. **明确单线程化范围**：本规格优先覆盖 `--headless` JSONL/CLI 模式；server 模式在 v1.1 只做**单会话化**，不承诺单线程化。
2. **明确 stdin 管道超时范围**：普通 stdin 管道的 `ReadLine(timeoutMs)` 在 v1.1 **不承诺支持**，但保留接口和后续 TODO；TINPUT 超时优先覆盖 `HttpSessionIO` / server 模式。
3. **修正 TINPUT 超时语义**：超时不能简单等价于 `PressEnterKey("")`，必须尽量接近原 WinForms timer 行为，包括默认输入、`TimeUpMes`、`isTimeout` 状态。
4. **降低 CLI 范围**：CLI 模式只保证交互路径从后台线程改为同步驱动；TINPUT timeout 不进入 v1.1 范围。
5. **补充验收标准**：新增 timeout、server 单会话、CLI smoke、WinForms 回归等验收项。

---

## 2. 目标

### 2.1 主要目标

- 将 `--headless` JSONL 管道模式的 agent 协议从“后台线程驱动”改为“调用方同步步进驱动”。
- 保留现有 JSONL 协议对外行为：初始 turn 自动输出；每个 `{"type":"input","value":"..."}` 输入后输出下一个 turn。
- 支持 server 模式下 TINPUT 超时，超时行为尽量接近现有 WinForms timer。
- 将 HTTP server 从多会话改为单会话，避免同一进程同时运行多个游戏实例。
- 保持 WinForms 非 headless 行为不变。

### 2.2 非目标

v1.1 不覆盖以下内容：

- WebSocket 支持。
- 多会话 server。
- 普通 stdin 管道模式的可靠 `ReadLine(timeoutMs)`。
- CLI 模式的 TINPUT timeout。
- Runtime 层核心脚本语义修改。
- WinForms UI 行为变更。
- 跨平台 native stdin 非阻塞读取。

---

## 3. 术语

| 术语 | 含义 |
|------|------|
| JSONL 管道模式 | `stdin` 被重定向，`Emuera.Headless` 通过 stdout 输出 JSON turn，通过 stdin 接收 JSON 输入。 |
| CLI 模式 | 普通终端交互模式，不使用 JSONL 协议。 |
| server 模式 | `--server` 启动 HTTP 服务，通过 `/sessions` API 长轮询 turn 和提交输入。 |
| Step | 同步执行“提交输入 → 游戏推进 → 返回下一 turn JSON”的最小单位。 |
| TINPUT | 带时限的输入请求，由 `inputReq.Timelimit > 0` 表示。 |
| `HttpSessionIO` | server 模式使用的内存队列 IO，支持 `ReadLine(timeoutMs)`。 |
| `ConsoleOutIO` | 普通 stdin/stdout 管道模式使用的 IO。 |

---

## 4. v1.1 范围矩阵

| 模式 | 单线程同步步进 | TINPUT timeout | 单会话 server | 说明 |
|------|----------------|----------------|---------------|------|
| `--headless` JSONL 管道 | 是 | 否，v1.1 TODO | 不适用 | 保留现有协议行为；`ConsoleOutIO.ReadLine(timeoutMs)` 不承诺可用。 |
| CLI 终端交互 | 是 | 否 | 不适用 | 只保证交互路径不再后台线程驱动。 |
| `--server` HTTP | 否，允许 worker thread | 是 | 是 | v1.1 只做单会话化，不要求 HTTP 层单线程。 |
| WinForms | 不变 | 不变 | 不适用 | 非 `HEADLESS` 构建不改变行为。 |

---

## 5. 架构约束

### 5.1 单线程步进边界

v1.1 中，JSONL 管道模式的协议层不再自行创建 agent 线程。调用方负责：

1. 初始化 `EmueraConsole`。
2. 等待游戏进入 `WaitInput` / `Quit` / `Error`。
3. 输出初始 turn。
4. 从 IO 读取输入。
5. 调用 `AgentProtocolBase.Step(input)`。
6. 输出返回的 turn。

### 5.2 server 模式边界

server 模式 v1.1 允许保留 session worker thread，但必须满足：

- 同一时间最多存在一个 `Session`。
- 第二个 `POST /sessions` 必须返回 `409 Conflict`。
- 旧会话未销毁前，不允许创建新会话。
- HTTP 请求处理仍可使用 `Task.Run` / worker thread，不作为 v1.1 单线程化验收项。

### 5.3 stdin 管道超时边界

v1.1 不要求 `ConsoleOutIO.ReadLine(timeoutMs)` 可靠支持超时。

允许行为：

- `timeoutMs < 0`：无限等待，等价于 `ReadLine()`。
- `timeoutMs > 0`：可以阻塞等待，也可以返回 `null`；但不得破坏后续输入顺序。
- `timeoutMs == 0`：可以阻塞等待；不得承诺非阻塞。

禁止行为：

- 使用 `Task.Run(() => Console.ReadLine())` 后通过 `Task.Wait(timeout)` 模拟超时，因为超时 task 可能吞掉后续输入。
- 在超时后继续复用已被后台 task 消费的 stdin 状态。

后续 TODO：如必须支持 stdin 管道 TINPUT timeout，应单独设计 native non-blocking / cancellable stdin 读取方案。

---

## 6. 协议规格

### 6.1 JSONL turn 输出

每次 turn 输出必须是一行 JSON，字段保持兼容：

```json
{
  "text": "当前可见文本",
  "state": "WaitInput",
  "inputType": "EnterKey",
  "needValue": false,
  "buttons": [
    { "label": "[0] Hello", "value": 0 }
  ]
}
```

字段说明：

| 字段 | 类型 | 说明 |
|------|------|------|
| `text` | string | 当前可见文本。 |
| `state` | string | `ConsoleState.ToString()`。 |
| `inputType` | string? | 当前 `InputRequest.InputType.ToString()`。 |
| `needValue` | bool | 当前请求是否需要值。 |
| `buttons` | array | 可见按钮列表，每项包含 `label` 和 `value`。 |

### 6.2 JSONL input

客户端输入仍为：

```json
{ "type": "input", "value": "0" }
```

约束：

- `type != "input"` 时忽略。
- `value` 缺失时按空字符串处理。
- 非法 JSON 忽略，不输出错误。
- 输入处理异常时输出 error turn：

```json
{ "error": "异常消息", "state": "Error" }
```

error turn 必须使用 `JsonSerializer.Serialize(...)`，禁止字符串拼接未转义 JSON。

---

## 7. 核心状态机

### 7.1 JSONL 管道模式

```text
Start
  ↓
Initialize EmueraConsole
  ↓
WaitForInitialState()
  ├─ WaitInput → BuildTurn() → Output initial turn
  ├─ Quit/Error → BuildTurn() → Stop
  └─ Timeout → Stop without output

Loop while !IsStopped:
  ↓
ReadLine()
  ├─ EOF → Stop
  ├─ invalid JSON → continue
  ├─ non-input command → continue
  └─ input command → Step(value ?? "")
        ↓
      BuildTurn()
        ├─ Quit/Error → Stop after output
        └─ WaitInput/Running/Sleep → Output turn and continue
```

v1.1 中 JSONL 管道模式不处理 TINPUT timeout；即使当前存在 `InputTimeoutMs`，普通 stdin 管道仍使用普通 `ReadLine()`。

### 7.2 server 模式 TINPUT timeout

```text
Session started
  ↓
Initialize console
  ↓
GetInitialTurn()
  ↓
Output initial turn
  ↓
Loop while session active:
  ↓
timeoutMs = console.InputTimeoutMs
  ├─ null / <= 0 → ReadLine()
  └─ > 0 → ReadLine(timeoutMs)

ReadLine result:
  ├─ null && !IsConnected → Stop
  ├─ null && IsConnected && InputTimeoutMs exists → SubmitTimeout()
  ├─ invalid JSON → continue
  └─ input command → Step(value ?? "")

SubmitTimeout():
  ↓
尽量等价于原 timer timeout:
  - 停止当前 timer
  - 设置 isTimeout = true
  - 显示 TimeUpMes（如存在）
  - 以空输入执行默认输入路径
  - RefreshStrings
  - BuildTurn()
```

### 7.3 CLI 模式

CLI 模式 v1.1 只做同步化：

- 不再由 `AgentCliProtocol.Run()` 创建后台线程。
- `Program.RunHeadless()` 在非管道模式下调用同步 `RunCliLoop()`。
- `RunCliLoop()` 继续处理 `Console.ReadKey(true)`、退格、回车、输入缓冲。
- TINPUT timeout 不进入 v1.1。

---

## 8. API 设计

### 8.1 `SessionIO`

`Emuera/Server/SessionIO.cs`

新增方法：

```csharp
public abstract string? ReadLine(int timeoutMs);
```

语义：

| `timeoutMs` | 语义 |
|-------------|------|
| `< 0` | 无限等待，等价于 `ReadLine()`。 |
| `= 0` | 立即尝试读取；如果实现无法非阻塞，可阻塞。 |
| `> 0` | 等待指定毫秒；具体是否支持取决于实现。 |

实现要求：

- `HttpSessionIO` 必须支持可靠超时。
- `ConsoleOutIO` v1.1 不承诺可靠超时；禁止使用会吞后续输入的后台 `Console.ReadLine()` task。

### 8.2 `AgentProtocolBase`

`Emuera/UI/Game/AgentProtocolBase.cs`

v1.1 建议接口：

```csharp
internal abstract class AgentProtocolBase
{
    private volatile bool _stopped;

    protected readonly EmueraConsole console;
    protected readonly IConsoleUI ui;

    protected const int TurnTimeoutMs = 30000;
    protected const int PollIntervalMs = 50;

    internal AgentProtocolBase(EmueraConsole console, IConsoleUI ui);

    internal bool IsStopped => _stopped;

    internal abstract string? GetInitialTurn();
    internal abstract string? Step(string input);

    public virtual void Stop() => _stopped = true;

    public static AgentProtocolBase? Detect(EmueraConsole console, IConsoleUI ui);
}
```

说明：

- `Step(string input)` 使用非 nullable `string`，避免调用方误把 `null` 当作正常输入。
- TINPUT timeout 不再通过 `Step(null)` 表达，而通过专门的 `SubmitTimeout()` / `HandleTimerTimeout()` 路径表达。
- `Detect()` 只检测并创建协议实例，不再自动启动线程。

### 8.3 `AgentJsonlProtocol`

`Emuera/UI/Game/AgentJsonlProtocol.cs`

职责：

- `GetInitialTurn()`：等待初始 `WaitInput` / `Quit` / `Error`，返回初始 turn JSON 或 `null`。
- `Step(string input)`：在 `WaitInput` 状态下提交输入，返回下一 turn JSON 或 `null`。
- `SubmitTimeout()`：server 模式超时专用路径，调用 `EmueraConsole` 的 timer-timeout 等价逻辑。
- `BuildTurn()`：采集 `_agentBuffer`、`CurrentRequest`、可见 buttons。

`JsonlCommand` 必须从 private record 提升为 protocol 可访问的内部类型，例如：

```csharp
internal record JsonlCommand(string type, string value);
```

### 8.4 `AgentCliProtocol`

`Emuera/UI/Game/AgentCliProtocol.cs`

职责：

- 保留 CLI 输入缓冲、退格、回车、Escape 行为。
- 不再创建后台线程。
- 提供同步 `RunCliLoop()` 或等价方法，由 `Program.RunHeadless()` 调用。
- v1.1 不处理 TINPUT timeout。

### 8.5 `EmueraConsole`

`Emuera/UI/Game/EmueraConsole.cs`

新增 headless 可见属性：

```csharp
#if HEADLESS
internal long? InputTimeoutMs
{
    get
    {
        if (state != ConsoleState.WaitInput || inputReq == null || inputReq.Timelimit <= 0)
            return null;

        var remaining = inputReq.Timelimit - _genericTimerStopwatch.ElapsedMilliseconds;
        return remaining <= 0 ? 0 : remaining;
    }
}
#endif
```

要求：

- `_genericTimerStopwatch` 必须在 timer 请求创建时重启，而不是在 `Initialize()` 时重启后一直复用。
- `need_settimer` 在 HEADLESS 下也必须被正确清零，避免状态泄漏。
- HEADLESS 下不得启动 `genericTimer`。
- 非 HEADLESS 下保持原行为。

新增内部方法用于 TINPUT timeout：

```csharp
#if HEADLESS
internal void SubmitTimeout()
{
    // 等价于原 endTimer() 的 headless-safe 版本：
    // 1. stopTimer()
    // 2. isTimeout = true
    // 3. 显示 TimeUpMes
    // 4. RunEmueraProgram("")
    // 5. RefreshStrings(true)
}
#endif
```

推荐实现方式：将现有 `endTimer()` 拆分为可复用方法，例如：

```csharp
private void EndTimerCore()
{
    stopTimer();
    isTimeout = true;

    if (IsWaitingPrimitive)
    {
        // primitive 默认输入逻辑
        RefreshStrings(true);
        return;
    }

    if (inputReq.DisplayTime)
        changeLastLine(inputReq.TimeUpMes);
    else if (inputReq.TimeUpMes != null)
        PrintSingleLine(inputReq.TimeUpMes);

    RunEmueraProgram("");
    if (state == ConsoleState.WaitInput && inputReq.NeedValue)
    {
        // headless 下无需移动鼠标；WinForms 下保留原逻辑
    }
    RefreshStrings(true);
}
```

非 HEADLESS 的 `endTimer()` 可调用 `EndTimerCore()` 并保留 WinForms 专属 UI 操作。

---

## 9. 模式实现规格

### 9.1 `Program.RunHeadless()`

`Emuera.Headless/Program.cs`

v1.1 行为：

1. 创建 `HeadlessConsole` 和 `EmueraConsole`。
2. `console.Initialize().Wait()`。
3. `var protocol = console.AgentBridge`。
4. 如果 `protocol == null`：输出错误并退出。
5. 如果 `Console.IsInputRedirected`：
   - 使用 JSONL 同步主循环。
   - 不处理 TINPUT timeout。
6. 否则：
   - 调用 CLI 同步循环。
   - 不处理 TINPUT timeout。

JSONL 主循环伪代码：

```csharp
var io = ConsoleOutIO.Instance;

var initialTurn = protocol.GetInitialTurn();
if (initialTurn != null)
    io.WriteLine(initialTurn);

while (!protocol.IsStopped)
{
    var line = io.ReadLine(-1);
    if (line == null)
        break;

    JsonlCommand? cmd;
    try { cmd = JsonSerializer.Deserialize<JsonlCommand>(line); }
    catch { continue; }

    if (cmd?.type != "input")
        continue;

    var turn = protocol.Step(cmd.value ?? "");
    if (turn != null)
        io.WriteLine(turn);
    else
        break;
}
```

### 9.2 server `Session`

`Emuera/Server/Session.cs`

v1.1 允许保留 session worker thread，但 server 必须单会话。

`Session` 职责：

- 持有单个 `EmueraConsole`。
- 持有单个 `AgentJsonlProtocol`。
- 持有单个 `HttpSessionIO`。
- 启动后输出初始 turn。
- 循环读取 `HttpSessionIO.ReadLine(timeoutMs)`。
- 对 TINPUT timeout 调用 `SubmitTimeout()`。
- 对正常输入调用 `Step(input)`。

`IsRunning` 可以继续表示 session worker 是否存活。

### 9.3 server `HttpGameServer`

`Emuera/Server/HttpGameServer.cs`

v1.1 要求：

- 删除 `SessionManager`。
- 使用单个 `Session? _session`。
- `POST /sessions`：
  - 如果 `_session != null` 且未结束，返回 `409 Conflict`。
  - 否则创建新 session。
- `GET /sessions/{id}`：
  - 只接受当前 session id。
- `GET /sessions/{id}/turn`：
  - 只接受当前 session id。
  - 长轮询当前 session 的 `HttpSessionIO` 输出队列。
- `POST /sessions/{id}/input`：
  - 只接受当前 session id。
  - 将 body 原样 enqueue 到 `HttpSessionIO`。
- `DELETE /sessions/{id}`：
  - 只接受当前 session id。
  - 停止并释放 session。

HTTP 请求处理是否继续使用 `Task.Run` 不属于 v1.1 单线程化验收范围。

---

## 10. 错误处理

### 10.1 输入处理异常

JSONL/server 输入处理异常必须输出合法 JSON：

```csharp
_io.WriteLine(JsonSerializer.Serialize(new
{
    error = ex.Message,
    state = console.State.ToString()
}));
```

### 10.2 IO 断开

- `ReadLine()` 返回 `null` 且 `IsConnected == false`：停止 session。
- `ReadLine(timeoutMs)` 返回 `null` 且 `IsConnected == true`：
  - JSONL 管道模式：v1.1 不处理 timeout，继续阻塞读取。
  - server 模式：调用 `SubmitTimeout()`。

### 10.3 Dispose 幂等

`Session.Dispose()` 必须可重复调用：

```csharp
private bool _disposed;

public void Dispose()
{
    if (_disposed)
        return;

    _disposed = true;
    _cts.Cancel();
    _protocol?.Stop();
    _io?.Close();
    _console?.Dispose();
}
```

---

## 11. 兼容性与回归要求

### 11.1 JSONL 兼容

现有测试必须继续通过：

- [tests/test_jsonl.py](tests/test_jsonl.py)
- [tests/test_buttons.py](tests/test_buttons.py)

要求：

- 初始 turn 仍自动输出。
- `text`、`state`、`inputType`、`needValue`、`buttons` 字段保持存在。
- buttons 的 `label` / `value` 行为不变。
- 非法 JSON 输入不导致进程崩溃。

### 11.2 WinForms 兼容

非 `HEADLESS` 构建必须保持：

- timer 显示倒计时。
- `endTimer()` 的 UI 行为不变。
- 鼠标移动、tooltip、重绘、WinForms 事件处理不变。
- `AgentProtocolBase` 在非 headless 下不引入新行为。

### 11.3 CLI 兼容

CLI 模式必须保持：

- 可输入文本。
- Backspace 删除。
- Enter 提交。
- Escape 清空当前输入。
- 非等待输入状态下输入被提示忽略。

---

## 12. 验收标准

### 12.1 编译

必须通过：

```bash
dotnet build Emuera.Headless/Emuera.Headless.csproj -c Debug
dotnet build Emuera/Emuera.csproj -c Debug-NAudio
```

### 12.2 JSONL 回归

必须通过：

```bash
python tests/test_jsonl.py
python tests/test_buttons.py
```

### 12.3 TINPUT timeout

新增测试必须覆盖：

- TINPUT 超时后输出 `TimeUpMes`（如配置存在）。
- 超时后执行默认空输入路径。
- 超时后状态进入下一 turn。
- 超时后继续输入不会吞行或串轮。
- `InputTimeoutMs` 在剩余时间 <= 0 时返回 `0`。

### 12.4 server 单会话

新增测试或手动验证必须覆盖：

```bash
Emuera.Headless.exe --server --port 8080

curl -X POST http://localhost:8080/sessions
# → 201

curl -X POST http://localhost:8080/sessions
# → 409

curl -X POST http://localhost:8080/sessions/{id}/input \
  -H "Content-Type: application/json" \
  -d '{"type":"input","value":"0"}'
# → 200

curl http://localhost:8080/sessions/{id}/turn
# → 200 或 204，取决于是否已有输出

curl -X DELETE http://localhost:8080/sessions/{id}
# → 200
```

### 12.5 CLI smoke

新增轻量测试或手动验证：

- 非管道启动 `Emuera.Headless.exe --ExeDir <game>`。
- 输入文本、Backspace、Enter。
- 游戏能正常推进。
- 进程可退出。

### 12.6 WinForms 回归

至少手动验证：

- `Emuera.exe` 可启动。
- TINPUT 倒计时显示正常。
- 超时后行为与改造前一致。
- 普通按钮输入正常。

---

## 13. 风险与控制

| 风险 | 等级 | 控制措施 |
|------|------|----------|
| `ConsoleOutIO.ReadLine(timeout)` 误用 `Task.Run(Console.ReadLine)` 导致吞输入 | 高 | v1.1 明确不支持普通 stdin timeout；禁止该实现。 |
| TINPUT timeout 与 `PressEnterKey("")` 语义不一致 | 高 | 新增 `SubmitTimeout()` / `EndTimerCore()`，复用原 timer 语义。 |
| HEADLESS 下 `need_settimer` 状态泄漏 | 中高 | 明确 `need_settimer` 清零位置和 `_genericTimerStopwatch.Restart()` 时机。 |
| server 单会话与 HTTP 长轮询并发冲突 | 中 | `_session` 加锁或同步访问；`HttpSessionIO` 队列线程安全。 |
| WinForms timer 行为被 `#if HEADLESS` 误改 | 中 | 所有 timer UI 逻辑用 `#if !HEADLESS` 保护；非 HEADLESS 构建必须回归。 |
| JSON error 序列化不合法 | 中 | 所有 error turn 使用 `JsonSerializer.Serialize`。 |

---

## 14. 后续 TODO

v1.1 暂不实现，但应记录为后续工作，并在每次制定或修订新计划前更新：

- [普通 stdin 管道模式支持可靠 `ReadLine(timeoutMs)`](./TODO.md#t-001普通-stdin-管道模式支持可靠-readlinetimeoutms)
- [CLI 模式支持 TINPUT timeout](./TODO.md#t-002cli-模式支持-tinput-timeout)
- [server HTTP 层进一步事件驱动化](./TODO.md#t-003server-http-层进一步事件驱动化)
- [补充 server 单会话自动化测试](./TODO.md#t-004补充-server-单会话自动化测试)
- [补充 TINPUT timeout 自动化测试](./TODO.md#t-005补充-tinput-timeout-自动化测试)
- [拆分 SPEC 与 PLAN 职责](./TODO.md#t-006拆分-spec-与-plan-职责)
- [清理 `_agentBufferLock`](./TODO.md#t-007清理-_agentbufferlock)

完整维护文件：[docs/single-thread/TODO.md](./TODO.md)。

---

## 15. 文件改动总览

| 文件 | v1.1 改动 |
|------|-----------|
| `Emuera/Server/SessionIO.cs` | 新增 `ReadLine(int timeoutMs)`；明确各实现支持范围。 |
| `Emuera/Server/ConsoleOutIO.cs` | v1.1 不实现会吞输入的 timeout；保留接口。 |
| `Emuera/Server/HttpSessionIO.cs` | 实现可靠 `ReadLine(int timeoutMs)`。 |
| `Emuera/UI/Game/AgentProtocolBase.cs` | 从后台线程协议改为同步 `Step()`；`Detect()` 不自动启动线程。 |
| `Emuera/UI/Game/AgentJsonlProtocol.cs` | 实现 `GetInitialTurn()`、`Step()`、`SubmitTimeout()`。 |
| `Emuera/UI/Game/AgentCliProtocol.cs` | 改为同步 CLI loop；v1.1 不处理 timeout。 |
| `Emuera/UI/Game/EmueraConsole.cs` | HEADLESS 下暴露 `InputTimeoutMs`；拆分 timer timeout core。 |
| `Emuera.Headless/Program.cs` | JSONL/CLI 主循环改为同步驱动。 |
| `Emuera/Server/Session.cs` | server 同步游戏主循环；允许 session worker thread。 |
| `Emuera/Server/HttpGameServer.cs` | 删除 `SessionManager`，改为单 `Session?`。 |
| `Emuera/Server/SessionManager.cs` | 删除。 |
| `Emuera/UI/Game/EmueraConsole.AgentBuffer.cs` | 单线程化后可移除 `_agentBufferLock`，但需确认所有构建路径。 |

---

## 16. 验收结论判定

v1.1 通过条件：

- 12.1、12.2、12.3、12.4 必须全部通过。
- 12.5、12.6 至少完成 smoke 验证。
- 普通 stdin timeout 不作为失败项，但必须在文档和代码中明确“v1.1 不支持”。
- server 不要求单线程，但必须单会话。
