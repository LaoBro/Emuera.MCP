# Emuera Headless 协议注入、归属整理与 CLI 可测化规格书

> 版本：v1.1
> 日期：2026-06-11
> 配套实施计划：[PLAN.md](./PLAN.md)

---

## 1. 背景与动机

### 1.1 现状问题

当前代码存在三类耦合：

1. **协议创建耦合在 `EmueraConsole` 中**  
   `EmueraConsole` 构造函数会通过 `AgentProtocolBase.Detect(this, _uiAdapter)` 自动选择协议。协议选择本质上是入口点的运行模式决策，不应由核心 UI/游戏控制器负责。

2. **WinForms 项目承载了 headless/server 代码**  
   `Emuera/Program.cs` 中存在 `--headless`、`--server`、`--port` 等 headless/server 入口逻辑；`Emuera/UI/Game/Agent*.cs` 与 `Emuera/Server/*.cs` 也位于 WinForms 项目树下，但实际只应属于 headless/server 运行模式。

3. **CLI 协议不可稳定自动测试**  
   当前 CLI 协议依赖 `Console.KeyAvailable` 与 `Console.ReadKey(true)`，需要真实 TTY。CI/自动化测试通常只提供 redirected stdin/stdout，因此 CLI 协议无法像 JSONL 协议一样稳定测试。

### 1.2 目标

1. **协议创建权从 `EmueraConsole` 移到入口点**  
   `EmueraConsole` 不再自动检测或创建协议，改为由 `Emuera.Headless/Program.cs` 显式创建并通过 `SetAgentBridge()` 注入。

2. **headless/server/agent 协议代码归入 `Emuera.Headless` 项目**  
   `AgentProtocolBase`、`AgentJsonlProtocol`、`AgentCliProtocol` 以及 `Server/*` 相关文件移动到 `Emuera.Headless/` 下，WinForms 项目不再编译这些 headless 专属代码。

3. **删除 `Emuera.Headless` 不必要的 `--headless` 参数**  
   `Emuera.Headless.exe` 本身就是 headless 程序，不再需要 `--headless` 参数。

4. **为 `Emuera.Headless` 增加显式 `--protocol` 参数**  
   支持 `auto`（默认）、`jsonl`、`cli` 三种模式，测试可显式控制协议选择。

5. **让 CLI 协议支持 redirected stdin/stdout 自动测试**  
   CLI 协议在真实终端中继续使用 `Console.KeyAvailable` / `Console.ReadKey(true)`；在 redirected stdin 环境中改为读取 `Console.In.ReadLine()`，使自动化测试可以像 JSONL 一样通过 pipe 驱动。

6. **恢复 WinForms 项目为纯 GUI 入口**  
   删除 `Emuera/Program.cs` 中 headless/server 选项、方法和分发逻辑；`Dialog.cs` 恢复为纯 `MessageBox.Show`。

---

## 2. 非目标

- 不修改 JSONL 协议的业务格式、turn 字段和 Step 语义。
- 不修改 server HTTP API、Session 管理语义和已有 HTTP 请求/响应格式。
- 不删除 `AgentProtocolBase.Detect()` 方法；保留为 `--protocol auto` 的实现。
- 不修改 Runtime 层核心代码。
- 不修改 WinForms 窗体行为。
- 不要求 `Emuera.Headless.exe --headless` 继续兼容；该参数按本规格删除。
- 不要求 CLI 的真实终端交互模式改变；仅新增 redirected stdin 下的自动测试模式。

---

## 3. 改动范围

### 3.1 文件移动

| 当前路径 | 目标路径 | 说明 |
|---|---|---|
| `Emuera/UI/Game/AgentProtocolBase.cs` | `Emuera.Headless/Agent/AgentProtocolBase.cs` | headless agent 协议基类 |
| `Emuera/UI/Game/AgentJsonlProtocol.cs` | `Emuera.Headless/Agent/AgentJsonlProtocol.cs` | JSONL 协议实现 |
| `Emuera/UI/Game/AgentCliProtocol.cs` | `Emuera.Headless/Agent/AgentCliProtocol.cs` | CLI 协议实现 |
| `Emuera/Server/ConsoleOutIO.cs` | `Emuera.Headless/Server/ConsoleOutIO.cs` | server stdout IO |
| `Emuera/Server/HttpGameServer.cs` | `Emuera.Headless/Server/HttpGameServer.cs` | HTTP server |
| `Emuera/Server/HttpSessionIO.cs` | `Emuera.Headless/Server/HttpSessionIO.cs` | HTTP session IO |
| `Emuera/Server/Session.cs` | `Emuera.Headless/Server/Session.cs` | server session |
| `Emuera/Server/SessionIO.cs` | `Emuera.Headless/Server/SessionIO.cs` | session IO 接口 |

移动后保持命名空间不变：

- 协议类继续位于 `MinorShift.Emuera.GameView`。
- server 类继续位于 `MinorShift.Emuera.Server`。
- `JsonlCommand` 先保持全局记录类型，避免大范围引用调整。

### 3.2 文件修改

| # | 文件 | 项目 | 操作 | 说明 |
|---|------|------|------|------|
| 1 | `Emuera/UI/Game/EmueraConsole.cs` | 共享 | 修改 | 删除构造函数中的 `Detect()`；新增 `#if HEADLESS` 下的 `SetAgentBridge()` |
| 2 | `Emuera/UI/Game/EmueraConsole.AgentBridge.cs` | 共享 | 修改 | 在 HEADLESS 下将输出写入 `_agentBuffer`；新增 `WriteToAgentBuffer()` |
| 3 | `Emuera/UI/Game/AgentProtocolBase.cs` | Headless | 移动 + 文档更新 | 标注 `Detect()` 为 `--protocol auto` 的可选辅助方法 |
| 4 | `Emuera/UI/Game/AgentCliProtocol.cs` | Headless | 移动 + 修改 | 增加 redirected stdin 自动测试模式 |
| 5 | `Emuera/Program.cs` | WinForms | 修改 | 删除 headless/server 选项、方法、属性和分发逻辑 |
| 6 | `Emuera/UI/Dialog.cs` | WinForms | 修改 | 恢复为纯 `MessageBox.Show` |
| 7 | `Emuera.Headless/Program.cs` | Headless | 修改 | 删除 `--headless`；新增 `--protocol`；显式创建并注入协议 |
| 8 | `Emuera.Headless/Emuera.Headless.csproj` | Headless | 修改 | 删除旧的 Server/Agent linked compile 配置 |
| 9 | `Emuera/Server/*.cs` | Headless | 移动 | server 相关文件全部移动到 `Emuera.Headless/Server/` |
| 10 | `tests/emuera_agent.py` | 测试 | 修改 | JSONL 启动命令添加 `--protocol jsonl` |
| 11 | `tests/run_all.py` | 测试 | 修改 | 用 pipe-based CLI protocol 测试替代 TTY-only smoke |
| 12 | `tests/test_cli.py` | 测试 | 新增 | CLI 协议自动测试 |

---

## 4. 详细设计

### 4.1 `EmueraConsole` — 协议注入替代自动检测

**现状**：构造函数中调用 `AgentProtocolBase.Detect(this, _uiAdapter)`。

**改造**：

1. 删除构造函数中的自动检测调用。
2. 在 `#if HEADLESS` 下保留协议字段、属性和注入方法。
3. 新增 `internal void SetAgentBridge(AgentProtocolBase protocol)`。
4. `SetAgentBridge()` 必须在 `Initialize()` 之前调用。

示例：

```csharp
#if HEADLESS
private AgentProtocolBase? _agentBridge;

public AgentProtocolBase? AgentBridge => _agentBridge;

internal void SetAgentBridge(AgentProtocolBase protocol) => _agentBridge = protocol;
#endif
```

> 说明：当前项目关闭 nullable 检查，因此 `AgentProtocolBase?` 主要表达语义；如需完整 nullable 静态检查，需要另行启用项目级 nullable。

### 4.2 `WriteAlignedLine` — HEADLESS 下写入 agent buffer

**现状**：`WriteAlignedLine` 在 `_agentBridge is null` 时直接 return，导致协议注入时机晚于输出时可能丢失文本。

**改造**：

```csharp
#if HEADLESS
private void WriteAlignedLine(ConsoleDisplayLine line)
{
    string text = line.ToString();
    if (string.IsNullOrEmpty(text))
    {
        WriteToAgentBuffer("");
        return;
    }

    int textWidth = GetDisplayWidth(text);
    int consoleWidth;
    try { consoleWidth = Console.WindowWidth; }
    catch { consoleWidth = 80; }

    string output;
    switch (line.Align)
    {
        case DisplayLineAlignment.CENTER:
            int centerPad = Math.Max((consoleWidth - textWidth) / 2, 0);
            output = new string(' ', centerPad) + text;
            break;

        case DisplayLineAlignment.RIGHT:
            int rightPad = Math.Max(consoleWidth - textWidth, 0);
            output = new string(' ', rightPad) + text;
            break;

        default:
            output = text;
            break;
    }

    WriteToAgentBuffer(output);
}

private void WriteToAgentBuffer(string text)
{
    lock (_agentBufferLock)
    {
        _agentBuffer.AppendLine(text);
    }
}
#else
private void WriteAlignedLine(ConsoleDisplayLine line)
{
    // WinForms 模式不采集 agent buffer。
}
#endif
```

效果：

- Headless 模式：协议可以在 `Initialize()` 前注入，输出不会丢失。
- WinForms 模式：不依赖 `AgentProtocolBase`，不采集 agent buffer。

### 4.3 WinForms `Program.cs` — 恢复纯 GUI 入口

从 `Emuera/Program.cs` 删除：

- `headlessOption`
- `serverOption`
- `portOption`
- `--headless`、`--server`、`--port` 及大小写别名
- `RunHeadless()`
- `RunServer()`
- `IsHeadlessMode`
- `if (server) ... else if (headless) ...` 分发逻辑

保留：

- `--ExeDir`
- `-Debug`
- `-GenLang`
- 文件参数
- WinForms 初始化流程

### 4.4 WinForms `Dialog.cs` — 恢复纯 MessageBox

删除 `IsHeadless` 判断和 `Console.Error.WriteLine` 降级逻辑。

改造后：

```csharp
public static void Show(string text)
{
    MessageBox.Show(text);
}

public static void Show(string title, string text)
{
    MessageBox.Show(text, title);
}

public static bool ShowPrompt(string title, string text)
{
    var result = MessageBox.Show(text, title, MessageBoxButtons.YesNo);
    return result == DialogResult.Yes;
}
```

Headless 项目使用自己的 `HeadlessDialog.cs`，不受影响。

### 4.5 `Emuera.Headless` — 删除 `--headless`

删除 `Emuera.Headless/Program.cs` 中：

```csharp
static readonly Option<bool> headlessOption = new(
    name: "--headless",
    description: "无头模式：不创建 GUI 窗口，通过 stdin/stdout 进行 JSONL 交互"
);
```

以及：

- `-headless` / `-HEADLESS` 别名
- `rootCommand.AddOption(headlessOption)`
- `result.GetValueForOption(headlessOption)`
- `headless` 局部变量
- 不再需要的 `IsHeadlessMode = true`

`Emuera.Headless.exe` 默认即 headless。

### 4.6 `Emuera.Headless` — 新增 `--protocol`

新增选项：

```csharp
static readonly Option<string> protocolOption = new(
    name: "--protocol",
    description: "协议模式：auto(默认), jsonl, cli",
    getDefaultValue: () => "auto"
);
```

建议保留大小写别名：

```csharp
protocolOption.AddAlias("-protocol");
protocolOption.AddAlias("-PROTOCOL");
rootCommand.AddOption(protocolOption);
```

支持值：

| 值 | 行为 |
|---|---|
| `auto` | 使用 `DetectProtocol()` 按环境选择 JSONL 或 CLI |
| `jsonl` | 强制创建 `AgentJsonlProtocol` |
| `cli` | 强制创建 `AgentCliProtocol` |

未知值应报错并退出：

```text
[headless] 未知协议模式: xxx
```

### 4.7 Headless 协议创建与注入

`RunHeadless(string protocolArg)` 应显式创建协议：

```csharp
private static void RunHeadless(string protocolArg)
{
    var ui = new HeadlessConsole();
    var console = new EmueraConsole(ui);

    AgentProtocolBase? protocol = SelectProtocol(protocolArg, console, ui);

    if (protocol == null)
    {
        Console.Error.WriteLine("[headless] 无法确定协议模式；请使用 --protocol jsonl 或 --protocol cli");
        Environment.Exit(1);
        return;
    }

    console.SetAgentBridge(protocol);
    console.Initialize().Wait();

    if (protocol is AgentJsonlProtocol)
        RunJsonlLoop(protocol);
    else if (protocol is AgentCliProtocol)
        RunCliLoop(protocol);
}
```

`SelectProtocol()`：

```csharp
private static AgentProtocolBase? SelectProtocol(string protocolArg, EmueraConsole console, IConsoleUI ui)
{
    return protocolArg.Trim().ToLowerInvariant() switch
    {
        "auto" => DetectProtocol(console, ui),
        "jsonl" => new AgentJsonlProtocol(console, ui),
        "cli" => new AgentCliProtocol(console, ui),
        _ => throw new ArgumentException($"未知协议模式: {protocolArg}", nameof(protocolArg))
    };
}
```

### 4.8 `DetectProtocol()` — 仅用于 `auto`

保留自动检测逻辑，但只作为 `--protocol auto` 的实现：

```csharp
private static AgentProtocolBase? DetectProtocol(EmueraConsole console, IConsoleUI ui)
{
    if (Console.IsInputRedirected)
    {
        using var stdin = Console.OpenStandardInput();
        if (stdin.CanSeek)
            return null;

        return new AgentJsonlProtocol(console, ui);
    }

    try
    {
        _ = Console.KeyAvailable;
    }
    catch
    {
        return null;
    }

    return new AgentCliProtocol(console, ui);
}
```

### 4.9 Server 模式

Server 模式继续由 `Emuera.Headless/Program.cs` 处理：

```bash
Emuera.Headless.exe --server --port 8080 --ExeDir test_game
```

server 模式下不应使用 `--protocol`。建议当 `--server` 与 `--protocol` 同时出现时直接报错，避免协议选择与 server session 管理产生歧义。

### 4.10 `Session.cs` — 显式注入协议

移动后的 `Emuera.Headless/Server/Session.cs` 构造函数应在创建协议后立即注入：

```csharp
public Session(HttpSessionIO io)
{
    _io = io;
    _ui = new HeadlessConsole();
    _console = new EmueraConsole(_ui);
    _protocol = new AgentJsonlProtocol(_console, _ui, io);
    _console.SetAgentBridge(_protocol);
}
```

### 4.11 CLI 协议 — redirected stdin 自动测试模式

`AgentCliProtocol` 需要同时支持真实终端和 pipe 测试。

#### 真实终端模式

非 redirected stdin 时保持现状：

```csharp
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
```

#### redirected stdin 模式

redirected stdin 时读取文本行，并模拟 CLI 按键行为：

```csharp
private void RunPipeCliLoop(TextReader input)
{
    while (!IsStopped)
    {
        string? line = input.ReadLine();
        if (line == null)
            break;

        foreach (char ch in line)
            ProcessChar(ch);

        ProcessChar('\r');
        FlushBuffer();
    }
}
```

`ProcessChar()` 行为：

| 输入 | 行为 |
|---|---|
| 普通可打印字符 | 追加到 CLI buffer，并写入输出 |
| `\b` | 删除最后一个字符 |
| escape | 清空 CLI buffer |
| `\r` / `\n` | 提交当前 buffer |

`RunCliLoop()` 统一入口：

```csharp
internal void RunCliLoop()
{
    FlushBuffer();

    if (Console.IsInputRedirected)
        RunPipeCliLoop(Console.In);
    else
        RunConsoleKeyLoop();

    FlushBuffer();
}
```

效果：

- 真实终端用户行为不变。
- 自动化测试可以用普通 `stdin=PIPE` / `stdout=PIPE` 驱动 CLI 协议。

---

## 5. 测试改动

### 5.1 `emuera_agent.py`

JSONL 启动命令必须显式传入 `--protocol jsonl`：

```python
if self._use_dotnet:
    cmd = [
        "dotnet", "exec", binary_path,
        "--ExeDir", self.game_dir,
        "--protocol", "jsonl"
    ]
else:
    cmd = [
        binary_path,
        "--ExeDir", self.game_dir,
        "--protocol", "jsonl"
    ]
```

效果：JSONL 测试不再依赖 `stdin=PIPE` 自动检测。

### 5.2 新增 `tests/test_cli.py`

新增 CLI 协议自动测试，要求：

1. 启动：

   ```bash
   Emuera.Headless.exe --ExeDir test_game --protocol cli
   ```

2. 使用：

   ```python
   stdin=subprocess.PIPE
   stdout=subprocess.PIPE
   stderr=subprocess.PIPE
   ```

3. 向 stdin 写入：

   ```text
   0\n
   ```

4. 从 stdout 读取文本输出。
5. 断言 CLI 输出包含预期内容，并能推进 `test_game` 流程。

该测试不依赖真实 TTY。

### 5.3 `run_all.py`

改造内容：

- 移除旧的 TTY-only CLI smoke。
- 移除 `sys.stdin.isatty()` 跳过逻辑。
- 移除 `--force-cli-smoke` 参数。
- 加入 `tests/test_cli.py`。
- JSONL 测试通过 `emuera_agent.py` 使用 `--protocol jsonl`。
- server 测试不再传 `--headless`。

回归套件应包含：

- JSONL + buttons 测试
- CLI protocol 测试
- Server 单会话测试
- TINPUT timeout 测试

---

## 6. 编译验证

改造完成后必须同时通过两个项目的编译：

```bash
# WinForms 项目
dotnet build Emuera/Emuera.csproj -c Debug-NAudio

# Headless 项目
dotnet build Emuera.Headless/Emuera.Headless.csproj -c Debug
```

---

## 7. 功能验证

### 7.1 WinForms 回归

- `Emuera.exe` 双击启动正常，窗口正常显示。
- 游戏逻辑正常运行，输入输出正常。
- `Dialog.Show` 弹出 `MessageBox`，不写 `Console.Error`。
- `Emuera.exe --headless` 不再是 WinForms 项目支持的能力。

### 7.2 Headless 默认模式

```bash
Emuera.Headless.exe --ExeDir test_game
```

默认 `--protocol auto`：

- redirected stdin 且非 seekable 时选择 JSONL。
- 真实终端时选择 CLI。

### 7.3 Headless JSONL 模式

```bash
echo '{"type":"input","value":"0"}' | Emuera.Headless.exe --ExeDir test_game --protocol jsonl
```

要求：

- 不依赖自动检测。
- 输出 JSONL turn。
- 自动化测试可稳定驱动。

### 7.4 Headless CLI 真实终端模式

```bash
Emuera.Headless.exe --ExeDir test_game --protocol cli
```

要求：

- 在真实终端中继续使用 `Console.KeyAvailable` / `Console.ReadKey(true)`。
- 用户可输入字符、Backspace、Enter、Escape。

### 7.5 Headless CLI pipe 自动测试模式

```bash
Emuera.Headless.exe --ExeDir test_game --protocol cli
```

测试进程使用 redirected stdin/stdout：

```python
proc.stdin.write("0\n")
proc.stdin.flush()
```

要求：

- 不依赖真实 TTY。
- 能从 stdout 读取 CLI 文本输出。
- 能推进测试游戏流程。

### 7.6 Server 模式

```bash
Emuera.Headless.exe --server --port 8080 --ExeDir test_game
```

要求：

- 创建会话正常。
- 提交输入正常。
- 获取 turn 正常。
- 删除会话正常。
- 不再需要 `--headless`。

### 7.7 自动化测试

```bash
python tests/run_all.py --binary Emuera.Headless/bin/Debug/net10.0/Emuera.Headless.exe --game-dir test_game
```

所有测试通过，包括：

- JSONL + buttons 测试
- CLI protocol 测试
- Server 单会话测试
- TINPUT timeout 测试

---

## 8. 风险与缓解

| 风险 | 影响 | 缓解 |
|---|---|---|
| 协议文件移动后 WinForms 仍引用 `AgentProtocolBase` | WinForms 编译失败 | `EmueraConsole` 删除自动检测，协议相关代码全部放入 `#if HEADLESS` |
| 旧文件未从 `Emuera/` 删除导致重复编译 | 类型重复或维护混乱 | 物理移动文件，并删除旧的 linked compile 配置 |
| `Emuera.Headless.csproj` 未更新 | 移动后的文件未被编译或旧文件重复编译 | 删除 Server/Agent linked compile 配置，依赖 SDK 默认 include |
| `SetAgentBridge()` 调用晚于 `Initialize()` | 初始化期间输出丢失 | 在 `Program.cs` 和 `Session.cs` 中明确先注入再 `Initialize()` |
| CLI pipe 模式与真实终端模式行为不一致 | 测试结果不能代表真实用户 | `ProcessChar()` 复用现有按键语义，仅输入来源不同 |
| CLI 测试阻塞 | CI 超时 | 测试使用 timeout，并在结束后关闭 stdin/清理进程 |
| `--server` 与 `--protocol` 同时使用产生歧义 | server 行为不确定 | 建议直接报错，要求 server 模式不传 `--protocol` |
| 删除 `--headless` 破坏旧调用 | 用户命令失败 | 这是本规格明确要求；可在文档中说明 `Emuera.Headless.exe` 默认即 headless |

---

## 9. 与已有规格的关系

本规格是对 [SPEC_SINGLE_THREAD.md](../3.single-thread/SPEC_SINGLE_THREAD.md) 的补充和延续：

- 单线程化规格将协议从“后台线程驱动”改为“同步步进驱动”。
- 本规格将协议创建从“自动检测”改为“显式注入”。
- 本规格进一步将 agent/server 代码从 WinForms 项目树移动到 `Emuera.Headless` 项目树。
- 本规格将 CLI 协议从“只能真实 TTY”扩展为“真实 TTY + redirected stdin 自动测试”两种模式。

本规格完成后，可重新评估 `docs/3.single-thread/TODO.md` 中的 T-007（清理 `_agentBufferLock`）：在单线程化且协议注入明确后，`WriteToAgentBuffer` 中的 `lock` 可能不再需要，但应先在两个项目中验证无并发写入风险。
