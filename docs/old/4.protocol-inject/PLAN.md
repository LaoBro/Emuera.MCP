# Emuera Headless 协议注入、归属整理与 CLI 可测化 — 实施计划

> 版本：v1.1
> 日期：2026-06-11
> 所属规格：[SPEC.md](./SPEC.md)

---

## 实施步骤

按依赖顺序执行。每步完成后运行相关构建；最终运行完整回归测试。

### Step 1：移动协议与 server 文件到 `Emuera.Headless`

**文件**：

- `Emuera/UI/Game/AgentProtocolBase.cs` → `Emuera.Headless/Agent/AgentProtocolBase.cs`
- `Emuera/UI/Game/AgentJsonlProtocol.cs` → `Emuera.Headless/Agent/AgentJsonlProtocol.cs`
- `Emuera/UI/Game/AgentCliProtocol.cs` → `Emuera.Headless/Agent/AgentCliProtocol.cs`
- `Emuera/Server/*.cs` → `Emuera.Headless/Server/*.cs`

**验证**：

```bash
dotnet build Emuera/Emuera.csproj -c Debug-NAudio -v:minimal /clp:ErrorsOnly
dotnet build Emuera.Headless/Emuera.Headless.csproj -c Debug -v:minimal /clp:ErrorsOnly
```

---

### Step 2：更新 `Emuera.Headless.csproj`

**文件**：`Emuera.Headless/Emuera.Headless.csproj`

**改动**：

1. 删除 `..\Emuera\Server\**\*.cs` 的 linked compile 配置。
2. 删除 `AgentProtocolBase.cs`、`AgentJsonlProtocol.cs`、`AgentCliProtocol.cs` 的 linked compile 配置。
3. 依赖 SDK 默认 include 编译 `Emuera.Headless/Agent` 与 `Emuera.Headless/Server` 下的源码。

**验证**：

```bash
dotnet build Emuera.Headless/Emuera.Headless.csproj -c Debug -v:minimal /clp:ErrorsOnly
```

---

### Step 3：`EmueraConsole` 支持协议注入

**文件**：`Emuera/UI/Game/EmueraConsole.cs`

**改动**：

1. 删除构造函数中的 `AgentProtocolBase.Detect(this, _uiAdapter)`。
2. 在 `#if HEADLESS` 下保留 `_agentBridge`、`AgentBridge`。
3. 新增 `internal void SetAgentBridge(AgentProtocolBase protocol)`。
4. `Dispose()` 中仅在 `#if HEADLESS` 下停止 `_agentBridge`。

**验证**：

```bash
dotnet build Emuera/Emuera.csproj -c Debug-NAudio -v:minimal /clp:ErrorsOnly
dotnet build Emuera.Headless/Emuera.Headless.csproj -c Debug -v:minimal /clp:ErrorsOnly
```

---

### Step 4：`WriteAlignedLine` 解耦 buffer 写入

**文件**：`Emuera/UI/Game/EmueraConsole.AgentBridge.cs`

**改动**：

1. 在 `#if HEADLESS` 下，`WriteAlignedLine` 始终写入 `_agentBuffer`。
2. 新增 `WriteToAgentBuffer(string text)`。
3. WinForms 构建下保留空实现，避免引用协议类型。

**验证**：

```bash
dotnet build Emuera/Emuera.csproj -c Debug-NAudio -v:minimal /clp:ErrorsOnly
dotnet build Emuera.Headless/Emuera.Headless.csproj -c Debug -v:minimal /clp:ErrorsOnly
```

---

### Step 5：恢复 WinForms `Program.cs` 为纯 GUI 入口

**文件**：`Emuera/Program.cs`

**改动**：

1. 删除 `headlessOption`、`serverOption`、`portOption`。
2. 删除 `--headless`、`--server`、`--port` 及大小写别名。
3. 删除 `RunHeadless()`、`RunServer()`。
4. 删除 `IsHeadlessMode`。
5. `Main()` 只解析 GUI 参数并调用 `RunWinForms(args, icon)`。

**验证**：

```bash
dotnet build Emuera/Emuera.csproj -c Debug-NAudio -v:minimal /clp:ErrorsOnly
```

---

### Step 6：恢复 WinForms `Dialog.cs` 为纯 MessageBox

**文件**：`Emuera/UI/Dialog.cs`

**改动**：

1. 删除 `IsHeadless` 属性。
2. `Show(string)`、`Show(string, string)` 直接使用 `MessageBox.Show`。
3. `ShowPrompt(...)` 直接使用 `MessageBox.Show(..., MessageBoxButtons.YesNo)`。

**验证**：

```bash
dotnet build Emuera/Emuera.csproj -c Debug-NAudio -v:minimal /clp:ErrorsOnly
```

---

### Step 7：`Emuera.Headless` 删除 `--headless` 并新增 `--protocol`

**文件**：`Emuera.Headless/Program.cs`

**改动**：

1. 删除 `headlessOption`、`-headless`、`-HEADLESS`。
2. 新增 `protocolOption`，默认值为 `auto`。
3. `RunHeadless()` 接收 `protocolArg`。
4. 新增 `SelectProtocol()` 与 `DetectProtocol()`。
5. 在 `console.Initialize().Wait()` 前调用 `console.SetAgentBridge(protocol)`。
6. server 模式显式传 `--protocol` 时直接报错。

**验证**：

```bash
dotnet build Emuera.Headless/Emuera.Headless.csproj -c Debug -v:minimal /clp:ErrorsOnly
```

---

### Step 8：`Session.cs` 显式注入协议

**文件**：`Emuera.Headless/Server/Session.cs`

**改动**：构造函数中创建 `AgentJsonlProtocol` 后调用：

```csharp
_console.SetAgentBridge(_protocol);
```

**验证**：

```bash
dotnet build Emuera.Headless/Emuera.Headless.csproj -c Debug -v:minimal /clp:ErrorsOnly
```

---

### Step 9：CLI 协议支持 redirected stdin 自动测试

**文件**：`Emuera.Headless/Agent/AgentCliProtocol.cs`

**改动**：

1. 真实终端模式继续使用 `Console.KeyAvailable` / `Console.ReadKey(true)`。
2. redirected stdin 模式使用 `Console.In.ReadLine()`。
3. 将每行文本拆成字符并复用现有 CLI buffer 语义：
   - 普通字符追加；
   - backspace 删除；
   - escape 清空；
   - 回车提交。

**验证**：

```bash
python tests/test_cli.py --binary Emuera.Headless/bin/Debug/net10.0/Emuera.Headless.exe --game-dir test_game
```

---

### Step 10：更新 Python 测试

**文件**：

- `tests/emuera_agent.py`
- `tests/run_all.py`
- `tests/test_cli.py`（新增）

**改动**：

1. `emuera_agent.py` 启动命令添加 `--protocol jsonl`。
2. 新增 `tests/test_cli.py`，用 redirected stdin/stdout 测试 CLI 协议。
3. `run_all.py` 用 CLI protocol 测试替代旧 TTY-only CLI smoke。
4. server 测试继续不带 `--headless`。

**验证**：

```bash
python tests/run_all.py --binary Emuera.Headless/bin/Debug/net10.0/Emuera.Headless.exe --game-dir test_game
```

---

## 最终验证

必须通过：

```bash
dotnet build Emuera/Emuera.csproj -c Debug-NAudio -v:minimal /clp:ErrorsOnly
dotnet build Emuera.Headless/Emuera.Headless.csproj -c Debug -v:minimal /clp:ErrorsOnly
python tests/run_all.py --binary Emuera.Headless/bin/Debug/net10.0/Emuera.Headless.exe --game-dir test_game
```

期望结果：

- JSONL + buttons 通过。
- CLI protocol 通过。
- Server single-session 通过。
- TINPUT timeout 通过。
