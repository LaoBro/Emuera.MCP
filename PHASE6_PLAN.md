# Phase 6: 服务器模式 — TCP/HTTP 接口与多会话管理

> 目标：在 Phase 5 无头模式验证通过的基础上，添加网络层，使外部进程可通过 TCP/HTTP 与 Emuera 交互，支持多会话隔离。复用 `AgentJsonlProtocol` 的序列化逻辑，每个会话对应独立的 `EmueraConsole` 实例。

---

## 6.1 现状分析

### 6.1.1 Phase 5 已完成的成果

- `EmueraConsole` 完全通过 `IConsoleUI` 交互，无直接 WinForms 依赖
- `HeadlessConsole` 提供 `IConsoleUI` 空实现
- `AgentJsonlProtocol` 已解耦 `MainWindow`，通过 `IConsoleUI` 操作
- `Program.cs` 支持 `--headless` 参数，单进程单会话的 stdin/stdout JSONL 交互已跑通
- `EmueraConsole` 内部通过 `DetectAndRun` 自动检测环境并启动协议线程

### 6.1.2 当前架构限制

| 限制 | 说明 |
|------|------|
| 单进程单会话 | `RunHeadless` 只创建一个 `EmueraConsole`，无法同时服务多个客户端 |
| stdin/stdout 独占 | `AgentJsonlProtocol` 直接读写 `Console.ReadLine` / `Console.WriteLine`，无法同时支持多个流 |
| 无网络层 | 不存在任何 `HttpListener`、`TcpListener` 或 Socket 代码 |
| 生命周期耦合 | `EmueraConsole` 的初始化与协议启动在构造函数/Initialize 中耦合，不利于外部控制 |

### 6.1.3 Phase 6 需要引入的能力

1. **网络监听**：HTTP 或 TCP 端口监听，接受外部连接
2. **会话隔离**：每个客户端有独立的 `EmueraConsole` + `HeadlessConsole` + 协议状态
3. **协议复用**：复用 `AgentJsonlProtocol` 的 `BuildTurn()` / `DispatchInput()` 逻辑，但替换 IO 层
4. **生命周期管理**：会话创建、心跳、超时、销毁
5. **入口参数**：`--server --port 8080` 等参数解析

---

## 6.2 架构设计

### 6.2.1 总体架构

```
Program.Main(args)
  → InitializeCore(args)
  → 若 --server:
      → new GameServer(port)
      → server.Start()
      → 主线程阻塞/等待退出信号

GameServer (TcpListener 或 HttpListener)
  ├── 监听端口
  ├── 收到连接/请求
  │     → SessionManager.CreateSession() → new Session()
  │     → Session 内部: new HeadlessConsole() + new EmueraConsole(ui)
  │     → Session.Initialize()
  │     → 返回 sessionId
  ├── HTTP API:
  │     POST /sessions          → 创建会话
  │     GET  /sessions/{id}     → 查询会话状态
  │     POST /sessions/{id}/input → 提交输入
  │     DELETE /sessions/{id}   → 销毁会话
  └── WebSocket / TCP Stream:
        → 建立长连接后按 JSONL 推送回合状态
```

### 6.2.2 与现有代码的集成点

```
AgentJsonlProtocol (现有)
  ├── 保留: BuildTurn(), WaitForInput(), CollectVisibleButtons(), DispatchInput()
  ├── 替换: ReadStdinLoop / Console.WriteLine → 改为从 Session 的输入队列读取
  └── 新增: AgentJsonlProtocol(EmueraConsole, IConsoleUI, SessionIO io)

SessionIO (抽象)
  ├── string ReadLine()          // 阻塞读取客户端输入
  ├── void WriteLine(string)     // 向客户端输出
  └── void Close()               // 关闭连接

Session (新增)
  ├── EmueraConsole console
  ├── AgentJsonlProtocol protocol
  ├── SessionIO io
  └── Thread gameThread          // 每个会话独立游戏线程
```

---

## 6.3 修改方案

### 6.3.1 新增 `SessionIO` 抽象（Phase 6a）

**文件：** `Emuera/Server/SessionIO.cs`

```csharp
namespace MinorShift.Emuera.Server;

/// <summary>
/// 替换 Console.ReadLine/WriteLine 的抽象，使 AgentJsonlProtocol 不依赖全局 stdin/stdout。
/// </summary>
internal abstract class SessionIO
{
    public abstract string? ReadLine();
    public abstract void WriteLine(string text);
    public abstract void Close();
    public abstract bool IsConnected { get; }
}
```

**验收：**
- 编译通过
- `AgentJsonlProtocol` 尚未改动，不影响现有功能

---

### 6.3.2 改造 `AgentJsonlProtocol` 支持注入 IO（Phase 6b）

**文件：** `Emuera/UI/Game/AgentJsonlProtocol.cs`

**Step 6b.1：添加 `SessionIO` 字段与构造函数重载**

```csharp
private readonly SessionIO? _io;

// 原有构造函数保留（stdin/stdout 模式）
public AgentJsonlProtocol(EmueraConsole console, IConsoleUI ui)
    : this(console, ui, null) { }

// 新增构造函数（服务器模式注入 IO）
public AgentJsonlProtocol(EmueraConsole console, IConsoleUI ui, SessionIO io)
    : base(console, ui)
{
    _io = io;
    int clientHeight = ui.ClientHeight;
    _visibleLineCount = Math.Max(1, clientHeight / Config.LineHeight);
}
```

**Step 6b.2：替换 `Console.ReadLine` / `Console.WriteLine` 为 `_io` 调用**

```csharp
private void HandleMessage(string line)
{
    // ... 原有逻辑不变 ...
    if (turn != null)
        (_io ?? ConsoleOutIO.Instance).WriteLine(turn);
}

private void ReadStdinLoop(Action<string> onLine)
{
    var io = _io ?? ConsoleOutIO.Instance;
    while (!IsStopped && io.IsConnected)
    {
        string? line = io.ReadLine();
        if (line == null) break;
        onLine(line);
    }
    if (!IsStopped)
        ui.Invoke(() => ui.Close());
}
```

**Step 6b.3：新增 `ConsoleOutIO` 包装现有 stdin/stdout**

```csharp
namespace MinorShift.Emuera.Server;

internal sealed class ConsoleOutIO : SessionIO
{
    public static readonly ConsoleOutIO Instance = new();
    private ConsoleOutIO() { }

    public override string? ReadLine() => Console.ReadLine();
    public override void WriteLine(string text) => Console.WriteLine(text);
    public override void Close() { }
    public override bool IsConnected => true;
}
```

**验收：**
- `dotnet build -c Debug-NAudio` 编译通过
- `echo '{"type":"input","value":""}' | Emuera.exe --headless` 行为不变

---

### 6.3.3 新增 `Session` 与会话管理（Phase 6c）

**文件：** `Emuera/Server/Session.cs`

```csharp
using System;
using System.Threading;
using System.Threading.Tasks;
using MinorShift.Emuera.GameView;
using MinorShift.Emuera.UI.Game;

namespace MinorShift.Emuera.Server;

internal sealed class Session : IDisposable
{
    public string Id { get; } = Guid.NewGuid().ToString("N")[..8];
    public DateTimeOffset CreatedAt { get; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastActivityAt { get; private set; }
    public bool IsRunning => _gameThread?.IsAlive == true;

    private readonly HeadlessConsole _ui;
    private readonly EmueraConsole _console;
    private readonly AgentJsonlProtocol _protocol;
    private readonly SessionIO _io;
    private Thread? _gameThread;
    private readonly CancellationTokenSource _cts = new();

    public Session(SessionIO io)
    {
        _io = io;
        _ui = new HeadlessConsole();
        _console = new EmueraConsole(_ui);
        _protocol = new AgentJsonlProtocol(_console, _ui, io);
    }

    public void Start()
    {
        _gameThread = new Thread(GameLoop)
        {
            IsBackground = true,
            Name = $"Session-{Id}"
        };
        _gameThread.Start();
    }

    private void GameLoop()
    {
        try
        {
            _console.Initialize().Wait();
            _protocol.Run();

            // 保持会话运行直到协议停止或 IO 断开
            while (!_cts.IsCancellationRequested && !_protocol.IsStopped && _io.IsConnected)
            {
                Thread.Sleep(100);
            }
        }
        catch (Exception ex)
        {
            _io.WriteLine($"{{\"error\":\"{ex.Message}\",\"state\":\"Error\"}}");
        }
        finally
        {
            _protocol.Stop();
            _console.Dispose();
        }
    }

    public void Touch() => LastActivityAt = DateTimeOffset.UtcNow;

    public void Dispose()
    {
        _cts.Cancel();
        _protocol.Stop();
        _io.Close();
        _console.Dispose();
    }
}
```

**文件：** `Emuera/Server/SessionManager.cs`

```csharp
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;

namespace MinorShift.Emuera.Server;

internal sealed class SessionManager : IDisposable
{
    private readonly ConcurrentDictionary<string, Session> _sessions = new();
    private readonly Timer _cleanupTimer;
    private readonly TimeSpan _idleTimeout;

    public SessionManager(TimeSpan? idleTimeout = null)
    {
        _idleTimeout = idleTimeout ?? TimeSpan.FromMinutes(30);
        _cleanupTimer = new Timer(CleanupIdleSessions, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    public Session Create(SessionIO io)
    {
        var session = new Session(io);
        if (!_sessions.TryAdd(session.Id, session))
            throw new InvalidOperationException("Session ID collision");
        session.Start();
        return session;
    }

    public Session? Get(string id) => _sessions.TryGetValue(id, out var s) ? s : null;

    public bool Remove(string id)
    {
        if (_sessions.TryRemove(id, out var s))
        {
            s.Dispose();
            return true;
        }
        return false;
    }

    private void CleanupIdleSessions(object? state)
    {
        var cutoff = DateTimeOffset.UtcNow - _idleTimeout;
        foreach (var kv in _sessions.Where(kv => kv.Value.LastActivityAt < cutoff).ToList())
        {
            Remove(kv.Key);
        }
    }

    public void Dispose()
    {
        _cleanupTimer.Dispose();
        foreach (var id in _sessions.Keys.ToList())
            Remove(id);
    }
}
```

**验收：**
- 编译通过
- 单元测试：临时写一段代码验证 `SessionManager.Create` 能创建会话且不抛异常

---

### 6.3.4 新增 HTTP 服务器（Phase 6d）

**文件：** `Emuera/Server/HttpGameServer.cs`

使用 `HttpListener`（.NET 内置，无需额外依赖）：

```csharp
using System;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MinorShift.Emuera.Server;

internal sealed class HttpGameServer : IDisposable
{
    private readonly HttpListener _listener;
    private readonly SessionManager _sessions;
    private readonly CancellationTokenSource _cts = new();

    public HttpGameServer(int port)
    {
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://+:{port}/");
        _sessions = new SessionManager();
    }

    public void Start()
    {
        _listener.Start();
        Console.Error.WriteLine($"[server] HTTP 监听已启动于端口 {_listener.Prefixes.First()}");
        _ = Task.Run(RunLoop);
    }

    private async Task RunLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            var context = await _listener.GetContextAsync();
            _ = Task.Run(() => HandleRequest(context));
        }
    }

    private void HandleRequest(HttpListenerContext ctx)
    {
        var req = ctx.Request;
        var resp = ctx.Response;
        try
        {
            var path = req.Url?.AbsolutePath ?? "/";
            var method = req.HttpMethod;

            if (method == "POST" && path == "/sessions")
            {
                // 创建会话
                var io = new HttpSessionIO(resp);
                var session = _sessions.Create(io);
                io.SessionId = session.Id;
                WriteJson(resp, 201, new { sessionId = session.Id, createdAt = session.CreatedAt });
                return;
            }

            if (method == "GET" && path.StartsWith("/sessions/"))
            {
                var id = path.Split('/')[2];
                var session = _sessions.Get(id);
                if (session == null)
                {
                    WriteJson(resp, 404, new { error = "Session not found" });
                    return;
                }
                WriteJson(resp, 200, new
                {
                    sessionId = id,
                    isRunning = session.IsRunning,
                    lastActivity = session.LastActivityAt
                });
                return;
            }

            if (method == "POST" && path.StartsWith("/sessions/") && path.EndsWith("/input"))
            {
                var id = path.Split('/')[2];
                var session = _sessions.Get(id);
                if (session == null)
                {
                    WriteJson(resp, 404, new { error = "Session not found" });
                    return;
                }
                // TODO: 通过 HttpSessionIO 将输入投递到对应会话
                WriteJson(resp, 200, new { received = true });
                return;
            }

            if (method == "DELETE" && path.StartsWith("/sessions/"))
            {
                var id = path.Split('/')[2];
                var removed = _sessions.Remove(id);
                WriteJson(resp, removed ? 200 : 404, new { removed });
                return;
            }

            WriteJson(resp, 404, new { error = "Not found" });
        }
        catch (Exception ex)
        {
            WriteJson(resp, 500, new { error = ex.Message });
        }
    }

    private static void WriteJson(HttpListenerResponse resp, int status, object obj)
    {
        resp.StatusCode = status;
        resp.ContentType = "application/json; charset=utf-8";
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(obj));
        resp.OutputStream.Write(bytes, 0, bytes.Length);
        resp.Close();
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Close();
        _sessions.Dispose();
    }
}
```

> **注意：** `HttpListener` 在 Windows 上需要管理员权限注册 URL 前缀，或使用 `netsh http add urlacl`。开发阶段可用 `http://localhost:PORT/` 避免权限问题。生产环境可改用裸 TCP + 自定义轻量 HTTP 解析器。

**验收：**
- 编译通过
- `curl http://localhost:8080/sessions` 返回 404（尚未实现 GET /sessions 列表）或正确路由

---

### 6.3.5 `HttpSessionIO` 实现（Phase 6e）

HTTP 是无状态的，但我们需要把输入投递到正在运行的会话。设计两种模式：

**模式 A：长轮询（Long Polling）**
- 客户端 POST /sessions/{id}/input 提交输入
- 服务器将输入放入队列，会话游戏线程消费
- 客户端 GET /sessions/{id}/turn 阻塞等待下一回合输出

**模式 B：WebSocket（升级连接）**
- 客户端连接后保持全双工，按 JSONL 推送

Phase 6 先实现 **模式 A**，WebSocket 放入 TODO。

**文件：** `Emuera/Server/HttpSessionIO.cs`

```csharp
using System;
using System.Collections.Concurrent;
using System.Threading;

namespace MinorShift.Emuera.Server;

/// <summary>
/// 基于内存队列的 SessionIO，用于 HTTP 长轮询模式。
/// </summary>
internal sealed class HttpSessionIO : SessionIO
{
    private readonly ConcurrentQueue<string> _inputQueue = new();
    private readonly ConcurrentQueue<string> _outputQueue = new();
    private readonly AutoResetEvent _inputEvent = new(false);
    private volatile bool _connected = true;

    public string? SessionId { get; set; }

    public override string? ReadLine()
    {
        while (_connected)
        {
            if (_inputQueue.TryDequeue(out var line))
                return line;
            _inputEvent.WaitOne(100);
        }
        return null;
    }

    public override void WriteLine(string text)
    {
        if (_connected)
            _outputQueue.Enqueue(text);
    }

    public override void Close() => _connected = false;
    public override bool IsConnected => _connected;

    public void EnqueueInput(string line)
    {
        _inputQueue.Enqueue(line);
        _inputEvent.Set();
    }

    public bool TryDequeueOutput(out string? text) => _outputQueue.TryDequeue(out text);
}
```

**验收：**
- 编译通过
- 临时测试：`HttpSessionIO` 的 `EnqueueInput` / `ReadLine` 能正确传递消息

---

### 6.3.6 `Program.cs` 入口改造（Phase 6f）

**文件：** `Emuera/Program.cs`

**Step 6f.1：添加 `--server` 和 `--port` 参数**

```csharp
static readonly Option<bool> serverOption = new(
    name: "--server",
    description: "服务器模式：通过 HTTP 接口提供多会话服务"
);
static readonly Option<int> portOption = new(
    name: "--port",
    description: "服务器监听端口",
    getDefaultValue: () => 8080
);

// 在 rootCommand 中添加
rootCommand.AddOption(serverOption);
rootCommand.AddOption(portOption);
```

**Step 6f.2：主分支逻辑**

```csharp
var server = result.GetValueForOption(serverOption);
var port = result.GetValueForOption(portOption);

if (server)
{
    RunServer(port);
}
else if (headless)
{
    RunHeadless(args);
}
else
{
    RunWinForms(args, icon);
}
```

**Step 6f.3：新增 `RunServer`**

```csharp
private static void RunServer(int port)
{
    AnalysisMode = false;
    Console.Error.WriteLine($"[server] Emuera {AssemblyData.EmueraVersionText} 服务器模式启动");
    Console.Error.WriteLine($"[server] 监听端口: {port}");

    using var server = new Server.HttpGameServer(port);
    server.Start();

    Console.Error.WriteLine("[server] 按 Enter 键停止服务器...");
    Console.ReadLine();
}
```

**验收：**
- `Emuera.exe --server --port 8080` 启动后输出监听日志
- `curl http://localhost:8080/sessions -X POST` 返回 sessionId

---

### 6.3.7 HTTP 路由完善与输入投递（Phase 6g）

**文件：** `Emuera/Server/HttpGameServer.cs`

完善 `/sessions/{id}/input` 和 `/sessions/{id}/turn`：

```csharp
// POST /sessions/{id}/input
if (method == "POST" && path.StartsWith("/sessions/") && path.EndsWith("/input"))
{
    var id = path.Split('/')[2];
    if (!_httpIOMap.TryGetValue(id, out var io))
    {
        WriteJson(resp, 404, new { error = "Session not found" });
        return;
    }
    using var reader = new StreamReader(req.InputStream);
    var body = reader.ReadToEnd();
    io.EnqueueInput(body);
    WriteJson(resp, 200, new { received = true });
    return;
}

// GET /sessions/{id}/turn（长轮询）
if (method == "GET" && path.StartsWith("/sessions/") && path.EndsWith("/turn"))
{
    var id = path.Split('/')[2];
    if (!_httpIOMap.TryGetValue(id, out var io))
    {
        WriteJson(resp, 404, new { error = "Session not found" });
        return;
    }
    // 阻塞等待输出，最多 25 秒（浏览器超时前）
    var sw = Stopwatch.StartNew();
    while (sw.Elapsed < TimeSpan.FromSeconds(25))
    {
        if (io.TryDequeueOutput(out var turn) && turn != null)
        {
            resp.ContentType = "application/json; charset=utf-8";
            var bytes = Encoding.UTF8.GetBytes(turn);
            resp.OutputStream.Write(bytes, 0, bytes.Length);
            resp.Close();
            return;
        }
        Thread.Sleep(50);
    }
    // 超时返回空对象，客户端应再次轮询
    WriteJson(resp, 204, new { });
    return;
}
```

同时需要在 `SessionManager.Create` 时注册 `HttpSessionIO` 到映射表，或在 `HttpGameServer` 内部维护 `_httpIOMap`。

**验收：**
- `curl -X POST -d '{"type":"input","value":""}' http://localhost:8080/sessions/{id}/input` 成功
- `curl http://localhost:8080/sessions/{id}/turn` 能阻塞并返回回合 JSON

---

## 6.4 修改步骤汇总

### Phase 6a: `SessionIO` 抽象
- [ ] **Step 6a.1** 新建 `Emuera/Server/SessionIO.cs`
- [ ] **Step 6a.2** 新建 `Emuera/Server/ConsoleOutIO.cs`
- [ ] **Step 6a.3** 编译通过

### Phase 6b: `AgentJsonlProtocol` IO 解耦
- [ ] **Step 6b.1** 添加 `SessionIO` 字段与构造函数重载
- [ ] **Step 6b.2** 替换 `Console.ReadLine` / `Console.WriteLine` 为 `_io` 调用
- [ ] **Step 6b.3** `--headless` 管道模式回归测试通过

### Phase 6c: `Session` 与会话管理
- [ ] **Step 6c.1** 新建 `Emuera/Server/Session.cs`
- [ ] **Step 6c.2** 新建 `Emuera/Server/SessionManager.cs`
- [ ] **Step 6c.3** 编译通过 + 临时单元测试

### Phase 6d: HTTP 服务器骨架
- [ ] **Step 6d.1** 新建 `Emuera/Server/HttpGameServer.cs`
- [ ] **Step 6d.2** 实现 POST /sessions、GET /sessions/{id}、DELETE /sessions/{id}
- [ ] **Step 6d.3** 编译通过 + curl 测试路由

### Phase 6e: `HttpSessionIO` 实现
- [ ] **Step 6e.1** 新建 `Emuera/Server/HttpSessionIO.cs`
- [ ] **Step 6e.2** 实现输入队列与输出队列
- [ ] **Step 6e.3** 编译通过 + 临时单元测试

### Phase 6f: `Program.cs` 入口改造
- [ ] **Step 6f.1** 添加 `--server`、`--port` 参数
- [ ] **Step 6f.2** 主分支添加 `RunServer`
- [ ] **Step 6f.3** `Emuera.exe --server --port 8080` 能启动并监听

### Phase 6g: HTTP 路由完善与输入投递
- [ ] **Step 6g.1** 完善 POST /sessions/{id}/input
- [ ] **Step 6g.2** 实现 GET /sessions/{id}/turn 长轮询
- [ ] **Step 6g.3** 端到端测试：创建会话 → 提交输入 → 获取回合

### Phase 6h: 测试与验证
- [ ] **Step 6h.1** 单会话端到端测试（curl 序列）
- [ ] **Step 6h.2** 多会话隔离测试（两个 sessionId 同时游戏）
- [ ] **Step 6h.3** 空闲超时清理测试
- [ ] **Step 6h.4** WinForms 模式回归测试
- [ ] **Step 6h.5** `--headless` 管道模式回归测试

---

## 6.5 验收标准

### Phase 6a-6c 验收
```bash
dotnet build -c Debug-NAudio
# → 编译通过
```

### Phase 6d-6g 验收
```bash
# 1. 启动服务器
Emuera.exe --server --port 8080
# → stderr 输出 [server] 监听日志

# 2. 创建会话
curl -X POST http://localhost:8080/sessions
# → {"sessionId":"abc12345","createdAt":"..."}

# 3. 查询会话状态
curl http://localhost:8080/sessions/abc12345
# → {"sessionId":"abc12345","isRunning":true,...}

# 4. 提交输入（后台投递）
curl -X POST -d '{"type":"input","value":""}' http://localhost:8080/sessions/abc12345/input
# → {"received":true}

# 5. 获取回合（长轮询，阻塞直到有输出）
curl http://localhost:8080/sessions/abc12345/turn
# → {"text":"...","state":"WaitInput","inputType":"...","needValue":false,"buttons":[...]}
```

### Phase 6h 整体验收
```bash
# 1. 多会话隔离
id1=$(curl -s -X POST http://localhost:8080/sessions | jq -r .sessionId)
id2=$(curl -s -X POST http://localhost:8080/sessions | jq -r .sessionId)
# 向 id1 和 id2 发送不同输入，确认输出互不影响

# 2. 空闲超时
# 等待 30 分钟以上（或调低超时配置），确认未活动会话被自动清理
# curl http://localhost:8080/sessions/OLD_ID → 404

# 3. 回归测试
Emuera.exe
# → WinForms 模式正常

echo '{"type":"input","value":""}' | Emuera.exe --headless
# → 管道模式正常
```

---

## 6.6 风险与缓解

| 风险 | 影响 | 缓解 |
|------|------|------|
| `HttpListener` 需要管理员权限 | 中 | 开发用 `localhost` 前缀；生产环境改用裸 TCP + 轻量 HTTP 解析器 |
| 多线程 `EmueraConsole` 并发问题 | 高 | 每个 `Session` 有独立的 `EmueraConsole` 实例，不共享状态；但 `Config` / `GlobalStatic` 是全局的，需确认脚本层是否依赖可变全局状态 |
| 内存泄漏（会话未销毁） | 中 | `SessionManager` 空闲超时清理 + 客户端 DELETE / 进程退出时 Dispose |
| `AgentJsonlProtocol` 的 `ui.Invoke` 在服务器模式下行为 | 低 | `HeadlessConsole.Invoke` 同步执行，无消息泵依赖，安全 |
| 游戏脚本依赖 `System.Windows.Forms` | 低 | 脚本层（GameProc）不应直接引用 WinForms；若存在，需在 Phase 6c 审查中发现 |
| 长轮询性能 | 低 | 每个 turn 一个 HTTP 请求，开销可接受；未来可升级为 WebSocket |

---

## 6.7 与后续 Phase 的衔接

- **Phase 7**：Python 网关 `mcp_relay.py` 可通过 HTTP API 与 Emuera 服务器交互，替代直接进程管理
  - `mcp_relay.py` 内部维护 sessionId，通过 `POST /sessions/{id}/input` + `GET /sessions/{id}/turn` 驱动游戏
  - 多用户场景下，Python 层负责将 MCP 客户端映射到不同 sessionId
- **TODO 冻结项**：WebSocket 实时推送、跨平台编译（Linux/macOS）

---

## 6.8 检查清单

- [ ] `SessionIO.cs` 抽象完成
- [ ] `ConsoleOutIO.cs` 包装完成
- [ ] `AgentJsonlProtocol` 支持注入 IO
- [ ] `Session.cs` 单会话生命周期管理
- [ ] `SessionManager.cs` 多会话管理与空闲清理
- [ ] `HttpGameServer.cs` HTTP 路由实现
- [ ] `HttpSessionIO.cs` 队列 IO 实现
- [ ] `Program.cs` 添加 `--server`、`--port` 参数与 `RunServer`
- [ ] 单会话端到端测试通过
- [ ] 多会话隔离测试通过
- [ ] WinForms 模式回归测试通过
- [ ] `--headless` 管道模式回归测试通过
