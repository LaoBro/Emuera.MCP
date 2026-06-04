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

**目标**：支持外部进程通过网络连接，而非仅 stdin/stdout。

### 新增文件

| 文件 | 说明 |
|------|------|
| `Emuera/Server/GameServer.cs` | TCP/HTTP 服务器（轻量实现，`HttpListener` 或裸 TCP） |
| `Emuera/Server/Session.cs` | 会话管理 |
| `Emuera/Server/GameProtocol.cs` | 基于 JSONL 的网络协议封装 |

### 接口设计（最小集）

```csharp
// POST /input  → 提交输入
// GET  /state  → 获取当前回合状态
// WebSocket /stream → 实时推送
```

### 与现有代码集成

- 复用 `AgentJsonlProtocol` 的轮询/序列化逻辑
- 每个连接对应一个 `EmueraConsole` 实例（或共享，视需求）

### 验收标准

```bash
# 1. 启动服务器
Emuera.exe --server --port 8080

# 2. HTTP 交互
curl http://localhost:8080/state
# → 返回 JSON 回合状态

curl -X POST -d '{"value":"1"}' http://localhost:8080/input
# → 推进游戏，返回新状态

# 3. 多客户端隔离（如已实现多会话）
# 每个客户端有独立游戏状态
```

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
