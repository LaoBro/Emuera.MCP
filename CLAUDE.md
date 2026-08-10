# CLAUDE.md

本文件只保留进入项目时必须知道的范围、命令、约束和文档入口。详细架构见 [`ARCHITECTURE.md`](ARCHITECTURE.md)，专题内容不要复制到本文件。

## 维护范围

当前维护核心：

- `Emuera.Headless.Core`
- `Emuera.Headless.Server`
- `Emuera.Headless.Cli`
- `Emuera.Maui`
- `Emuera.Web`

`Emuera/` 是已停止维护的 WinForms 参考源码；`EmueraPluginExample/` 和 `experiments/` 不属于产品主路径。

项目结构、依赖边界和运行时数据流见 [`ARCHITECTURE.md`](ARCHITECTURE.md)。

## 前置依赖

- .NET 10 SDK 或更高版本
- Python 3.10+
- Node.js 18+
- Windows CLI/ConPTY 测试：`pip install pywinpty`
- WebSocket 测试：`pip install websockets`

## 常用命令

### 构建 CLI/Server

```bash
dotnet build Emuera.Headless.Cli/Emuera.Headless.Cli.csproj -c Debug
```

### 运行交互式 CLI

需要真实 TTY：

```bash
dotnet exec Emuera.Headless.Cli/bin/Debug/net10.0/Emuera.Headless.Cli.dll --ExeDir test_game --protocol cli
```

### 运行 HTTP Server

```bash
dotnet exec Emuera.Headless.Cli/bin/Debug/net10.0/Emuera.Headless.Cli.dll --ExeDir test_game --server --port 8080
```

### 构建/运行 MAUI Windows

```bash
dotnet build Emuera.Maui/Emuera.Maui.csproj -f net10.0-windows10.0.19041.0 -c Debug
dotnet run --project Emuera.Maui/Emuera.Maui.csproj -f net10.0-windows10.0.19041.0 -c Debug
```

### C# 单元测试

```bash
dotnet test Emuera.Headless.Tests/Emuera.Headless.Tests.csproj
```

### 全量回归

```bash
python tests/run_all.py
```

测试夹具、套件和单项运行方式见 [`tests/README.md`](tests/README.md)。

### Web 前端

```bash
cd Emuera.Web && npm install
npm run dev
npm run typecheck
npm test
npm run build
```

前端文件导航和常见修改入口见 [`Emuera.Web/README.md`](Emuera.Web/README.md)。

### MCP 网关

嵌入模式：

```bash
python -m emuera_gateway --emuera-path Emuera.Headless.Cli/bin/Debug/net10.0/Emuera.Headless.Cli.exe --game-dir test_game
```

独立模式：

```bash
python -m emuera_gateway --standalone --server-url http://localhost:8080
```

使用 `emuera_gateway`，不要使用已废弃的 `mcp_relay.py`。

## 必须遵守的约束

- Core/Server/Cli/Maui 开启 `TreatWarningsAsErrors`；新增 warning 必须修复，不能随意增加 `NoWarn`。
- T-024 后 stdin pipe 和 JSONL stdin/stdout 模式已删除；自动化统一使用 `--server`。
- 升级 Agent 协议版本时，必须同步更新 C# `TurnRecord.CurrentProtocolVersion` 和 Python `tests/emuera_server.py:PROTOCOL_VERSION`。
- MAUI 只依赖 Core，不引入 Server/Cli 的 ASP.NET Core 依赖。
- 修改前端结构、组件或状态时，先查看 [`Emuera.Web/README.md`](Emuera.Web/README.md)。
- NativeAOT 结论以 [`docs/2026.8.4.安卓性能优化2/nativeaot-verify-report.md`](docs/2026.8.4.安卓性能优化2/nativeaot-verify-report.md) 为准，不在本文件重复论证。

## 环境陷阱

在 Windows/TRAE 环境中，不要使用 `Start-Process -RedirectStandardOutput ... -Wait` 捕获全量 `dotnet build` 输出。重定向管道可能因缓冲区填满而死锁。直接运行 `dotnet build`，或使用不会阻塞输出读取的方式。

## 文档索引

- 架构总览：[`ARCHITECTURE.md`](ARCHITECTURE.md)
- Web 前端：[`Emuera.Web/README.md`](Emuera.Web/README.md)
- 测试：[`tests/README.md`](tests/README.md)
- 终端行为教训：[`docs/LESSONS/terminal-windows.md`](docs/LESSONS/terminal-windows.md)
- 全部教训索引：[`docs/LESSONS/README.md`](docs/LESSONS/README.md)
- Issue tracker：[`docs/agents/issue-tracker.md`](docs/agents/issue-tracker.md)
- Triage labels：[`docs/agents/triage-labels.md`](docs/agents/triage-labels.md)
- Domain docs：[`docs/agents/domain.md`](docs/agents/domain.md)

## Agent skills

Issues live under `.scratch/<feature>/`. 使用仓库中 `.agents/skills/` 提供的专业流程；需要规划、实现、测试、审查或文档维护时，先加载匹配的 skill。
