# Emuera

Eramaker 引擎的 C# 移植版，基于 .NET 运行。完整支持 ERB 脚本语言，并通过 MCP 协议支持 AI 代理控制。

本项目以 **Emuera.Headless** 无头运行器为唯一维护目标。已拆分为三个项目：
- `Emuera.Headless.Cli` — CLI 交互模式 & HTTP 服务器模式入口（Exe）
- `Emuera.Headless.Core` — 无头核心库（Library，无 AspNetCore 依赖）
- `Emuera.Headless.Server` — HTTP 服务器组件（Library，引 AspNetCore）

另有 **MAUI 桌面/移动应用**（`Emuera.Maui`）用于原生窗口体验。`Emuera/` 目录保留 WinForms 专用源码作只读参考，**不再维护，不可独立构建**。

## 环境要求

- [.NET 10 SDK](https://dotnet.microsoft.com/download) 或更高版本
- Python 3.10+（用于 MCP 网关和测试）
- Node.js 18+（用于 Web 前端 Emuera.Web/）
- Windows（跨平台支持计划中，目前仅完成 Windows）

## 构建

### CLI & Server 模式（共享 `Emuera.Headless.Cli`）

```bash
dotnet build Emuera.Headless.Cli/Emuera.Headless.Cli.csproj -c Debug
```

发布单文件：

```bash
dotnet publish Emuera.Headless.Cli/Emuera.Headless.Cli.csproj -c Release --no-self-contained -o publish/cli
```

### MAUI Windows 桌面应用

```bash
dotnet build Emuera.Maui/Emuera.Maui.csproj -f net10.0-windows10.0.19041.0 -c Debug
```

MAUI 不支持 `PublishSingleFile`。发布需框架依赖或独立部署：

```bash
dotnet publish Emuera.Maui/Emuera.Maui.csproj -f net10.0-windows10.0.19041.0 -c Release
dotnet publish Emuera.Maui/Emuera.Maui.csproj -f net10.0-windows10.0.19041.0 -c Release --self-contained -r win-x64
```

### Android APK

```bash
dotnet publish Emuera.Maui/Emuera.Maui.csproj -f net10.0-android -c Release
```

需要 Android SDK + JDK 17+（`JAVA_HOME`）环境。

> **注意：** I-12 阶段 1 已启用按路径分级的质量护栏。`Emuera.Headless.Core/Shared/`（迁移自 `Emuera/`）下的历史警告已全局抑制。修改自有源码时应关注新引入的 CA/CS 警告。`Emuera.Headless.Cli` 和 `Emuera.Headless.Server` 启用 `TreatWarningsAsErrors`。

## 运行

### CLI 交互模式（需真实 TTY）

```bash
dotnet exec Emuera.Headless.Cli/bin/Debug/net10.0/Emuera.Headless.Cli.dll --ExeDir <游戏目录> --protocol cli
```

### HTTP 服务器模式（浏览器访问 http://localhost:8080）

```bash
dotnet exec Emuera.Headless.Cli/bin/Debug/net10.0/Emuera.Headless.Cli.dll --ExeDir <游戏目录> --server --port 8080
```

> `--protocol jsonl` 在非 server 模式下会报错；脚本/自动化统一走 `--server`。

### MAUI Windows 桌面应用

```bash
dotnet run --project Emuera.Maui/Emuera.Maui.csproj -f net10.0-windows10.0.19041.0 -c Debug
```

启动后自动解压内置 `test_game/` 到 AppData，WebView 加载 Vue 前端界面。

### Web 前端

前端为 Vue 3 + TypeScript SPA，开发时由 Vite 代理 HTTP/WS 到 C# Kestrel（:8080）。

```bash
cd Emuera.Web && npm install     # 安装依赖
npm run dev                      # Vite dev server → localhost:5173
npm run build                    # 生产构建 → dist/
npm test                         # Vitest 单元测试（12 个文件）
```

## MCP 集成

Emuera 通过 Model Context Protocol 被 AI 编程工具（Claude Code、VS Code、Cursor 等）控制。项目使用 Python 网关管理游戏进程生命周期，仅在需要时才启动游戏。

### 架构

```
Claude Code <-- MCP over stdio --> emuera_gateway <-- HTTP --> Emuera.Headless (C# 服务器)
```

- `emuera_gateway` 对外提供 MCP 协议，对内通过 HTTP 与 C# 服务器通信。
- 嵌入模式下由 Python 自动启动/停止 C# 服务器；独立模式下连接已运行的服务器。

### 配置（Claude Code）

1. 先构建项目（见上）。

2. 确认项目根目录存在 `.mcp.json`：

```json
{
  "mcpServers": {
    "emuera": {
      "command": "python",
      "args": ["-m", "emuera_gateway"]
    }
  }
}
```

3. 在 VS Code 设置（`settings.json`）中启用：

```json
{
  "enabledMcpjsonServers": ["emuera"]
}
```

4. 重启 Claude Code。MCP 工具立即可用——在调用工具之前不会出现游戏窗口。

### 路径配置

首次使用前，需要通过 `emuera_set_config` 工具配置 Emuera 二进制路径和游戏目录：

> 调用 `emuera_set_config`，传入 `binaryPath`（编译后的二进制路径，可以是 `.dll` 或 `.exe`）和 `gameDir`（游戏数据目录）。
>
> 路径可以是相对于项目根目录的相对路径，也可以是绝对路径。配置会自动保存到 `.emuera-mcp.json`。

也可以在项目根目录手动创建 `.emuera-mcp.json`：

```json
{
  "binaryPath": "Emuera.Headless.Cli/bin/Debug/net10.0/Emuera.Headless.Cli.exe",
  "gameDir": "test_game"
}
```

该文件**不应提交到仓库**——每个开发者有自己的路径和构建配置。

### MCP 网关运行模式

嵌入模式（默认）——由 Python 启动和停止 C# 服务器：

```bash
python -m emuera_gateway --emuera-path Emuera.Headless.Cli/bin/Debug/net10.0/Emuera.Headless.Cli.exe --game-dir test_game
```

独立模式——连接已运行的 C# 服务器：

```bash
python -m emuera_gateway --standalone --server-url http://localhost:8080
```

### 工具

| 工具 | 说明 |
|------|------|
| `emuera_step` | 提交输入并等待下一回合。传 `{"value": "0"}` 发送输入，传 `{}` 读取当前状态。 |
| `emuera_get_state` | 阻塞等待游戏进入 `WaitInput` 状态，然后返回当前状态和输出文本。 |
| `emuera_kill` | 强制关闭游戏进程和窗口。 |
| `emuera_set_config` | 设置二进制路径和/或游戏目录。传 `{"binaryPath": "...", "gameDir": "..."}`（参数可选），保存前验证路径有效性。 |
| `emuera_get_config` | 返回当前配置的二进制路径和游戏目录。 |

### 响应格式

每个工具的返回结果在 `content[0].text` 中，为一个 JSON 字符串：

```json
{
  "text": "=== 游戏输出 ===\n[0] Hello\n[1] Quit\n",
  "state": "WaitInput",
  "inputType": "IntValue",
  "needValue": true,
  "buttons": [{"label": "[0] Hello", "value": 0}]
}
```

| 字段 | 说明 |
|------|------|
| `text` | 本回合产生的所有输出文本 |
| `state` | `WaitInput` / `Running` / `Quit` / `Error` |
| `inputType` | `IntValue` / `StrValue` / `EnterKey` / `AnyKey` / `AnyValue` / `IntButton` / `StrButton` |
| `needValue` | 为 true 时表示需要非空输入 |
| `buttons` | 可见区域内的按钮列表，每项含 `label`（显示文本）和 `value`（输入值） |

### 其他 AI 工具

任何支持 stdio 传输的 MCP 客户端均可使用：

- **Claude Desktop** — 编辑 `%APPDATA%\Claude\claude_desktop_config.json`，填入相同配置
- **VS Code Copilot** — 创建 `.vscode/mcp.json`，使用 `"type": "stdio"` 及相同的 command/args
- **Cursor** — 创建 `.cursor/mcp.json`

## JSONL 协议

无头运行器的 JSONL 协议由 server 模式（`--server`）通过 HTTP 暴露，适合脚本和自动化。创建会话后游戏自动输出初始 turn，之后每发送一条输入命令返回一个 turn。

```python
# 游戏自动输出初始 turn（无需发送任何命令）：
{"text": "标题画面...", "state": "WaitInput", "inputType": "IntValue", "needValue": true, "buttons": [...]}

# 客户端发送输入：
{"type": "input", "value": "0"}

# 服务器响应下一 turn：
{"text": "输出文本...", "state": "WaitInput", "inputType": "IntValue", "needValue": true, "buttons": [...]}
```

参考 `tests/emuera_server.py`（server 模式测试 helper）和 `tests/test_jsonl.py`（示例）。

## 测试

测试分三层：**C# 单元测试**（xUnit）、**Python 端到端**（CLI/HTTP/WebSocket）、**前端测试**（Vitest）。

```bash
# C# 单元测试（xUnit，304 用例）
dotnet test Emuera.Headless.Tests/Emuera.Headless.Tests.csproj

# MAUI 单元测试（11 用例）
dotnet test Emuera.Maui.Tests/Emuera.Maui.Tests.csproj

# 前端测试（Vitest，13 个测试文件，224 用例）
cd Emuera.Web && npm test

# Python 端到端（使用 test_game）
python tests/test_jsonl.py --binary D:/LaoBro/Emuera.MCP/Emuera.Headless.Cli/bin/Debug/net10.0/Emuera.Headless.Cli.exe --game-dir test_game

# 全部回归测试（先 C# 单测+构建，再全量 Python 套件，14 套件）
python tests/run_all.py --binary D:/LaoBro/Emuera.MCP/Emuera.Headless.Cli/bin/Debug/net10.0/Emuera.Headless.Cli.exe --game-dir test_game
```

`tests/README.md` 是测试的权威文档。

## 项目结构

```
Emuera.Headless.Cli/    -- CLI 交互 & HTTP 服务器入口（Exe，唯一可运行的 Headless 入口）
Emuera.Headless.Core/   -- 无头核心库（Library，无 AspNetCore 依赖，MAUI 可直接引用）
Emuera.Headless.Server/ -- HTTP 服务器组件（Library，引 AspNetCore）
Emuera.Maui/            -- MAUI 桌面/移动应用（Windows + Android，原生 WebView 壳）
Emuera.Web/             -- Vue 3 + TypeScript 浏览器前端（Vite + Pinia + Vitest）
Emuera.Headless.Tests/  -- C# 单元测试（xUnit，304 用例）
Emuera.Maui.Tests/      -- MAUI 单元测试（xUnit，15 用例）
Emuera/                 -- WinForms 残留源码（不再维护，仅作只读参考，不可独立构建）
emuera_gateway/         -- Python MCP 网关
tests/                  -- Python 端到端测试脚本
build/                  -- MSBuild targets（VueBuild.targets 共享）
test_game/              -- 开发用最小 ERB 测试游戏
```

## 许可证

Copyright (C) 2008- MinorShift。社区维护分支。
