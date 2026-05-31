# Emuera

Eramaker 引擎的 C# 移植版，基于 .NET + WinForms 运行。完整支持 ERB 脚本语言，并内置 MCP 协议支持 AI 代理控制。

## 环境要求

- [.NET 10 SDK](https://dotnet.microsoft.com/download) 或更高版本
- Windows（依赖 WinForms）

## 构建

```bash
dotnet build -c Debug-NAudio Emuera/Emuera.csproj
```

编译后的 DLL 位于 `Emuera/artifacts/bin/Emuera/debug-naudio/Emuera.dll`。

## 运行

```bash
dotnet exec Emuera/artifacts/bin/Emuera/debug-naudio/Emuera.dll --ExeDir <游戏目录>
```

加上 `--agent` 参数会启动代理模式（通过 stdio 使用 MCP/JSONL 协议），而非直接打开交互窗口。

## MCP 集成

Emuera 可以通过 Model Context Protocol 被 AI 编程工具（Claude Code、VS Code、Cursor 等）控制。项目使用 Python 中继脚本管理游戏进程生命周期，仅在需要时才启动游戏，打开编辑器时不会弹出窗口。

### 配置（Claude Code）

1. 先构建项目（见上）。

2. 确认项目根目录存在 `.mcp.json`：

```json
{
  "mcpServers": {
    "emuera": {
      "command": "python",
      "args": ["path/to/mcp_relay.py"]
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

首次使用前，需要通过 `emuera_set_config` 工具配置 Emuera.dll 路径和游戏目录：

> 调用 `emuera_set_config`，传入 `dllPath`（编译后的 DLL 路径）和 `gameDir`（游戏数据目录）。
>
> 路径可以是相对于项目根目录的相对路径，也可以是绝对路径。配置会自动保存到 `.emuera-mcp.json`。

也可以在项目根目录手动创建 `.emuera-mcp.json`：

```json
{
  "dllPath": "Emuera/artifacts/bin/Emuera/debug-naudio/Emuera.dll",
  "gameDir": "test_game"
}
```

该文件**不应提交到仓库**——每个开发者有自己的路径和构建配置。

### 工具

| 工具 | 说明 |
|------|------|
| `emuera_step` | 提交输入并等待下一回合。传 `{"value": "0"}` 发送输入，传 `{}` 读取当前状态。 |
| `emuera_get_state` | 阻塞等待游戏进入 `WaitInput` 状态，然后返回当前状态和输出文本。 |
| `emuera_kill` | 强制关闭游戏进程和窗口。 |
| `emuera_set_config` | 设置 DLL 路径和/或游戏目录。传 `{"dllPath": "...", "gameDir": "..."}`（参数可选），保存前验证路径有效性。 |
| `emuera_get_config` | 返回当前配置的 DLL 路径和游戏目录。 |

### 响应格式

每个工具的返回结果在 `content[0].text` 中，为一个 JSON 字符串：

```json
{
  "text": "=== 游戏输出 ===\n[0] Hello\n[1] Quit\n",
  "state": "WaitInput",
  "inputType": "IntValue",
  "needValue": true
}
```

| 字段 | 说明 |
|------|------|
| `text` | 本回合产生的所有输出文本 |
| `state` | `WaitInput` / `Running` / `Quit` / `Error` |
| `inputType` | `IntValue` / `StrValue` / `EnterKey` / `AnyKey` / `AnyValue` / `IntButton` / `StrButton` |
| `needValue` | 为 true 时表示需要非空输入 |

### 中继原理

```
Claude Code <-- MCP over stdio --> mcp_relay.py <-- MCP over stdio --> Emuera
```

- `mcp_relay.py` 常驻运行（无窗口、极低资源占用）。直接处理 `initialize`、`tools/list`、`emuera_kill` 请求。
- 当 `emuera_step` 或 `emuera_get_state` 被调用时，中继通过 `dotnet exec` 启动 Emuera，完成 MCP 握手，然后转发请求。
- 游戏进入 `Quit` 或 `Error` 状态时，中继自动杀掉进程并清理。
- 下次工具调用会启动全新的游戏进程。

### 直接连接（不使用中继）

如果希望 Emuera 直接作为 MCP 服务器运行，可将 `.mcp.json` 配置为指向 DLL：

```json
{
  "mcpServers": {
    "emuera": {
      "command": "dotnet",
      "args": [
        "exec", "path/to/Emuera.dll",
        "--agent", "--ExeDir", "path/to/game"
      ]
    }
  }
}
```

注意：这种方式下 MCP 连接建立时会立即弹出游戏窗口。

### 其他 AI 工具

任何支持 stdio 传输的 MCP 客户端均可使用：

- **Claude Desktop** -- 编辑 `%APPDATA%\Claude\claude_desktop_config.json`，填入相同配置
- **VS Code Copilot** -- 创建 `.vscode/mcp.json`，使用 `"type": "stdio"` 及相同的 command/args
- **Cursor** -- 创建 `.cursor/mcp.json`

## JSONL 协议

Emuera 同时支持更简单的 JSON-Lines 协议（通过首行输入自动检测），适合脚本和自动化。

```python
# 客户端发送：
{"type": "input", "value": "0"}

# 服务器响应：
{"text": "输出文本...", "state": "WaitInput", "inputType": "IntValue", "needValue": true}
```

参考 `tests/emuera_agent.py`（Python 封装库）和 `tests/test_jsonl.py`（示例）。

## 项目结构

```
Emuera/          -- 主程序（C# / WinForms）
tests/           -- Python 测试脚本和代理库
test_game/       -- 开发用最小 ERB 测试游戏
mcp_relay.py     -- MCP 中继服务器（Python）
```

## 许可证

Copyright (C) 2008- MinorShift。社区维护分支。
