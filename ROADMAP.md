# Emuera 跨平台后端服务器改造路线图

> 目标：剥离 WinForms，改为跨平台后端服务器，原有 WinForms 窗体通过桥接作为可选前端，同时提供 MCP/JSONL 协议的无头模式。图片功能暂时忽略，以最小修改实现，复杂逻辑可转移到 Python 中，为以后添加 Web 前端保留接口。

---

## 总体原则

- **绝不同时修改多个层**：UI 层、协议层、业务层每次只动一层
- **向后兼容**：每个阶段完成后，原有 WinForms 模式必须仍能正常运行
- **验收标准明确**：每个阶段都有可执行的验证命令或测试场景
- **代码未动，文档先行**：每阶段开始前先确认接口契约
- **跨平台编译和 Web 前端暂不实施**，放入 TODO 冻结
- **编译环境约束**：非 NAudio 配置含 `COMReference`（WMPLib），不支持 `dotnet build`，需用 MSBuild；日常开发使用 `dotnet build -c Debug-NAudio`

---

## 当前架构

```
Program.cs ──→ MainWindow (WinForms) ──→ EmueraConsole (核心控制器)
                                              │
                                              ├──→ GameProc.Process (脚本引擎/业务逻辑)
                                              ├──→ AgentProtocolBase (协议桥接层)
                                              │         ├──→ AgentJsonlProtocol (JSONL无头模式)
                                              │         └──→ AgentCliProtocol (CLI交互模式)
                                              └──→ 渲染/输入系统 (WinForms依赖)
```

---

## Phase 1: UI 抽象层 — 创建 `IConsoleUI` 接口

**目标**：定义跨平台 UI 契约，不改动任何现有代码逻辑。

### 新增文件

| 文件 | 说明 |
|------|------|
| `Emuera/UI/Game/IConsoleUI.cs` | 纯接口定义 |
| `Emuera/UI/Game/HeadlessConsole.cs` | 无头空实现 |
| `Emuera/UI/Game/WinFormsConsole.cs` | 对现有 MainWindow 的适配器（可选） |

### 接口最小成员集（基于 `EmueraConsole.cs` 中所有 `window.` 引用扫描）

- 生命周期：`Created`, `Close()`, `Reboot()`
- 渲染触发：`Refresh()`, `Invoke(Action)`
- 尺寸查询：`ClientWidth`, `ClientHeight`
- 输入辅助：`UpdateLastInput()`, `ResetTextBoxPos()`
- 滚动条：`ScrollBar`（子接口 `IScrollBar`）
- 标题/文本框：`Text`, `TextBox`（子接口 `ITextBox`）
- 工具提示：`ToolTip`（子接口 `IToolTip`，可空实现）
- 鼠标位置：`GetMousePosition()`
- 应用退出：`ExitApplication()`
- 激活状态：`IsActive`

### 验收标准

```bash
# 1. 编译通过（NAudio 配置，因非 NAudio 配置含 COMReference 不支持 dotnet build）
dotnet build Emuera/Emuera.csproj -c Debug-NAudio

# 2. WinForms 模式正常运行（未引入任何行为变更）
Emuera.exe

# 3. 接口可被实例化（单元测试或临时入口）
# 临时写一段代码验证 HeadlessConsole 能创建且属性不抛异常
```

### 风险与缓解

| 风险 | 缓解措施 |
|------|---------|
| 接口设计遗漏关键成员 | 基于 `EmueraConsole.cs` 中所有 `window.` 引用完整扫描后设计 |

---

## Phase 2: 协议层解耦 — `AgentProtocolBase` 移除 `MainWindow` 强依赖

**目标**：让 JSONL/CLI 协议在无窗体环境下也能初始化。

### 修改文件

| 文件 | 改动内容 |
|------|---------|
| `Emuera/UI/Game/AgentProtocolBase.cs` | 构造函数改为接受 `IConsoleUI` |
| `Emuera/UI/Game/AgentJsonlProtocol.cs` | 移除 `window.MainPicBox.Height` 直接引用；`window.Invoke` 改为 `ui?.Invoke()` |
| `Emuera/UI/Game/AgentCliProtocol.cs` | 同上 |

### 关键改动点

- 构造函数：`AgentProtocolBase(EmueraConsole console, MainWindow window)` → `(EmueraConsole console, IConsoleUI ui)`
- 所有 `window.Invoke()` / `window.BeginInvoke()` 改为 `ui?.Invoke()`（`?.` 允许 null，同步执行）
- `AgentJsonlProtocol` 构造时的 `window.MainPicBox.Height` 改为 `ui?.ClientHeight ?? Config.WindowY`

### 验收标准

```bash
# 1. 编译通过（NAudio 配置）
dotnet build -c Debug-NAudio

# 2. 原有管道模式仍正常工作
# 通过 mcp_relay.py 或手动 pipe 启动，JSONL 交互无异常

# 3. （预备验证）临时将 DetectAndRun 中的 window 传 null，
#    确认 AgentJsonlProtocol 在 null window 下不抛 NullReferenceException
```

---

## Phase 3: 入口改造 — `Program.cs` 支持 `--headless` 参数

**目标**：应用能根据参数选择启动模式，但 `--headless` 暂时走不到完整流程。

### 修改文件

| 文件 | 改动内容 |
|------|---------|
| `Emuera/Program.cs` | 解析 `args`，支持 `--headless` / `--server` 参数 |

### 新增文件

| 文件 | 说明 |
|------|------|
| `Emuera/HeadlessEntry.cs` | 无头模式临时入口桩 |

### HeadlessEntry 当前行为

1. 初始化配置 (`Config.Load`)
2. 创建 `HeadlessConsole`
3. 尝试创建 `EmueraConsole`（此时会失败，因 `EmueraConsole` 仍依赖 WinForms，但这是预期内的）
4. 输出诊断信息并优雅退出

### 验收标准

```bash
# 1. WinForms 模式不变
Emuera.exe
# → 正常出现窗口

# 2. Headless 参数被识别
Emuera.exe --headless
# → 输出诊断日志，提示 "Headless mode stub active, EmueraConsole not yet decoupled"
# → 进程退出码 0

# 3. 其他参数不受影响
Emuera.exe --debug
# → 正常进入调试模式
```

---

## Phase 4: 核心控制器改造 — `EmueraConsole` 解耦 WinForms

**目标**：`EmueraConsole` 不再直接引用 `MainWindow`，改为通过 `IConsoleUI` 交互。

> 这是改动量最大的阶段，内部再分 4 个子阶段，每个子阶段独立验收。

### Phase 4a: 字段与属性替换

**修改内容**：
- `private readonly MainWindow window` → `private readonly IConsoleUI _ui`
- `public MainWindow Window` → `public IConsoleUI UI`
- 所有 `window.` 属性访问改为 `_ui.`

**验收**：编译通过 + WinForms 模式正常启动

### Phase 4b: `Invoke` 与消息循环解耦

**修改内容**：
- `window.Invoke(...)` → `_ui.Invoke(...)`
- `Application.DoEvents()` → 抽象为 `_ui.ProcessEvents()` 或条件编译
- `Application.Exit()` → 抽象为 `_ui.ExitApplication()`

**验收**：编译通过 + 按钮点击/输入响应正常

### Phase 4c: 输入与鼠标系统解耦

**修改内容**：
- `window.MainPicBox.PointToClient(...)` → `_ui.GetMousePosition()`
- `Control.MousePosition` / `Cursor.Position` → 通过接口获取
- `Form.ActiveForm` → 接口属性 `IsActive`

**验收**：编译通过 + 鼠标悬停/点击/宏功能正常

### Phase 4d: 绘图与定时器解耦

**修改内容**：
- `window.Refresh()` → `_ui.Refresh()`
- `window.ScrollBar` → `_ui.ScrollBar`
- `redrawTimer` (WinForms Timer) → `System.Timers.Timer`（已在用）或接口封装

**验收**：编译通过 + 画面刷新/滚动/定时器功能正常

### Phase 4 整体验收标准

```bash
# 1. 编译通过（NAudio 配置）
dotnet build -c Debug-NAudio

# 2. WinForms 模式完整功能测试
# - 正常游戏流程
# - 按钮点击、输入、宏、定时器
# - 调试窗口、配置对话框

# 3. --headless 参数测试
Emuera.exe --headless < test_input.txt
# → 能读取脚本、输出文本到 stdout
# → JSONL 协议正常交互
```

---

## Phase 5: 无头模式验收 — 纯控制台环境端到端验证

**目标**：不启动任何窗体，通过 stdin/stdout 完整运行游戏。

**修改文件**：无（修复 Phase 4 遗留问题）

### 验收标准

```bash
# 1. 纯管道模式运行
echo '{"type":"input","value":""}' | Emuera.exe --headless
# → 输出初始回合 JSON
# → 进程保持等待下一次输入

# 2. 多轮交互
python mcp_relay.py --mode jsonl
# → 能完整进行游戏对话

# 3. 与原有 WinForms 模式行为一致性对比
# - 相同输入产生相同输出
# - 按钮列表一致
# - 状态转换一致
```

---

## Phase 6: 服务器模式 — 添加 TCP/HTTP 接口

**目标**：支持外部进程通过网络连接，而非仅 stdin/stdout。复用 `AgentJsonlProtocol` 的序列化逻辑，每个会话对应独立的 `EmueraConsole` 实例。

> 详细子计划见 [`PHASE6_PLAN.md`](./PHASE6_PLAN.md)。

### 6.1 架构设计

```
Program.Main(args)
  → 若 --server:
      → new GameServer(port)
      → server.Start()
      → 主线程阻塞等待退出信号

GameServer (HttpListener)
  ├── 监听端口
  ├── 收到 HTTP 请求
  │     → SessionManager.CreateSession() → new Session()
  │     → Session 内部: new HeadlessConsole() + new EmueraConsole(ui)
  │     → Session.Initialize() + AgentJsonlProtocol.Run()
  │     → 返回 sessionId
  ├── HTTP API:
  │     POST   /sessions              → 创建会话
  │     GET    /sessions/{id}         → 查询会话状态
  │     POST   /sessions/{id}/input   → 提交输入（投递到会话输入队列）
  │     GET    /sessions/{id}/turn    → 长轮询获取下一回合 JSON
  │     DELETE /sessions/{id}         → 销毁会话
  └── 会话管理:
        → SessionManager 维护 ConcurrentDictionary<string, Session>
        → 空闲超时自动清理（默认 30 分钟）
```

### 6.2 新增文件

| 文件 | 说明 |
|------|------|
| `Emuera/Server/SessionIO.cs` | IO 抽象：替换 `Console.ReadLine` / `Console.WriteLine` |
| `Emuera/Server/ConsoleOutIO.cs` | `SessionIO` 的 stdin/stdout 实现（兼容现有 `--headless`） |
| `Emuera/Server/HttpSessionIO.cs` | `SessionIO` 的内存队列实现（用于 HTTP 会话） |
| `Emuera/Server/Session.cs` | 单个会话：独立 `EmueraConsole` + `AgentJsonlProtocol` + 游戏线程 |
| `Emuera/Server/SessionManager.cs` | 多会话管理、空闲超时清理 |
| `Emuera/Server/HttpGameServer.cs` | `HttpListener` 封装、HTTP 路由、长轮询 |

### 6.3 关键接口契约

#### `SessionIO`（抽象）
```csharp
internal abstract class SessionIO
{
    public abstract string? ReadLine();
    public abstract void WriteLine(string text);
    public abstract void Close();
    public abstract bool IsConnected { get; }
}
```

#### `AgentJsonlProtocol` 改造
- 新增构造函数：`AgentJsonlProtocol(EmueraConsole, IConsoleUI, SessionIO io)`
- 内部读写从 `Console.ReadLine/WriteLine` 改为注入的 `SessionIO`
- 原有构造函数保留，内部使用 `ConsoleOutIO.Instance`，不影响 `--headless` 管道模式

#### `Session` 生命周期
```csharp
internal sealed class Session : IDisposable
{
    public string Id { get; }
    public bool IsRunning { get; }
    public DateTimeOffset LastActivityAt { get; }
    public void Start();      // 启动游戏线程 + 协议线程
    public void Dispose();    // 停止协议、释放资源
}
```

#### `HttpGameServer` 路由
| 方法 | 路径 | 说明 |
|------|------|------|
| POST | `/sessions` | 创建新会话，返回 `{sessionId, createdAt}` |
| GET | `/sessions/{id}` | 查询会话状态，返回 `{sessionId, isRunning, lastActivity}` |
| POST | `/sessions/{id}/input` | 提交输入 JSON，投递到会话输入队列 |
| GET | `/sessions/{id}/turn` | 长轮询（最多 25s），返回回合 JSON 或 204 |
| DELETE | `/sessions/{id}` | 销毁会话，返回 `{removed}` |

### 6.4 修改文件

| 文件 | 改动内容 |
|------|---------|
| `Emuera/UI/Game/AgentJsonlProtocol.cs` | 支持注入 `SessionIO`，替换 `Console.ReadLine/WriteLine` |
| `Emuera/Program.cs` | 添加 `--server`、`--port` 参数；新增 `RunServer()` 入口 |

### 6.5 验收标准

```bash
# 1. 编译通过
dotnet build -c Debug-NAudio

# 2. 启动服务器
Emuera.exe --server --port 8080
# → stderr 输出 [server] 监听日志

# 3. 创建会话
SESSION=$(curl -s -X POST http://localhost:8080/sessions | jq -r .sessionId)

# 4. 提交输入
curl -X POST -d '{"type":"input","value":""}' http://localhost:8080/sessions/$SESSION/input

# 5. 获取回合（长轮询）
curl http://localhost:8080/sessions/$SESSION/turn
# → {"text":"...","state":"WaitInput","inputType":"...","needValue":false,"buttons":[...]}

# 6. 多会话隔离
SESSION2=$(curl -s -X POST http://localhost:8080/sessions | jq -r .sessionId)
# 向 SESSION 和 SESSION2 发送不同输入，确认输出互不影响

# 7. 回归测试
Emuera.exe
# → WinForms 模式正常

echo '{"type":"input","value":""}' | Emuera.exe --headless
# → 管道模式正常
```

### 6.6 风险与缓解

| 风险 | 影响 | 缓解 |
|------|------|------|
| `HttpListener` 需要管理员权限 | 中 | 开发用 `localhost` 前缀；生产环境改用裸 TCP |
| 多线程 `EmueraConsole` 并发 | 高 | 每个 Session 独立实例；`Config`/`GlobalStatic` 全局状态需审查 |
| 内存泄漏 | 中 | SessionManager 空闲超时清理 + 客户端 DELETE |
| 长轮询性能 | 低 | 每个 turn 一个 HTTP 请求，可接受；未来升级 WebSocket |

---

## Phase 7: Python 网关扩展 — `mcp_relay.py` 升级为完整协议网关

**目标**：Python 层承担协议转换、会话管理、复杂逻辑。

### 新增/修改文件

| 文件 | 说明 |
|------|------|
| `emuera_gateway/__init__.py` | 包入口 |
| `emuera_gateway/server.py` | HTTP/WebSocket 服务 |
| `emuera_gateway/mcp.py` | MCP 协议实现 |
| `emuera_gateway/session.py` | Emuera 进程池管理 |

### 职责划分

| 职责 | C# 端 | Python 端 |
|------|-------|----------|
| 脚本执行 | ✅ | |
| 状态管理 | ✅ | |
| JSONL 协议 | ✅ | |
| MCP 协议 | | ✅ |
| HTTP API | | ✅ |
| 多用户会话 | | ✅ |
| AI/图片处理 | | ✅ (预留) |

### 验收标准

```bash
# 1. Python 网关启动
python -m emuera_gateway --emuera-path ./Emuera.exe

# 2. MCP 客户端连接
# Claude Desktop / Cursor 等能识别并调用工具

# 3. HTTP API 可用
curl http://localhost:8000/api/sessions

# 4. 复杂逻辑扩展点验证
# 能无缝接入新 Python 模块而不修改 C# 代码
```

---

## TODO 冻结项

| 项 | 冻结原因 | 解冻条件 |
|----|---------|---------|
| 跨平台编译 (Linux/macOS) | 需替换 `System.Drawing`、WMP/NAudio、文件路径处理 | Phase 1-5 稳定后 |
| Web 前端接口 | 需先完成服务器模式和多会话管理 | Phase 6-7 稳定后 |

---

## 里程碑与回退策略

| 里程碑 | 回退方案 |
|--------|---------|
| Phase 1 接口设计不合理 | 废弃接口文件，不影响任何运行代码 |
| Phase 2 协议层改崩 | `git revert`，WinForms 模式不受影响 |
| Phase 4 解耦引入 Bug | 保留 `MainWindow` 备用字段，快速切回 |
| Phase 5 无头模式不可用 | 继续用原有管道模式 + mcp_relay.py |

---

## 贡献指南

- 每个 Phase 开始前，先在此文档中更新详细接口契约
- 每个子阶段完成后，更新此文档中的验收状态
- 发现接口设计遗漏时，回退到 Phase 1 补充，不临时打补丁
- 所有代码修改必须通过 WinForms 模式回归测试

---

## 变更日志

| 日期 | 版本 | 变更内容 |
|------|------|---------|
| 2026-06-05 | v0.1 | 初始路线图制定 |
