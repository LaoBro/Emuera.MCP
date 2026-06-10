# Emuera.Headless 单线程化改造规格书

> 版本：v1.0 | 日期：2026-06-10

---

## 一、为什么要做这个改造

### 1.1 现状

当前 Headless 模式下，每个游戏会话至少使用 2 个线程：

- **主线程**：什么都不做，只是 `Thread.Sleep(100)` 轮询等待协议线程结束
- **Agent 线程**：读取输入、调用 `PressEnterKey` 驱动游戏、采集输出

此外还有一个隐藏的 **Timer 线程**（`System.Timers.Timer`），在游戏脚本使用 `TINPUT`（带超时的输入指令）时触发，通过 `ui.Invoke` 调用游戏逻辑。

这套多线程设计是从 WinForms 版本继承来的——WinForms 中 UI 线程不能阻塞在 `ReadLine` 上（会冻住窗口），所以必须用独立线程读输入。但在无头模式下，根本没有窗口需要响应，这些线程只是增加了复杂度。

### 1.2 问题

1. **竞态条件**：Timer 线程和 Agent 线程可能同时调用 `PressEnterKey`，没有任何锁保护。在 WinForms 中这不是问题（因为 `Control.Invoke` 会串行化到 UI 线程），但 Headless 的 `Invoke` 是直接调用，没有串行化保障。

2. **资源浪费**：服务器模式下每个会话 2-3 个线程，100 个会话就是 200-300 个线程，大部分时间都在 `Sleep` 或 `WaitOne`。

3. **代码复杂**：为了线程安全，引入了 `_agentBufferLock`、`ConcurrentQueue`、`AutoResetEvent` 等同步原语。`AgentJsonlProtocol` 还需要 `WaitForInput()` 轮询（每 50ms 检查一次游戏状态），增加了延迟。

4. **调试困难**：多线程 bug 难以复现，`ui.Invoke` 的间接调用让调用栈变深。

### 1.3 核心洞察

**游戏引擎本身就是单线程的。** `Process.DoScript()` 是一个同步阻塞方法——从当前指令一直执行到游戏进入 `WaitInput` 状态才返回，中间不会 yield、不会 await、不会主动让出线程。这意味着：

- 不可能同时处理两个输入
- 不可能在执行游戏逻辑的同时做其他事情
- 每个会话天然就是"输入 → 执行 → 输出 → 等待下一个输入"的串行循环

当前的多线程完全是为了适配 WinForms 消息泵而引入的，在无头场景下是多余的。

---

## 二、改造目标

把 Headless 模式从"多线程 + 间接调用"改为"单线程 + 直接调用"，让代码更简单、更可靠、更省资源。

具体来说：

- **管道模式**（`--headless`）：从 3 个线程降到 1 个
- **服务器模式**（`--server`）：从每会话 2-3 个线程降到每会话 1 个；暂只实现单会话
- 消除 Timer 线程与 Agent 线程的竞态条件
- 消除 `ui.Invoke` 间接调用，改为直接调用
- 消除 `_agentBufferLock`，改为单线程直接访问
- 消除 `WaitForInput()` 轮询，因为 `PressEnterKey` 执行完毕后游戏一定已经到达下一个 `WaitInput`

---

## 三、改造后的运行模型

### 3.1 管道模式

**改造前**（3 个线程）：

```
主线程:     Sleep(100) 轮询 ──────────────────────────────── 退出
Agent 线程: ReadLine → WaitForInput(轮询) → Invoke(PressEnterKey) → BuildTurn → ReadLine → ...
Timer 线程: [TINPUT时] tickTimer → Invoke(RunEmueraProgram)
```

**改造后**（1 个线程）：

```
主线程: ReadLine → PressEnterKey → BuildTurn → WriteTurn → ReadLine → ...
```

就这么简单。主线程读一行输入，直接调用 `PressEnterKey`（游戏逻辑同步执行完毕），构建回合输出，写出去，然后读下一行输入。

### 3.2 服务器模式（单会话）

**改造前**（3+ 个线程）：

```
主线程:           Sleep 等待 Enter
HttpListener 线程: 接收请求 → 投递到 SessionIO 队列
Session 线程:     Sleep(100) 轮询
Agent 线程:       ReadLine(从队列) → WaitForInput → Invoke(PressEnterKey) → BuildTurn → ...
```

**改造后**（2 个线程：1 个 HTTP + 1 个游戏）：

```
主线程:           Sleep 等待 Enter
HttpListener 线程: 接收请求 → 投递到 SessionIO 队列 / 从队列读取输出
游戏线程:         ReadLine(从队列) → PressEnterKey → BuildTurn → WriteLine(到队列) → ReadLine → ...
```

HttpListener 线程是 .NET 运行时管理的，我们无法消除。但游戏逻辑本身只在 1 个线程上运行。

### 3.3 TINPUT（带超时的输入）的处理

**改造前**：`System.Timers.Timer` 在 ThreadPool 线程上触发超时回调。

**改造后**：在等待输入时用带超时的阻塞等待。如果超时了还没收到输入，就自动提交默认输入。

```
游戏线程: PressEnterKey → 游戏进入 WaitInput(带超时)
         → ReadLine(timeout=剩余时间)
             ├─ 收到输入 → 正常处理
             └─ 超时 → PressEnterKey(默认值) → 游戏继续
```

---

## 四、需要改动的文件

### 4.1 重写的文件

| 文件 | 改动说明 |
|------|---------|
| `Emuera.Headless/Program.cs` | `RunHeadless()` 和 `RunServer()` 改为同步循环驱动 |

### 4.2 大幅修改的文件

| 文件 | 改动说明 |
|------|---------|
| `Emuera/UI/Game/AgentProtocolBase.cs` | 去掉线程管理，保留 `BuildTurn` / `CollectVisibleButtons` 等工具方法，新增同步 `Step()` 方法 |
| `Emuera/UI/Game/AgentJsonlProtocol.cs` | 重写：去掉 `ReadStdinLoop` 和 `WaitForInput` 轮询，改为同步的 `Step(value)` 方法 |
| `Emuera/UI/Game/AgentCliProtocol.cs` | 重写：去掉独立线程，改为同步的按键处理循环 |
| `Emuera/Server/Session.cs` | `GameLoop` 改为同步驱动循环，不再启动 Agent 线程 |
| `Emuera/Server/SessionIO.cs` | `ReadLine()` 增加超时参数 `ReadLine(int timeoutMs)` |

### 4.3 小幅修改的文件

| 文件 | 改动说明 |
|------|---------|
| `Emuera/UI/Game/EmueraConsole.cs` | Timer 相关逻辑加 `#if HEADLESS` 条件编译，无头模式下禁用 `genericTimer` |
| `Emuera/UI/Game/HeadlessConsole.cs` | `Invoke()` 改为直接调用（已经是了，确认无需改动） |
| `Emuera/Server/HttpSessionIO.cs` | `ReadLine()` 支持超时参数 |
| `Emuera/Server/ConsoleOutIO.cs` | `ReadLine()` 支持超时参数 |
| `Emuera/Server/HttpGameServer.cs` | 适配单会话模式，去掉多会话管理 |

### 4.4 可以删除/简化的代码

| 代码 | 位置 | 原因 |
|------|------|------|
| `AgentProtocolBase._thread` 字段 | AgentProtocolBase.cs | 不再需要后台线程 |
| `AgentProtocolBase.Run()` 的线程启动逻辑 | AgentProtocolBase.cs | 改为同步调用 |
| `AgentJsonlProtocol.ReadStdinLoop()` | AgentJsonlProtocol.cs | 改为主循环直接 ReadLine |
| `AgentJsonlProtocol.WaitForInput()` | AgentJsonlProtocol.cs | PressEnterKey 后游戏直接到达 WaitInput，无需轮询 |
| `AgentJsonlProtocol.SubmitAndGetTurn()` 中的 `ui.Invoke` | AgentJsonlProtocol.cs | 直接调用 PressEnterKey |
| `EmueraConsole.genericTimer` / `tickTimer` / `endTimer` | EmueraConsole.cs | 无头模式下用内联超时替代 |
| `EmueraConsole._agentBufferLock` | EmueraConsole.cs | 单线程无需锁 |
| `SessionManager.cs` | Server/ | 单会话模式不需要 |

---

## 五、关键接口设计

### 5.1 AgentProtocolBase — 从"线程管理器"变为"同步步进器"

**改造前**：

```csharp
class AgentProtocolBase
{
    protected Thread _thread;
    abstract void Run();           // 启动后台线程
    void Stop();                   // 停止线程
    void WriteOutput(...);         // 写入缓冲（需加锁）
}
```

**改造后**：

```csharp
class AgentProtocolBase
{
    bool IsStopped { get; }

    // 同步执行一个游戏回合：输入 → 游戏执行 → 返回回合 JSON
    // 超时返回 null
    abstract string? Step(string? input = null);

    // 初始化后获取初始回合（游戏加载完毕后的第一个 WaitInput 状态）
    string? GetInitialTurn();
}
```

调用方式从"启动线程然后轮询"变成"直接调用 Step"：

```csharp
// 改造前
protocol.Run();
while (!protocol.IsStopped) Thread.Sleep(100);

// 改造后
string? turn = protocol.GetInitialTurn();
WriteTurn(turn);
while (!protocol.IsStopped)
{
    string? input = ReadInput();
    turn = protocol.Step(input);
    WriteTurn(turn);
}
```

### 5.2 SessionIO — 增加超时读取

```csharp
abstract class SessionIO
{
    // 原有
    abstract string? ReadLine();
    abstract void WriteLine(string text);
    abstract void Close();
    abstract bool IsConnected { get; }

    // 新增：带超时的读取
    // timeoutMs < 0 表示无限等待（等同于 ReadLine()）
    // timeoutMs = 0 表示立即返回（非阻塞）
    // timeoutMs > 0 表示等待指定毫秒数
    // 超时返回 null，IsConnected 仍为 true
    abstract string? ReadLine(int timeoutMs);
}
```

### 5.3 EmueraConsole — 暴露超时信息

为了让协议层知道当前输入的超时时间，需要在 `EmueraConsole` 上暴露：

```csharp
// EmueraConsole 已有 inputReq 字段
// 只需暴露超时信息
public long? InputTimeoutMs
{
    get
    {
        if (state != ConsoleState.WaitInput || inputReq == null || inputReq.Timelimit <= 0)
            return null;
        return inputReq.Timelimit - _genericTimerStopwatch.ElapsedMilliseconds;
    }
}
```

---

## 六、各模式的完整流程

### 6.1 管道模式 (`--headless`)

```
1. 初始化
   - 创建 HeadlessConsole
   - 创建 EmueraConsole(ui)
   - console.Initialize().Wait()     ← 加载游戏，执行到第一个 WaitInput

2. 输出初始回合
   - protocol.GetInitialTurn()       ← 采集初始输出和按钮
   - 写入 stdout

3. 主循环（单线程）
   while (协议未停止)
   {
       line = Console.ReadLine()     ← 阻塞等待输入
       if (line == null) break       ← 管道关闭

       cmd = 解析 JSON
       turn = protocol.Step(cmd.value)  ← 同步执行游戏回合
       写入 stdout
   }

4. 退出
```

### 6.2 服务器模式 (`--server`，单会话)

```
1. 初始化
   - 创建 HttpSessionIO
   - 启动 HttpGameServer

2. 游戏线程（单线程）
   - 创建 HeadlessConsole
   - 创建 EmueraConsole(ui)
   - console.Initialize().Wait()
   - protocol.GetInitialTurn() → 写入 HttpSessionIO 输出队列

   while (会话未断开)
   {
       timeoutMs = console.InputTimeoutMs ?? -1
       line = io.ReadLine(timeoutMs)   ← 带超时等待

       if (line == null && 超时)
           turn = protocol.Step("")    ← 超时，提交默认输入
       else if (line != null)
           turn = protocol.Step(解析输入)  ← 正常输入
       else
           break                       ← 连接断开

       写入 HttpSessionIO 输出队列
   }

3. HttpListener 线程（.NET 管理）
   - POST /input → io.EnqueueInput(body)
   - GET /turn   → io.TryDequeueOutput() 或长轮询等待
```

### 6.3 CLI 交互模式

```
1. 初始化（同管道模式）

2. 主循环（单线程）
   while (协议未停止)
   {
       // 刷新输出
       text = console.TakeAgentBuffer()
       if (text.Length > 0) Console.Write(text)

       // 检查是否有按键
       if (Console.KeyAvailable)
       {
           key = Console.ReadKey(true)
           处理按键（Enter 提交、Backspace 删除、字符追加）
       }
       else if (console.State == ConsoleState.WaitInput && 有超时)
       {
           // 带超时等待按键
           ... 超时则提交默认输入
       }
       else
       {
           Thread.Sleep(50)  ← 短暂让出 CPU
       }
   }
```

---

## 七、TINPUT 超时的详细处理

TINPUT 是游戏脚本中的"带超时的输入"指令，例如 `TINPUT 5000,"超时了",0` 表示等待 5 秒，超时后自动提交默认值。

### 7.1 改造前

```
1. 游戏执行 TINPUT → WaitInput(timelimit=5000)
2. EmueraConsole 启动 genericTimer（10ms 间隔）
3. Timer 每 10ms 检查是否超时
4. 超时 → tickTimer → endTimer → ui.Invoke(RunEmueraProgram(""))
5. Agent 线程的 WaitForInput 检测到状态变化
```

问题：Timer 回调在 ThreadPool 线程上，与 Agent 线程存在竞态。

### 7.2 改造后

```
1. 游戏执行 TINPUT → WaitInput(timelimit=5000)
2. 协议层读取 console.InputTimeoutMs → 5000
3. io.ReadLine(timeoutMs: 5000)  ← 带超时阻塞
4. 两种结果：
   a) 5 秒内收到输入 → 正常 Step(input)
   b) 超时 → Step("")  ← 提交空字符串，游戏引擎内部会使用默认值
```

**注意**：`PressEnterKey` 内部会检查 `genericTimer.Enabled` 来决定是否停止定时器。在无头模式下，`genericTimer` 不会被启用（通过 `#if HEADLESS` 禁用），所以 `PressEnterKey` 中的 `stopTimer()` 调用是安全的（对已停止的 Timer 调用 `Enabled = false` 不会有副作用）。

### 7.3 需要处理的边界情况

- **DisplayTime**：TINPUT 可以配置为显示剩余时间倒计时。无头模式下不需要这个功能，跳过。
- **OneInput**：TINPUT 可以配置为只接受单字符输入。协议层不需要特殊处理，由 `PressEnterKey` 内部逻辑处理。
- **超时后游戏直接退出**：`Step("")` 返回后检查 `console.State`，如果是 `Quit` 或 `Error` 则终止循环。

---

## 八、EmueraConsole 中 Timer 相关的条件编译

### 8.1 需要禁用的 Timer 逻辑

在 `#if HEADLESS` 下，以下逻辑需要禁用：

| 逻辑 | 位置 | 原因 | 替代 |
|------|------|------|------|
| `genericTimer.Enabled = true` | `setTimer()` | 不需要 Timer 触发超时 | 协议层用 ReadLine 超时替代 |
| `tickTimer` 回调 | `tickTimer()` | 不需要 Timer 回调 | 协议层内联超时检查 |
| `endTimer` 中的 `ui.Invoke(RunEmueraProgram)` | `endTimer()` | 不需要 Timer 触发游戏逻辑 | 协议层 `Step("")` 替代 |
| `redrawTimer` | `setRedrawTimer()` | 无头模式不需要动画重绘 | 直接禁用 |
| `inputReq.DisplayTime` 倒计时更新 | `tickTimer()` | 无头模式不显示倒计时 | 跳过 |

### 8.2 具体改动

```csharp
// EmueraConsole.cs 构造函数中
#if HEADLESS
    genericTimer = new();
    genericTimer.Elapsed += tickTimer;
    genericTimer.Interval = 10;
    genericTimer.Enabled = false;
    // 不需要 CBG_Clear
#else
    // ... 原有代码
#endif

// presetTimer() 中
private void presetTimer()
{
#if HEADLESS
    // 无头模式：不启动 Timer，超时由协议层处理
    // 但仍需设置 need_settimer 标记，以便 InputTimeoutMs 属性能返回正确的超时值
    need_settimer = true;
    _genericTimerStopwatch.Restart();
#else
    need_settimer = true;
    if (inputReq.DisplayTime)
    {
        var remainingMs = inputReq.Timelimit - _genericTimerStopwatch.ElapsedMilliseconds;
        PrintSingleLine(trsl.Remaining.Text + $"{remainingMs / 1000.0f:0.0}");
        timeDisplayCount = 0;
        inputed = false;
    }
#endif
}

// setTimer() 中
private void setTimer()
{
#if HEADLESS
    // 无头模式：不启动 Timer
#else
    genericTimer.Enabled = true;
    _genericTimerStopwatch.Restart();
    timer_endTime = inputReq.Timelimit;
#endif
}
```

---

## 九、服务器单会话模式的简化

### 9.1 当前多会话架构

```
HttpGameServer
  ├── SessionManager (ConcurrentDictionary<string, Session>)
  │     └── Timer (1分钟清理空闲 >30分钟的会话)
  └── ConcurrentDictionary<string, HttpSessionIO>
```

### 9.2 单会话简化

```
HttpGameServer
  ├── Session? _session          ← 唯一的会话
  └── HttpSessionIO _io          ← 唯一的 IO
```

**API 变化**：

| 方法 | 路径 | 行为变化 |
|------|------|---------|
| POST | `/sessions` | 如果已有会话则返回 409 Conflict，否则创建 |
| GET | `/sessions/{id}` | 只接受当前会话的 id，否则 404 |
| POST | `/sessions/{id}/input` | 同上 |
| GET | `/sessions/{id}/turn` | 同上 |
| DELETE | `/sessions/{id}` | 销毁当前会话，允许创建新会话 |

**好处**：
- 不需要 `SessionManager`
- 不需要空闲超时清理
- 不需要 `ConcurrentDictionary`
- 全局状态（`GlobalStatic`、`Config`）的并发访问问题不存在

---

## 十、改动清单汇总

### 10.1 新建文件

无。所有改动都是修改现有文件。

### 10.2 修改文件

| 文件 | 改动量 | 改动说明 |
|------|--------|---------|
| `Emuera.Headless/Program.cs` | 大 | 重写 `RunHeadless()` 和 `RunServer()` 为同步循环 |
| `Emuera/UI/Game/AgentProtocolBase.cs` | 大 | 去掉线程管理，新增 `Step()` / `GetInitialTurn()` |
| `Emuera/UI/Game/AgentJsonlProtocol.cs` | 大 | 重写为同步步进器 |
| `Emuera/UI/Game/AgentCliProtocol.cs` | 大 | 重写为同步循环 |
| `Emuera/Server/Session.cs` | 中 | `GameLoop` 改为同步驱动 |
| `Emuera/Server/SessionIO.cs` | 小 | `ReadLine` 增加超时重载 |
| `Emuera/Server/HttpSessionIO.cs` | 小 | 实现超时 ReadLine |
| `Emuera/Server/ConsoleOutIO.cs` | 小 | 实现超时 ReadLine |
| `Emuera/Server/HttpGameServer.cs` | 中 | 适配单会话模式 |
| `Emuera/UI/Game/EmueraConsole.cs` | 中 | Timer 条件编译 + 暴露 `InputTimeoutMs` |

### 10.3 可删除的文件

| 文件 | 原因 |
|------|------|
| `Emuera/Server/SessionManager.cs` | 单会话模式不需要 |

---

## 十一、验证标准

### 11.1 编译验证

```bash
# 无头项目编译通过
dotnet build Emuera.Headless/Emuera.Headless.csproj -c Debug

# WinForms 项目不受影响
dotnet build Emuera/Emuera.csproj -c Debug-NAudio
```

### 11.2 自动化测试（管道模式）

项目 `tests/` 目录下已有自动化测试脚本，基于 `test_game/` 最小游戏目录，使用 `EmueraAgent` 封装类驱动 Headless 进程。

**测试游戏**（`test_game/erb/TEST.ERB`）：3 轮交互流程，覆盖 WaitInput → 输入 → WaitInput → 输入 → Quit。

**运行方式**：

```bash
# 默认：自动查找 Emuera.Headless.exe，使用 test_game 目录
python tests/test_jsonl.py
python tests/test_buttons.py

# 指定二进制路径
python tests/test_jsonl.py --binary path/to/Emuera.Headless.exe

# 指定游戏目录
python tests/test_jsonl.py --game-dir path/to/game

# 通过环境变量指定二进制
set EMUERA_BINARY=path/to/Emuera.Headless.exe
python tests/test_jsonl.py
```

**测试脚本说明**：

| 脚本 | 验证内容 |
|------|---------|
| `test_jsonl.py` | JSONL 协议基本流程：初始回合输出、多轮输入/输出、状态转换（WaitInput → Quit）、文本内容正确性 |
| `test_buttons.py` | 按钮字段结构：每个按钮包含 label + value、value 类型为整数、按钮标签与游戏输出一致 |

**改造前基线**（当前版本测试结果）：

```
test_jsonl.py:   10 passed, 0 failed
test_buttons.py: 18 passed, 0 failed
```

**改造后验证**：所有测试必须全部通过，且无需修改测试脚本本身（测试脚本不依赖内部线程模型）。

### 11.3 手动验证（服务器模式）

```bash
# 启动服务器
Emuera.Headless.exe --server --port 8080

# 创建会话
curl -X POST http://localhost:8080/sessions
# → 返回会话 ID

# 提交输入
curl -X POST http://localhost:8080/sessions/{id}/input -d '{"value":"0"}'

# 获取回合
curl http://localhost:8080/sessions/{id}/turn

# 第二次创建会话应返回 409
curl -X POST http://localhost:8080/sessions
# → 409 Conflict
```

### 11.4 回归验证

```bash
# WinForms 版本不受影响
Emuera.exe
# → 正常出现窗口，游戏功能正常
```

---

## 十二、不做的事情

| 不做 | 原因 |
|------|------|
| 修改 `Process.DoScript()` 或 Runtime 层核心逻辑 | 游戏引擎本身不需要改动 |
| 实现多会话服务器模式 | 先验证单线程模型可行，多会话是后续工作 |
| 修改 WinForms 版本的任何行为 | 所有改动通过条件编译隔离 |
| 跨平台编译 | 仍依赖 System.Drawing，后续 Phase |
| 实现 WebSocket | HTTP 长轮询足够，后续升级 |

---

## 十三、风险与回退

| 风险 | 缓解 |
|------|------|
| TINPUT 超时精度从 ~10ms 降为 ~100ms | 对游戏逻辑无影响，超时精度不需要很高 |
| `PressEnterKey` 内部逻辑依赖 Timer 状态 | 通过条件编译禁用 Timer，`stopTimer()` 对已停止的 Timer 无副作用 |
| CLI 模式下无法同时等待按键和超时 | 用 `Console.KeyAvailable` + `Thread.Sleep` 轮询，与当前行为一致 |
| 改动范围较大 | 逐文件修改，每步编译验证；回退方案是 git revert |

**回退方案**：如果单线程化出现问题，直接 revert 所有改动，原有 WinForms 版本不受任何影响。
