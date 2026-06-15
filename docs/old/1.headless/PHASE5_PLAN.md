# Phase 5: 无头模式验收 — 纯控制台环境端到端验证

> 目标：不启动任何窗体，通过 stdin/stdout 完整运行游戏。修复 Phase 4 遗留问题，使 `--headless` 参数真正可用。

---

## 5.1 现状分析

### 5.1.1 Phase 4 已完成的解耦成果

- `EmueraConsole` 已完全通过 `IConsoleUI _uiAdapter` 交互，无直接 `MainWindow` 引用
- `HeadlessConsole` 已实现 `IConsoleUI` 全部成员的空操作/默认值
- `Program.cs` 已支持 `--headless` 参数，调用 `RunHeadless()`
- `AgentJsonlProtocol` / `AgentCliProtocol` 已解耦 `MainWindow`，通过 `IConsoleUI` 操作

### 5.1.2 当前 `--headless` 执行路径

```
Program.Main(args)
  → InitializeCore(args)          // 配置加载、目录检查
  → RunHeadless(args)
    → new HeadlessConsole()
    → new EmueraConsole(ui)
      → AgentProtocolBase.DetectAndRun(this, _uiAdapter)
        → Console.IsInputRedirected ?
          → AgentJsonlProtocol (管道重定向)
          → AgentCliProtocol   (有终端)
    → console.Initialize().Wait()   // 加载脚本、启动游戏
    → 主线程轮询 protocol.IsStopped
```

### 5.1.3 已知问题与风险点

| 问题 | 位置 | 风险等级 | 说明 |
|------|------|---------|------|
| `Initialize().Wait()` 阻塞主线程 | `Program.RunHeadless` | 高 | `Initialize()` 内部调用 `RunEmueraProgram("")`，会执行到第一个 `WaitInput` 并阻塞在 `state == ConsoleState.WaitInput`。但 `AgentJsonlProtocol` 在独立线程中轮询并提交输入，理论上可以推进。需要验证是否死锁。 |
| `Redraw` 与画面刷新 | `EmueraConsole` | 中 | 无头模式下 `Refresh()` 为空操作，`redrawTimer` 未启用。`AgentJsonlProtocol` 通过 `TakeAgentBuffer()` 采集文本，不依赖画面渲染。但需确认文本输出是否完整。 |
| `PressEnterKey` 线程安全 | `AgentJsonlProtocol.SubmitAndGetTurn` | 中 | `ui.Invoke(() => console.PressEnterKey(...))` 在 `HeadlessConsole.Invoke` 中是同步执行 `action?.Invoke()`。需确认 `PressEnterKey` 是否线程安全（是否会在协议线程与主线程竞争）。 |
| `Application.Exit()` 未调用 | `HeadlessConsole.ExitApplication` | 低 | 已改为 `Environment.Exit(0)`，无问题。 |
| `WinForms` 静态属性残留 | `EmueraConsole` 部分代码 | 低 | `Control.MousePosition` 等仅在 `state == WaitInput && inputReq.NeedValue` 的分支中使用，无头模式下无鼠标输入，不会执行到。 |
| `MessageBox.Show` | `ForceQuit()` | 低 | 仅在 `GlobalStatic.ForceQuitAndRestart == true` 时弹出，无头模式下该路径不可达（无用户交互触发）。 |
| `AnalysisMode` / `DebugMode` | `Program.InitializeCore` | 低 | 无头模式下 `AnalysisMode` 不应启用；`DebugMode` 会尝试打开调试窗口，需确认 `RunHeadless` 中是否跳过。 |

---

## 5.2 修改方案

### 5.2.1 `RunHeadless` 完善（Phase 5a）

**文件：** `Emuera/Program.cs`

**Step 5a.1：禁用 `AnalysisMode` 和 `DebugMode` 的窗口操作**

```csharp
private static void RunHeadless(string[] args)
{
    // 无头模式下禁用分析模式（分析模式需要 GUI 文件选择对话框）
    AnalysisMode = false;
    // DebugMode 保留，但跳过 OpenDebugDialog（EmueraConsole.Initialize 内部已用 _uiAdapter.Focus，HeadlessConsole.Focus 为空操作）

    var ui = new UI.Game.HeadlessConsole();
    var console = new GameView.EmueraConsole(ui);

    // 初始化并启动游戏逻辑
    console.Initialize().Wait();

    // 主线程保持运行，等待协议线程结束
    var protocol = console.AgentBridge;
    if (protocol != null)
    {
        while (!protocol.IsStopped)
        {
            Thread.Sleep(100);
        }
    }
    else
    {
        // 未检测到输入管道且非交互终端（理论上不会发生，因为 DetectAndRun 在 headless 模式下至少返回 CLI）
        Console.Error.WriteLine("[headless] 未检测到输入管道，游戏逻辑需要手动驱动");
        // 直接退出，避免空转
        Environment.Exit(1);
    }
}
```

**Step 5a.2：添加无头模式日志输出**

在 `RunHeadless` 开头输出诊断信息，便于排查：

```csharp
Console.Error.WriteLine($"[headless] Emuera {AssemblyData.EmueraVersionText} 无头模式启动");
Console.Error.WriteLine($"[headless] 工作目录: {ExeDir}");
Console.Error.WriteLine($"[headless] 协议类型: {(Console.IsInputRedirected ? "JSONL (管道)" : "CLI (终端)")}");
```

### 5.2.2 `AgentJsonlProtocol` 健壮性增强（Phase 5b）

**文件：** `Emuera/UI/Game/AgentJsonlProtocol.cs`

**Step 5b.1：输入提交线程安全确认**

当前 `SubmitAndGetTurn` 中：

```csharp
ui.Invoke(() =>
{
    if (console.State == ConsoleState.WaitInput)
        console.PressEnterKey(false, value, false);
});
```

`HeadlessConsole.Invoke` 同步执行，因此 `PressEnterKey` 在协议线程中直接运行。需确认 `PressEnterKey` 是否修改了任何 UI 相关状态（如 `_uiAdapter.TextBox.Text`）。由于 `HeadlessConsole` 所有 UI 操作为空，实际安全。

**Step 5b.2：异常处理与优雅退出**

在 `ReadStdinLoop` 和 `HandleMessage` 中添加异常捕获，避免协议线程崩溃导致主线程空转：

```csharp
private void HandleMessage(string line)
{
    JsonlCommand cmd;
    try { cmd = JsonSerializer.Deserialize<JsonlCommand>(line); }
    catch { return; }

    if (cmd?.type == "input")
    {
        try
        {
            string turn = SubmitAndGetTurn(cmd.value ?? "");
            if (turn != null)
                Console.WriteLine(turn);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[jsonl] 输入处理异常: {ex.Message}");
            // 可选：输出错误状态 JSON
            Console.WriteLine(JsonSerializer.Serialize(new { error = ex.Message, state = "Error" }));
        }
    }
}
```

**Step 5b.3：`TurnTimeoutMs` 调整**

当前 `TurnTimeoutMs = 30000`。无头模式下游戏可能长时间等待输入（如复杂脚本初始化），建议：
- 保持 30 秒默认值
- 或在 `AgentJsonlProtocol` 构造函数中允许外部配置（暂不改，保持最小修改）

### 5.2.3 `EmueraConsole` 无头模式兼容性修复（Phase 5c）

**文件：** `Emuera/UI/Game/EmueraConsole.cs`

**Step 5c.1：确认 `RefreshStrings` 在无头模式下不抛异常**

`RefreshStrings` 中会调用 `_uiAdapter.ProcessEvents()` 和 `_uiAdapter.Refresh()`。`HeadlessConsole` 均为空实现，安全。

**Step 5c.2：确认 `RunEmueraProgram` 在无头模式下正常推进**

`RunEmueraProgram` 是游戏主循环，会执行脚本直到遇到 `WaitInput`。此时 `state` 变为 `ConsoleState.WaitInput`，`AgentJsonlProtocol` 的 `WaitForInput()` 检测到后返回 `true`，然后 `BuildTurn()` 采集状态并输出 JSON。

潜在问题：`RunEmueraProgram` 内部是否依赖 `Application.DoEvents()` 或消息泵？
- 搜索 `Application.DoEvents` 在 `EmueraConsole` 中的使用：仅在 `RefreshStrings` 的帧率限制循环中，已替换为 `_uiAdapter.ProcessEvents()`。
- 无头模式下 `ProcessEvents()` 为空操作，不影响。

**Step 5c.3：`ForceQuit` 在无头模式下的行为**

```csharp
if (Program.rebootFlag)
    _uiAdapter.Reboot();
else
    _uiAdapter.ExitApplication();
```

`HeadlessConsole.Reboot()` 为空操作，`ExitApplication()` 调用 `Environment.Exit(0)`。无头模式下 `rebootFlag` 不会被设置（无用户操作），因此会直接退出进程。符合预期。

### 5.2.4 测试基础设施（Phase 5d）

**新增文件：** `test_input.txt`（示例输入序列，用于手动/自动化测试）

```jsonl
{"type":"input","value":""}
{"type":"input","value":"1"}
{"type":"input","value":""}
```

**新增文件：** `scripts/test_headless.ps1`（PowerShell 测试脚本）

```powershell
# 测试无头模式 JSONL 交互
$inputLines = @(
    '{"type":"input","value":""}'
    '{"type":"input","value":"1"}'
    '{"type":"input","value":""}'
)

$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = "Emuera.exe"
$psi.Arguments = "--headless"
$psi.RedirectStandardInput = $true
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$psi.UseShellExecute = $false

$proc = [System.Diagnostics.Process]::Start($psi)

foreach ($line in $inputLines) {
    $proc.StandardInput.WriteLine($line)
    Start-Sleep -Milliseconds 500
}

Start-Sleep -Seconds 2
$proc.Kill()

Write-Host "=== STDOUT ==="
$proc.StandardOutput.ReadToEnd()
Write-Host "=== STDERR ==="
$proc.StandardError.ReadToEnd()
```

> **注意：** 测试脚本仅用于 Phase 5 验证，不提交到仓库（或放入 `.gitignore`）。

---

## 5.3 修改步骤汇总

### Phase 5a: `RunHeadless` 完善

- [ ] **Step 5a.1** `Program.RunHeadless` 中设置 `AnalysisMode = false`
- [ ] **Step 5a.2** 添加无头模式启动诊断日志（stderr）
- [ ] **Step 5a.3** `protocol == null` 时输出错误并 `Environment.Exit(1)`

### Phase 5b: `AgentJsonlProtocol` 健壮性增强

- [ ] **Step 5b.1** `HandleMessage` 中添加 `SubmitAndGetTurn` 的异常捕获
- [ ] **Step 5b.2** `ReadStdinLoop` 中 `onLine` 调用加 try/catch（或统一在 HandleMessage 中处理）
- [ ] **Step 5b.3** 错误时输出 JSON 格式错误信息

### Phase 5c: `EmueraConsole` 兼容性确认（代码审查，可能无需修改）

- [ ] **Step 5c.1** 审查 `RefreshStrings` 在无头模式下的执行路径
- [ ] **Step 5c.2** 审查 `RunEmueraProgram` 是否依赖消息泵
- [ ] **Step 5c.3** 审查 `ForceQuit` 在无头模式下的退出路径
- [ ] **Step 5c.4** 审查 `PressEnterKey` 线程安全性

### Phase 5d: 测试与验证

- [ ] **Step 5d.1** 编写 `test_input.txt` 示例输入
- [ ] **Step 5d.2** 编写 `scripts/test_headless.ps1` 自动化测试脚本
- [ ] **Step 5d.3** 执行 WinForms 模式回归测试
- [ ] **Step 5d.4** 执行无头模式端到端测试

---

## 5.4 验收标准

### Phase 5a 验收

```bash
# 1. 编译通过（NAudio 配置）
dotnet build Emuera/Emuera.csproj -c Debug-NAudio

# 2. WinForms 模式不变
Emuera.exe
# → 正常出现窗口，游戏可玩

# 3. 无头模式启动有诊断输出
echo '{"type":"input","value":""}' | Emuera.exe --headless
# → stderr 出现 [headless] 启动日志
# → stdout 输出初始回合 JSON（含 text, state, inputType, needValue, buttons）
```

### Phase 5b 验收

```bash
# 1. 编译通过
dotnet build -c Debug-NAudio

# 2. 异常输入不崩溃
echo 'invalid json' | Emuera.exe --headless
# → 不抛异常，继续等待有效输入或超时退出

# 3. 空输入能推进游戏
echo '{"type":"input","value":""}' | Emuera.exe --headless
# → 输出回合状态 JSON
```

### Phase 5c 验收

```bash
# 1. 编译通过
dotnet build -c Debug-NAudio

# 2. 多轮交互测试（使用 test_input.txt）
Emuera.exe --headless < test_input.txt
# → 每行输入对应一个回合输出
# → 最终进程正常退出（或到达游戏结束）
```

### Phase 5d 验收

```bash
# 1. 纯管道模式运行（PowerShell 示例）
$proc = Start-Process Emuera.exe -ArgumentList "--headless" -RedirectStandardInput -RedirectStandardOutput -PassThru
$proc.StandardInput.WriteLine('{"type":"input","value":""}')
$proc.StandardOutput.ReadLine()
# → 返回 JSON 回合状态

# 2. 与 WinForms 模式行为一致性对比
# - 相同输入产生相同输出文本（对比 TakeAgentBuffer 内容）
# - 按钮列表一致（CollectVisibleButtons）
# - 状态转换一致（ConsoleState 序列）
```

### Phase 5 整体验收标准

```bash
# 1. 编译通过（NAudio 配置）
dotnet build -c Debug-NAudio

# 2. WinForms 模式完整功能测试
# - 正常游戏流程
# - 按钮点击、输入、宏、定时器
# - 调试窗口、配置对话框

# 3. --headless 参数端到端测试
# 3a. 管道重定向模式（JSONL）
python -c "import sys; [print(l) for l in sys.stdin]" | Emuera.exe --headless
# → 实际测试：通过 Python 脚本发送多轮输入，验证输出

# 3b. 终端交互模式（CLI）
Emuera.exe --headless
# → 出现终端提示符，键盘输入可推进游戏
# → Ctrl+C 或输入 exit 可退出

# 4. 行为一致性
# - WinForms 模式与无头模式在相同输入序列下，游戏状态一致
# - 文本输出一致（忽略渲染差异）
# - 按钮列表一致
```

---

## 5.5 风险与缓解

| 风险 | 影响 | 缓解 |
|------|------|------|
| `Initialize().Wait()` 与协议线程死锁 | 高 | 审查 `WaitInput` 的实现：主线程在 `RunEmueraProgram` 中阻塞，协议线程通过 `PressEnterKey` 修改状态。`PressEnterKey` 不持有主线程锁，理论上无死锁。若发现问题，改为异步 `Initialize()` + 主线程消息循环。 |
| `TakeAgentBuffer()` 文本不完整 | 中 | `AgentBuffer` 在 `OutputLog` / `Print` 时追加，无头模式下与 WinForms 模式追加逻辑一致。差异仅在于 `RefreshStrings` 不渲染画面。验证时对比 WinForms 与无头模式的 `TakeAgentBuffer` 输出。 |
| 游戏脚本依赖 `System.Windows.Forms` | 低 | 脚本层（GameProc）不应直接引用 WinForms。若存在，需在 Phase 5c 审查中发现并修复。 |
| `Environment.Exit(0)` 导致资源未释放 | 低 | 无头模式下无窗口资源、无文件锁（除日志外），`Environment.Exit` 可接受。如需优雅释放，可在 `HeadlessConsole.Close` 中设置标志，由主线程自然退出。 |
| 定时器功能在无头模式下异常 | 低 | `genericTimer` 是 `System.Timers.Timer`，不依赖 WinForms。`redrawTimer` 未启用。`presetTimer` / `timer_endTime` 逻辑纯计算，无头模式下正常。 |

---

## 5.6 与后续 Phase 的衔接

- **Phase 6**：服务器模式添加 TCP/HTTP 接口，复用已验证的 `AgentJsonlProtocol` 序列化逻辑
- **Phase 7**：Python 网关扩展，`mcp_relay.py` 通过 JSONL 与无头 Emuera 交互，Phase 5 的协议格式即为契约

---

## 5.7 检查清单

- [ ] `Program.cs` `RunHeadless` 设置 `AnalysisMode = false`
- [ ] `Program.cs` `RunHeadless` 添加诊断日志
- [ ] `AgentJsonlProtocol.cs` `HandleMessage` 添加异常处理
- [ ] `EmueraConsole.cs` 审查无头模式兼容性（可能无需修改）
- [ ] 编写 `test_input.txt` 测试输入
- [ ] 编写 `scripts/test_headless.ps1` 自动化测试
- [ ] WinForms 模式回归测试通过
- [ ] `--headless` 管道模式测试通过
- [ ] `--headless` 终端模式测试通过
- [ ] 行为一致性验证通过
