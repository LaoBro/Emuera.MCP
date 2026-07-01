# Emuera

Eramaker 引擎的 C# 移植版，基于 .NET 运行。完整支持 ERB 脚本语言，并通过 MCP 协议支持 AI 代理控制。

本项目以 **Emuera.Headless** 无头运行器为主，支持 JSONL/CLI/HTTP 服务器三种协议。另有一个 WinForms 图形界面版本（仅 Windows）。

## 环境要求

- [.NET 10 SDK](https://dotnet.microsoft.com/download) 或更高版本
- Python 3.10+（用于 MCP 网关和测试）
- Windows（跨平台支持计划中，目前仅完成 Windows）

## 构建

构建无头运行器：

```bash
dotnet build Emuera.Headless/Emuera.Headless.csproj -c Debug
```

构建 WinForms 应用（NAudio 音频后端）：

```bash
dotnet build Emuera/Emuera.csproj -c Debug-NAudio
```

`Debug-NAudio` 是常规构建配置，因为默认的非 NAudio 配置依赖 Windows Media Player COM 引用。

发布无头运行器：

```bash
dotnet publish Emuera.Headless/Emuera.Headless.csproj -c Release --no-self-contained -o Emuera.Headless/bin/Release/Publish
```

> **注意：** 构建时会产生大量已有的 CS 警告（旧代码的 nullable、弃用 API 等），这些是已知且无害的，只关注 error 即可。

## 运行

以 JSONL 模式运行（适合脚本和自动化）：

```bash
dotnet exec Emuera.Headless/bin/Debug/net10.0/Emuera.Headless.dll --ExeDir <游戏目录> --protocol jsonl
```

以 CLI 模式运行（终端交互）：

```bash
dotnet exec Emuera.Headless/bin/Debug/net10.0/Emuera.Headless.dll --ExeDir <游戏目录> --protocol cli
```

运行 HTTP 服务器（MCP 网关使用此模式）：

```bash
dotnet exec Emuera.Headless/bin/Debug/net10.0/Emuera.Headless.dll --ExeDir <游戏目录> --server --port 8080
```

构建后运行 WinForms 应用：

```bash
dotnet exec Emuera/artifacts/bin/Emuera/debug-naudio/Emuera.dll --ExeDir <游戏目录>
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
  "binaryPath": "Emuera.Headless/bin/Debug/net10.0/Emuera.Headless.exe",
  "gameDir": "test_game"
}
```

该文件**不应提交到仓库**——每个开发者有自己的路径和构建配置。

### MCP 网关运行模式

嵌入模式（默认）——由 Python 启动和停止 C# 服务器：

```bash
python -m emuera_gateway --emuera-path Emuera.Headless/bin/Debug/net10.0/Emuera.Headless.exe --game-dir test_game
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

> T-024 后 `--protocol jsonl` 的 stdin/stdout 管道模式已废弃；脚本/自动化统一走 `--server`。交互式终端使用 `--protocol cli`（需真实 TTY）。

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

Python 测试使用仓库根目录的 `test_game` 作为测试游戏。Windows 环境下使用绝对路径更可靠。

```bash
python tests/test_jsonl.py --binary D:/LaoBro/Emuera.MCP/Emuera.Headless/bin/Debug/net10.0/Emuera.Headless.exe --game-dir test_game
python tests/test_server_single_session.py
python tests/test_tinput_timeout.py
python tests/test_force_quit_survival.py
python tests/run_all.py --binary D:/LaoBro/Emuera.MCP/Emuera.Headless/bin/Debug/net10.0/Emuera.Headless.exe --game-dir test_game
```

`tests/README.md` 是测试的权威文档。

## 项目结构

```
Emuera.Headless/   -- 无头运行器（主项目，JSONL/CLI/HTTP 服务器）
Emuera/            -- WinForms 图形界面版本
emuera_gateway/    -- Python MCP 网关
EmueraPluginExample/ -- 示例 C# 插件
tests/             -- Python 测试脚本和代理库
test_game/         -- 开发用最小 ERB 测试游戏
```

## 许可证

Copyright (C) 2008- MinorShift。社区维护分支。
