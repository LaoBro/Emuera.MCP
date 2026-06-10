# Emuera.Headless 单线程化改造实施计划 v1.2

> 版本：v1.2  
> 日期：2026-06-10  
> 配套规格书：[SPEC_SINGLE_THREAD.md](./SPEC_SINGLE_THREAD.md)  
> 维护 TODO：[TODO.md](./TODO.md)  
> 修订说明：基于 v1.1 plan 审查结果修订，明确 WinForms UI 线程语义、HEADLESS `need_settimer`、server 单会话和路由加锁策略。

---

## 1. 总览

本计划将 [SPEC_SINGLE_THREAD.md](./SPEC_SINGLE_THREAD.md) v1.1 拆解为 6 个阶段（Phase），每个阶段产出可编译、可验证的增量。阶段之间有严格依赖顺序。

```text
Phase 0: 基础设施与接口准备
        ↓
Phase 1: 协议层同步化
        ↓
Phase 2: EmueraConsole timer 拆分
        ↓
Phase 3: Server TINPUT timeout
        ↓
Phase 4: Server 单会话化
        ↓
Phase 5: 验收与回归
```

### 1.1 v1.2 关键决策

- **WinForms timer 语义**：保留原 UI 线程语义。`RunEmueraProgram("")` 和 WinForms 专属 `MoveMouse(...)` 仍在 `_uiAdapter.Invoke(...)` 内执行。
- **HEADLESS `need_settimer`**：HEADLESS 下也调用 `setTimer()`，但 `setTimer()` 不启动 `genericTimer`，只初始化 stopwatch、timer 元数据并清零 `need_settimer`。
- **server 单会话**：只要 `_session != null`，无论是否仍在运行，新的 `POST /sessions` 都返回 `409 Conflict`；必须 `DELETE /sessions/{id}` 后才能新建。
- **server 路由并发保护**：`_session` / `_ioMap` 的所有读写都走同一把 `_sessionLock`，包括 `GET`、`POST`、`DELETE`。
- **普通 stdin timeout**：v1.1/v1.2 不支持可靠 `ConsoleOutIO.ReadLine(timeoutMs)`；禁止 `Task.Run(Console.ReadLine)` 模式。

---

## 2. Phase 0 — 基础设施与接口准备

**目标**：为后续改造铺路，不改变运行时行为。

### 0.1 SessionIO 新增 `ReadLine(int timeoutMs)`

**文件**：`Emuera/Server/SessionIO.cs`

**改动**：

- 新增抽象方法：

```csharp
public abstract string? ReadLine(int timeoutMs);
```

- 语义文档：
  - `< 0`：无限等待，等价于 `ReadLine()`。
  - `= 0`：尽力非阻塞；实现无法非阻塞时可阻塞。
  - `> 0`：等待指定毫秒；具体支持范围取决于实现。

### 0.2 ConsoleOutIO 实现 `ReadLine(int timeoutMs)`

**文件**：`Emuera/Server/ConsoleOutIO.cs`

**改动**：

- v1.2 统一走 `Console.ReadLine()`，阻塞读取，忽略 timeout 参数。
- 添加注释明确：
  - v1.2 不支持普通 stdin 管道可靠 timeout。
  - 禁止 `Task.Run(() => Console.ReadLine())` 后 `Task.Wait(timeout)` 的实现。
  - 原因是超时 task 可能稍后消费下一行输入，导致 stdin 输入串轮。

建议实现：

```csharp
public override string? ReadLine(int timeoutMs) => Console.ReadLine();
```

### 0.3 HttpSessionIO 实现可靠 `ReadLine(int timeoutMs)`

**文件**：`Emuera/Server/HttpSessionIO.cs`

**改动**：

- `timeoutMs < 0`：走现有 `ReadLine()` 逻辑，使用 `_inputEvent.WaitOne(100)` 循环，直到有输入或断开。
- `timeoutMs == 0`：只尝试 `_inputQueue.TryDequeue(out line)`；无数据返回 `null`。
- `timeoutMs > 0`：使用 `_inputEvent.WaitOne(timeoutMs)` 等待；超时返回 `null`。
- `Close()` 后 `_connected = false`，后续 `ReadLine()` / `ReadLine(timeoutMs)` 必须尽快返回 `null`。

建议实现结构：

```csharp
public override string? ReadLine(int timeoutMs)
{
    if (timeoutMs == 0)
    {
        if (_inputQueue.TryDequeue(out var line))
            return line;
        return _connected ? null : null;
    }

    if (timeoutMs > 0)
    {
        while (_connected)
        {
            if (_inputQueue.TryDequeue(out var line))
                return line;

            if (!_inputEvent.WaitOne(timeoutMs))
                return _connected ? null : null;
        }

        return null;
    }

    return ReadLine();
}
```

> 注：如果 `WaitOne(timeoutMs)` 被唤醒但队列仍为空，应继续短暂循环或返回 `null`，由调用方按 `IsConnected` 判断。

### 0.4 提升 `JsonlCommand` 可见性

**文件**：`Emuera/UI/Game/AgentJsonlProtocol.cs`

**改动**：

- 将 `private record JsonlCommand(string type, string value)` 提升为 namespace 级内部类型：

```csharp
internal record JsonlCommand(string type, string value);
```

- 或移动到 `AgentProtocolBase.cs` 中作为内部共享类型。

### 0.5 验证点

- `dotnet build Emuera.Headless/Emuera.Headless.csproj -c Debug` 通过。
- `dotnet build Emuera/Emuera.csproj -c Debug-NAudio` 通过。
- 现有行为不变，新接口暂无调用方。

---

## 3. Phase 1 — 协议层同步化

**目标**：将 `AgentProtocolBase` / `AgentJsonlProtocol` / `AgentCliProtocol` 从“后台线程驱动”改为“调用方同步步进驱动”。

> 注意：本阶段只要求协议层不再创建后台线程。server session worker thread 在 v1.2 中允许保留，Phase 4 只做单会话化。

### 1.1 重构 `AgentProtocolBase`

**文件**：`Emuera/UI/Game/AgentProtocolBase.cs`

**改动**：

1. 删除 `_innerProtocol` 字段和 `_thread` 字段。
2. 删除 `Run()` 虚方法。
3. 新增抽象方法：
   - `internal abstract string? GetInitialTurn();`
   - `internal abstract string? Step(string input);`
4. 新增虚方法，默认抛出或返回 `null`：

```csharp
internal virtual string? SubmitTimeout()
{
    throw new NotSupportedException("TINPUT timeout is not supported by this protocol.");
}
```

5. `Stop()` 简化为只设 `_stopped = true`，不再 Join 线程。
6. `DetectAndRun()` 改为 `Detect()`：只检测并返回协议实例，不启动线程。

```csharp
public static AgentProtocolBase? Detect(EmueraConsole console, IConsoleUI ui)
{
    AgentProtocolBase protocol;
    if (Console.IsInputRedirected)
    {
        var stdin = Console.OpenStandardInput();
        if (stdin.CanSeek)
            return null;
        protocol = new AgentJsonlProtocol(console, ui);
    }
    else
    {
        try { _ = Console.KeyAvailable; }
        catch { return null; }
        protocol = new AgentCliProtocol(console, ui);
    }

    return protocol;
}
```

7. 保留 `WriteOutput()` 和 `DispatchInput()` 辅助方法供子类复用。

### 1.2 重构 `AgentJsonlProtocol`

**文件**：`Emuera/UI/Game/AgentJsonlProtocol.cs`

**改动**：

1. 删除 `Run()` 方法，不再创建后台线程。
2. 删除 `ReadStdinLoop()` 和 `HandleMessage()`，主循环由 `Program.RunHeadless()` 或 `Session.GameLoop()` 驱动。
3. 删除 `OnStart()`，由 `GetInitialTurn()` 替代。
4. 实现 `GetInitialTurn()`：

```csharp
internal override string? GetInitialTurn()
{
    if (!WaitForInput())
        return null;

    return BuildTurn();
}
```

5. 实现 `Step(string input)`：

```csharp
internal override string? Step(string input)
{
    if (IsStopped)
        return null;

    if (!WaitForInput())
        return null;

    if (console.State != ConsoleState.WaitInput)
        return null;

    try
    {
        ui.Invoke(() =>
        {
            if (console.State == ConsoleState.WaitInput)
                console.PressEnterKey(false, input, false);
        });

        return BuildTurn();
    }
    catch (Exception ex)
    {
        return JsonSerializer.Serialize(new
        {
            error = ex.Message,
            state = console.State.ToString()
        });
    }
}
```

6. 保留 `SubmitTimeout()` 默认抛出，Phase 3 再覆盖实现。
7. 保留 `WaitForInput()`、`BuildTurn()`、`CollectVisibleButtons()`。
8. 构造函数保留 `SessionIO` 参数：
   - JSONL 管道模式传 `ConsoleOutIO.Instance`。
   - server 模式传 `HttpSessionIO`。

### 1.3 重构 `AgentCliProtocol`

**文件**：`Emuera/UI/Game/AgentCliProtocol.cs`

**改动**：

1. 删除 `Run()` 方法，不再创建后台线程。
2. 新增同步 `RunCliLoop()`：

```csharp
internal void RunCliLoop()
{
    FlushBuffer();

    while (!IsStopped)
    {
        if (Console.KeyAvailable)
        {
            var key = Console.ReadKey(true);
            ProcessKey(key);
        }

        FlushBuffer();
        Thread.Sleep(PollIntervalMs);
    }

    FlushBuffer();
}
```

3. 保留 `ProcessKey()`、`FlushBuffer()`、`DispatchInput()`。
4. `GetInitialTurn()` 和 `Step()` 对 CLI 模式不适用，可返回 `null`。

### 1.4 修改 `EmueraConsole` 初始化

**文件**：`Emuera/UI/Game/EmueraConsole.cs`

**改动**：

- 将构造函数中的 `DetectAndRun()` 改为 `Detect()`：

```csharp
_agentBridge = AgentProtocolBase.Detect(this, _uiAdapter);
```

- 不再在构造函数中启动协议线程。

### 1.5 修改 `Program.RunHeadless()`

**文件**：`Emuera.Headless/Program.cs`

**改动**：

将 `RunHeadless()` 改为同步驱动：

```csharp
private static void RunHeadless()
{
    Console.Error.WriteLine($"[headless] Emuera {AssemblyData.EmueraVersionText} 无头模式启动");
    Console.Error.WriteLine($"[headless] 工作目录: {ExeDir}");
    Console.Error.WriteLine($"[headless] 协议类型: {(Console.IsInputRedirected ? "JSONL (管道)" : "CLI (终端)")}");

    var ui = new HeadlessConsole();
    var console = new EmueraConsole(ui);
    console.Initialize().Wait();

    var protocol = console.AgentBridge;
    if (protocol == null)
    {
        Console.Error.WriteLine("[headless] 未检测到输入管道");
        Environment.Exit(1);
        return;
    }

    if (Console.IsInputRedirected)
        RunJsonlLoop(protocol);
    else
        RunCliLoop(protocol);
}
```

新增 `RunJsonlLoop()`：

```csharp
private static void RunJsonlLoop(AgentProtocolBase protocol)
{
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
}
```

新增 `RunCliLoop()`：

```csharp
private static void RunCliLoop(AgentProtocolBase protocol)
{
    if (protocol is AgentCliProtocol cli)
        cli.RunCliLoop();
    else
        Console.Error.WriteLine("[headless] 非 CLI 协议，无法启动终端交互");
}
```

### 1.6 临时适配 `Session.GameLoop()`

**文件**：`Emuera/Server/Session.cs`

**改动**：

Phase 1 只适配接口变更，不实现 TINPUT timeout。server session worker thread 仍允许存在。

```csharp
private void GameLoop()
{
    try
    {
        _console.Initialize().Wait();

        var initialTurn = _protocol.GetInitialTurn();
        if (initialTurn != null)
            _io.WriteLine(initialTurn);

        while (!_cts.IsCancellationRequested && !_protocol.IsStopped && _io.IsConnected)
        {
            string? line;
            try { line = _io.ReadLine(); }
            catch { break; }

            if (line == null)
                break;

            JsonlCommand? cmd;
            try { cmd = JsonSerializer.Deserialize<JsonlCommand>(line); }
            catch { continue; }

            if (cmd?.type != "input")
                continue;

            var turn = _protocol.Step(cmd.value ?? "");
            if (turn != null)
                _io.WriteLine(turn);
            else
                break;
        }
    }
    catch (Exception ex)
    {
        _io.WriteLine(JsonSerializer.Serialize(new { error = ex.Message, state = "Error" }));
    }
    finally
    {
        _protocol.Stop();
        _console.Dispose();
    }
}
```

### 1.7 验证点

- `dotnet build` 两个项目均通过。
- JSONL 管道模式：初始 turn 自动输出；输入后输出下一 turn；非法 JSON 不崩溃。
- CLI 模式：可输入文本、Backspace、Enter、Escape。
- 协议层不再通过 `AgentProtocolBase.Run()` 创建后台线程。
- server session worker thread 允许保留，后续 Phase 4 再单会话化。

---

## 4. Phase 2 — EmueraConsole timer 拆分

**目标**：将 `endTimer()` 拆分为 headless-safe 核心逻辑和 WinForms 专属 UI 逻辑，为 TINPUT timeout 做准备。

### 2.1 拆分 `endTimer()`

**文件**：`Emuera/UI/Game/EmueraConsole.cs`

**改动**：

新增私有 `EndTimerCore()`，保留 WinForms 原 UI 线程语义：

```csharp
private void EndTimerCore()
{
    stopTimer();
    isTimeout = true;

    if (IsWaitingPrimitive)
    {
        InputMouseKey(4, 0, 0, 0, 0, 0);

#if !HEADLESS
        if (state == ConsoleState.WaitInput && inputReq.NeedValue)
        {
            Point point = _uiAdapter.MainPicBox.PointToClient(_uiAdapter.GetCursorPosition());
            if (_uiAdapter.MainPicBox.ClientRectangle.Contains(point))
                MoveMouse(point);
        }
#endif

        RefreshStrings(true);
        return;
    }

    if (inputReq.DisplayTime)
        changeLastLine(inputReq.TimeUpMes);
    else if (inputReq.TimeUpMes != null)
        PrintSingleLine(inputReq.TimeUpMes);

#if !HEADLESS
    _uiAdapter.Invoke(() =>
    {
        RunEmueraProgram("");
        if (state == ConsoleState.WaitInput && inputReq.NeedValue)
        {
            Point point = _uiAdapter.MainPicBox.PointToClient(_uiAdapter.GetCursorPosition());
            if (_uiAdapter.MainPicBox.ClientRectangle.Contains(point))
                MoveMouse(point);
        }
    });
#else
    RunEmueraProgram("");
    if (state == ConsoleState.WaitInput && inputReq.NeedValue)
    {
        Point point = _uiAdapter.MainPicBox.PointToClient(_uiAdapter.GetCursorPosition());
        if (_uiAdapter.MainPicBox.ClientRectangle.Contains(point))
            MoveMouse(point);
    }
#endif

    RefreshStrings(true);
}
```

原 `endTimer()` 改为：

```csharp
private void endTimer()
{
    EndTimerCore();
}
```

### 2.2 新增 `SubmitTimeout()` 公共方法

**文件**：`Emuera/UI/Game/EmueraConsole.cs`

**改动**：

```csharp
#if HEADLESS
internal void SubmitTimeout()
{
    if (state != ConsoleState.WaitInput || inputReq == null || inputReq.Timelimit <= 0)
        return;

    EndTimerCore();
}
#endif
```

### 2.3 新增 `InputTimeoutMs` 属性

**文件**：`Emuera/UI/Game/EmueraConsole.cs`

**改动**：

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

### 2.4 HEADLESS 下 `setTimer()` 不启动 `genericTimer`

**文件**：`Emuera/UI/Game/EmueraConsole.cs`

**改动**：

```csharp
private void setTimer()
{
    isTimeout = false;
    timerID = inputReq.ID;
    _genericTimerStopwatch.Restart();
    timer_endTime = inputReq.Timelimit;
    need_settimer = false;

#if !HEADLESS
    genericTimer.Enabled = true;
#endif
}
```

说明：

- HEADLESS 下 `setTimer()` 仍会被调用。
- HEADLESS 下不启动 `genericTimer`。
- `need_settimer` 在 `setTimer()` 内清零，避免依赖 `#if !HEADLESS` 路径。

### 2.5 确保 `need_settimer` 在 HEADLESS 下正确清零

**文件**：`Emuera/UI/Game/EmueraConsole.cs`

**改动**：

将当前 `RefreshStrings()` 中的清零逻辑从 `#if !HEADLESS` 块中移出，保证 HEADLESS 下也能调用 `setTimer()`：

```csharp
if (need_settimer)
{
    need_settimer = false;
    setTimer();
}
```

同时保留 WinForms 专属逻辑仍在 `#if !HEADLESS` 内。

### 2.6 验证点

- `dotnet build` 两个项目均通过。
- WinForms 构建下 `endTimer()` 行为不变，`RunEmueraProgram("")` 仍在 UI 线程执行。
- HEADLESS 构建下 `genericTimer` 不启动。
- HEADLESS 下 `need_settimer` 会被清零。
- `InputTimeoutMs` 在 TINPUT 请求时返回正确剩余毫秒数。
- `InputTimeoutMs` 在剩余时间 `<= 0` 时返回 `0`。
- `SubmitTimeout()` 在 HEADLESS 下执行等价于原 `endTimer()` 的核心逻辑。

---

## 5. Phase 3 — Server TINPUT timeout

**目标**：在 server 模式下实现 TINPUT 超时，行为尽量接近原 WinForms timer。

### 3.1 `AgentJsonlProtocol.SubmitTimeout()` 实现

**文件**：`Emuera/UI/Game/AgentJsonlProtocol.cs`

**改动**：

```csharp
internal override string? SubmitTimeout()
{
    console.SubmitTimeout();
    return BuildTurn();
}
```

### 3.2 `Session.GameLoop()` 添加 TINPUT timeout 逻辑

**文件**：`Emuera/Server/Session.cs`

**改动**：

- 每次循环先读取 `InputTimeoutMs`。
- 如果 `timeoutMs.HasValue && timeoutMs.Value <= 0`，立即 `SubmitTimeout()`，不要阻塞读取。
- 如果 `timeoutMs.HasValue && timeoutMs.Value > 0`，使用 `_io.ReadLine((int)timeoutMs.Value)`。
- 如果 `timeoutMs` 为 `null`，使用普通 `_io.ReadLine()`。

```csharp
private void GameLoop()
{
    try
    {
        _console.Initialize().Wait();

        var initialTurn = _protocol.GetInitialTurn();
        if (initialTurn != null)
            _io.WriteLine(initialTurn);

        while (!_cts.IsCancellationRequested && !_protocol.IsStopped && _io.IsConnected)
        {
            long? timeoutMs = _console.InputTimeoutMs;

            if (timeoutMs.HasValue && timeoutMs.Value <= 0)
            {
                var turn = _protocol.SubmitTimeout();
                if (turn != null)
                    _io.WriteLine(turn);
                continue;
            }

            string? line;
            if (timeoutMs.HasValue)
                line = _io.ReadLine((int)timeoutMs.Value);
            else
                line = _io.ReadLine();

            if (line == null)
            {
                if (!_io.IsConnected)
                    break;

                if (timeoutMs.HasValue)
                {
                    var turn = _protocol.SubmitTimeout();
                    if (turn != null)
                        _io.WriteLine(turn);
                    continue;
                }

                break;
            }

            JsonlCommand? cmd;
            try { cmd = JsonSerializer.Deserialize<JsonlCommand>(line); }
            catch { continue; }

            if (cmd?.type != "input")
                continue;

            var turn = _protocol.Step(cmd.value ?? "");
            if (turn != null)
                _io.WriteLine(turn);
            else
                break;
        }
    }
    catch (Exception ex)
    {
        _io.WriteLine(JsonSerializer.Serialize(new { error = ex.Message, state = "Error" }));
    }
    finally
    {
        _protocol.Stop();
        _console.Dispose();
    }
}
```

### 3.3 验证点

- TINPUT 超时后输出 `TimeUpMes`（如配置存在）。
- 超时后执行默认空输入路径，状态进入下一 turn。
- 超时后继续输入不会吞行或串轮。
- `InputTimeoutMs` 在剩余时间 `<= 0` 时返回 `0`。
- 非 TINPUT 请求时 `InputTimeoutMs` 返回 `null`，不影响正常输入。
- `timeoutMs == 0` 时立即 timeout，不阻塞。

---

## 6. Phase 4 — Server 单会话化

**目标**：将 HTTP server 从多会话改为单会话，删除 `SessionManager`。

### 4.1 删除 `SessionManager`

**文件**：`Emuera/Server/SessionManager.cs`

**改动**：删除整个文件。

### 4.2 重构 `HttpGameServer`

**文件**：`Emuera/Server/HttpGameServer.cs`

**改动**：

1. 删除 `SessionManager _sessions` 字段。
2. 新增：

```csharp
private Session? _session;
private readonly object _sessionLock = new();
private readonly ConcurrentDictionary<string, HttpSessionIO> _ioMap = new();
```

3. `POST /sessions`：只要 `_session != null`，无论是否仍在运行，都返回 `409 Conflict`。

```csharp
if (method == "POST" && path == "/sessions")
{
    lock (_sessionLock)
    {
        if (_session != null)
        {
            WriteJson(resp, 409, new { error = "A session is already active" });
            return;
        }

        var io = new HttpSessionIO();
        _session = new Session(io);
        io.SessionId = _session.Id;
        _ioMap[_session.Id] = io;
        _session.Start();
    }

    WriteJson(resp, 201, new { sessionId = _session.Id, createdAt = _session.CreatedAt });
    return;
}
```

> 注意：响应中读取 `_session.Id` 时应避免锁外访问竞态。更稳妥写法是在 lock 内保存局部变量：

```csharp
Session session;
lock (_sessionLock)
{
    if (_session != null)
    {
        WriteJson(resp, 409, new { error = "A session is already active" });
        return;
    }

    var io = new HttpSessionIO();
    session = new Session(io);
    io.SessionId = session.Id;
    _ioMap[session.Id] = io;
    _session = session;
    session.Start();
}

WriteJson(resp, 201, new { sessionId = session.Id, createdAt = session.CreatedAt });
```

4. 所有路由访问 `_session` / `_ioMap` 都必须 `lock (_sessionLock)`：
   - `GET /sessions/{id}`
   - `GET /sessions/{id}/turn`
   - `POST /sessions/{id}/input`
   - `DELETE /sessions/{id}`

5. 其他路由只接受当前 session id：
   - `GET /sessions/{id}` — `id == _session?.Id`，否则 404。
   - `GET /sessions/{id}/turn` — 同上。
   - `POST /sessions/{id}/input` — 同上。
   - `DELETE /sessions/{id}` — 同上，销毁后 `_session = null`。

6. `GET /sessions/{id}/turn` 在锁内取得当前 `HttpSessionIO` 后，可以在锁外长轮询该 IO；如果 session 被删除，`HttpSessionIO.IsConnected` 应变为 false 并结束轮询。

7. `Dispose()` 中清理 `_session`。

### 4.3 `Session.Dispose()` 幂等

**文件**：`Emuera/Server/Session.cs`

**改动**：

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

### 4.4 验证点

- `POST /sessions` 第一次返回 `201 Created`。
- `POST /sessions` 第二次在旧 session 未 DELETE 前返回 `409 Conflict`。
- 旧 session 已结束但未 DELETE 时，仍返回 `409 Conflict`。
- `DELETE /sessions/{id}` 后可重新创建会话。
- 并发请求不会创建多个会话，所有 `_session` / `_ioMap` 访问均有 `_sessionLock` 保护。
- 旧 session 删除后，旧 `/turn` 长轮询应结束或返回 404/204，不得挂死。

---

## 7. Phase 5 — 验收与回归

**目标**：确保所有验收标准通过。

### 5.1 编译验收

```bash
dotnet build Emuera.Headless/Emuera.Headless.csproj -c Debug
dotnet build Emuera/Emuera.csproj -c Debug-NAudio
```

### 5.2 JSONL 回归

```bash
python tests/test_jsonl.py
python tests/test_buttons.py
```

如测试文件不存在，需手动验证：

- 初始 turn 自动输出。
- `text`、`state`、`inputType`、`needValue`、`buttons` 字段保持存在。
- 非法 JSON 输入不导致进程崩溃。
- buttons 的 `label` / `value` 行为不变。

### 5.3 TINPUT timeout 自动化验收

新增测试必须覆盖：

- TINPUT 超时后输出 `TimeUpMes`（如配置存在）。
- 超时后执行默认空输入路径。
- 超时后状态进入下一 turn。
- 超时后继续输入不会吞行或串轮。
- `InputTimeoutMs` 在剩余时间 `<= 0` 时返回 `0`。
- `timeoutMs == 0` 时立即 timeout，不阻塞。

如测试游戏构造困难，至少提供可重复脚本，但目标仍是自动化测试。

### 5.4 Server 单会话验收

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
# → 200 或 204

curl -X DELETE http://localhost:8080/sessions/{id}
# → 200

curl -X POST http://localhost:8080/sessions
# → 201
```

并补充并发验证：

- 并发创建 session 时最多一个成功。
- 旧 session 未 DELETE 前，即使已结束，也不允许新建。
- 删除 session 后，旧 session id 的路由返回 404。

### 5.5 CLI smoke 验收

- 非管道启动 `Emuera.Headless.exe --ExeDir <game>`。
- 输入文本、Backspace、Enter。
- 游戏能正常推进。
- 进程可退出。

### 5.6 WinForms 回归验收

- `Emuera.exe` 可启动。
- TINPUT 倒计时显示正常。
- 超时后行为与改造前一致。
- 普通按钮输入正常。

---

## 8. 文件改动清单

| 文件 | Phase | 改动摘要 |
|------|-------|----------|
| `Emuera/Server/SessionIO.cs` | 0 | 新增 `ReadLine(int timeoutMs)` 抽象方法 |
| `Emuera/Server/ConsoleOutIO.cs` | 0 | 实现 `ReadLine(int timeoutMs)`；v1.2 阻塞读取，不支持 timeout |
| `Emuera/Server/HttpSessionIO.cs` | 0 | 实现可靠 `ReadLine(int timeoutMs)` |
| `Emuera/UI/Game/AgentJsonlProtocol.cs` | 0 | `JsonlCommand` 提升为 internal |
| `Emuera/UI/Game/AgentProtocolBase.cs` | 1 | 删除 `Run()`/`_thread`/`_innerProtocol`；新增 `GetInitialTurn()`/`Step()`；`DetectAndRun()` → `Detect()` |
| `Emuera/UI/Game/AgentJsonlProtocol.cs` | 1 | 删除 `Run()`/`ReadStdinLoop()`/`HandleMessage()`/`OnStart()`；实现 `GetInitialTurn()`/`Step()`/`SubmitTimeout()` 存根 |
| `Emuera/UI/Game/AgentCliProtocol.cs` | 1 | 删除 `Run()`；新增 `RunCliLoop()` |
| `Emuera/UI/Game/EmueraConsole.cs` | 1 | `DetectAndRun()` → `Detect()` |
| `Emuera.Headless/Program.cs` | 1 | `RunHeadless()` 改为同步驱动；新增 `RunJsonlLoop()`/`RunCliLoop()` |
| `Emuera/Server/Session.cs` | 1 | `GameLoop()` 临时改为同步 `Step()` 驱动，仍允许 worker thread |
| `Emuera/UI/Game/EmueraConsole.cs` | 2 | 拆分 `EndTimerCore()`；新增 `SubmitTimeout()`/`InputTimeoutMs`；HEADLESS 下禁止 `genericTimer`；HEADLESS 下也清零 `need_settimer` |
| `Emuera/UI/Game/AgentJsonlProtocol.cs` | 3 | `SubmitTimeout()` 实际实现 |
| `Emuera/Server/Session.cs` | 3 | `GameLoop()` 添加 TINPUT timeout 逻辑 |
| `Emuera/Server/SessionManager.cs` | 4 | 删除 |
| `Emuera/Server/HttpGameServer.cs` | 4 | 删除 `SessionManager`；改为单 `Session?`；`_sessionLock` 保护所有 session 路由；`POST /sessions` 未 DELETE 前返回 409 |
| `Emuera/Server/Session.cs` | 4 | `Dispose()` 幂等 |

---

## 9. 风险控制要点

| 风险 | 对应 Phase | 控制措施 |
|------|-----------|----------|
| `ConsoleOutIO.ReadLine(timeout)` 误用 `Task.Run(Console.ReadLine)` | 0 | v1.2 明确不实现；代码注释 + 规格约束 |
| `EndTimerCore()` 破坏 WinForms UI 线程语义 | 2 | `RunEmueraProgram("")` 和 WinForms `MoveMouse(...)` 保留在 `_uiAdapter.Invoke(...)` 内 |
| `need_settimer` 状态泄漏 | 2 | HEADLESS 下也调用 `setTimer()`；`setTimer()` 内 `need_settimer = false` |
| `InputTimeoutMs == 0` 被普通 `ReadLine()` 阻塞 | 3 | `timeoutMs <= 0` 时立即 `SubmitTimeout()` |
| WinForms timer 行为被 `#if HEADLESS` 误改 | 2 | 非 HEADLESS 构建回归测试 |
| JSON error 序列化不合法 | 1/3 | 统一使用 `JsonSerializer.Serialize` |
| Server 单会话并发冲突 | 4 | `_sessionLock` 保护所有 `_session` / `_ioMap` 读写 |
| `HttpSessionIO.ReadLine(timeout)` 精度 | 0 | 使用 `AutoResetEvent.WaitOne(timeoutMs)`，精度取决于 OS 调度 |
| 删除 session 后旧长轮询挂死 | 4 | `HttpSessionIO.Close()` 后 `ReadLine(timeout)` 尽快返回；长轮询检查 `IsConnected` |

---

## 10. 不在 v1.2 范围内的 TODO

以下项目明确不在 v1.2 实施范围内，完整维护在 [TODO.md](./TODO.md)：

- [T-001：普通 stdin 管道模式支持可靠 `ReadLine(timeoutMs)`](./TODO.md#t-001普通-stdin-管道模式支持可靠-readlinetimeoutms)
- [T-002：CLI 模式支持 TINPUT timeout](./TODO.md#t-002cli-模式支持-tinput-timeout)
- [T-003：server HTTP 层进一步事件驱动化](./TODO.md#t-003server-http-层进一步事件驱动化)
- [T-004：补充 server 单会话自动化测试](./TODO.md#t-004补充-server-单会话自动化测试)
- [T-005：补充 TINPUT timeout 自动化测试](./TODO.md#t-005补充-tinput-timeout-自动化测试)
- [T-006：拆分 SPEC 与 PLAN 职责](./TODO.md#t-006拆分-spec-与-plan-职责)
- [T-007：清理 `_agentBufferLock`](./TODO.md#t-007清理-_agentbufferlock)

---

## 11. 新计划更新记录

| 日期 | 计划版本 | 更新内容 |
|------|----------|----------|
| 2026-06-10 | v1.1 | 初始实施计划，基于 SPEC v1.1。 |
| 2026-06-10 | v1.2 | 修正 WinForms UI 线程语义、HEADLESS `need_settimer`、`InputTimeoutMs == 0`、server 单会话和路由加锁策略；同步更新 TODO 链接。 |
