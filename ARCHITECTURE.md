# Emuera Architecture

本文档是当前项目架构的总览入口。它描述当前有效的项目边界、依赖关系和主要运行时流程；历史迁移过程和具体任务决策仍以 `docs/` 下的专题文档为准。

## 维护边界

当前维护范围：

- `Emuera.Headless.Core`：无头运行器核心。
- `Emuera.Headless.Server`：HTTP/WebSocket 服务器组件。
- `Emuera.Headless.Cli`：CLI 和 Server 模式入口。
- `Emuera.Maui`：Windows/Android MAUI 应用壳和 C# 桥接。
- `Emuera.Web`：Vue 3 前端。

不在当前维护范围：

- `Emuera/`：旧 WinForms 源码，仅作行为参考。
- `EmueraPluginExample/`：旧插件示例，不属于当前解决方案主路径。
- `experiments/`：一次性实验项目，不作为产品架构依赖。

## 项目关系

```text
                         +----------------------+
                         |   Emuera.Web (Vue)   |
                         +----------+-----------+
                                    |
                    +---------------+----------------+
                    |                                |
              HTTP/WebSocket                      MAUI Bridge
                    |                                |
       +------------v-------------+       +----------v----------+
       | Emuera.Headless.Server   |       |    Emuera.Maui      |
       +------------+-------------+       +----------+----------+
                    |                                |
                    +---------------+----------------+
                                    |
                         +----------v-----------+
                         | Emuera.Headless.Core |
                         +----------------------+

       Emuera.Headless.Cli -> Core + Server
       Emuera.Headless.Tests -> Core + Server
       Emuera.Maui.Tests -> Maui
```

依赖边界：

- `Core` 不依赖 ASP.NET Core 或 MAUI。
- `Server` 依赖 `Core`，提供 Kestrel、HTTP 会话、WebSocket 广播和输入队列。
- `Cli` 依赖 `Core` 和 `Server`；Android RID 下通过条件编译排除 Server。
- `Maui` 只依赖 `Core`，通过 `MauiBridgeIO` 和 `BridgeHost` 连接游戏循环。
- `Web` 通过 HTTP/WS 或 MAUI JavaScript bridge 消费同一套回合/显示语义。

## Core

`Emuera.Headless.Core/` 是唯一维护中的引擎核心，负责：

- 游戏路径、配置、预加载和运行时初始化。
- `EmueraConsole`、显示状态、输入请求和按钮状态。
- Agent 协议、CLI 终端循环和终端渲染。
- `MauiBridgeIO` 等不依赖 ASP.NET Core 的输入/输出抽象。
- 从旧 WinForms 源码迁移而来的共享运行时，位于 `Shared/`。

主要区域：

| 路径 | 职责 |
|---|---|
| `Agent/` | Agent 协议、回合生成和按钮逻辑 |
| `Terminal/` | CLI/VT 输入、终端解析、渲染和滚动 |
| `Server/` | Core 内的传输抽象，例如 `MauiBridgeIO` |
| `UI/Game/` | `EmueraConsole` 和游戏控制台状态 |
| `Shared/` | 从旧 WinForms 迁移的共享运行时和 UI 代码 |
| `Headless/` | 无头替换实现，如声音、剪贴板和字符串测量 |

`EmueraConsole` 是核心游戏控制台状态对象，不属于 Server 或 Web 层。Server、MAUI 和 Agent 都通过它或其桥接状态消费游戏输出。

## Server

`Emuera.Headless.Server/` 提供单进程、单活跃会话的 HTTP 服务：

- `KestrelGameServer`：路由、会话生命周期和 `/load-game`。
- `Session`：持有一次游戏会话的 console、protocol 和 IO。
- `HttpSessionIO`：HTTP 输入队列、输出队列和超时处理。
- `OutputHub`：向 WebSocket 客户端旁路广播输出。

服务器会话约束：

- 同一时间只有一个活跃 `Session`。
- `POST /sessions` 用于创建会话；已有活跃会话时返回冲突。
- `POST /load-game` 负责校验目录、重建运行时和创建新会话。
- `DELETE /session` 结束当前会话并回到空闲态。
- `GET /ws` 与 HTTP 长轮询共用同一会话输入通道。

## CLI

`Emuera.Headless.Cli/` 是统一可执行入口：

- `HeadlessEntry`：解析命令行选项并选择运行模式。
- `HeadlessRunner`：交互式 CLI 模式。
- `ServerRunner`：启动 `KestrelGameServer`。
- `HeadlessOptions`：`--ExeDir`、`--server`、`--port` 和协议选项。

T-024 后已删除 stdin pipe 和 JSONL stdin/stdout 模式。脚本、自动化和 MCP 集成统一使用 Server 模式。

## MAUI

`Emuera.Maui/` 是 Windows/Android 应用壳：

- `MainPage` 承载 WebView。
- `BridgeHost` 管理 C# 游戏循环、输入和前端消息。
- `JsBridge/` 提供 Windows/Android 平台桥接实现。
- `MauiProgram` 负责 MAUI 依赖注入和应用启动。
- `MauiBridgeIO` 使核心游戏循环通过内存 Channel 与前端交互。

MAUI 启动和游戏选择流程：

```text
MainPage / MauiProgram
  -> 初始化 BridgeHost，但不自动启动游戏
  -> Vue 注册 window.__emueraOnTurn / window.__emueraOnMessage
  -> MauiGameList 扫描游戏目录
  -> 用户选择游戏
  -> loadGameFromPath(fullPath)
  -> C# 重建运行时并启动游戏循环
  -> BridgeHost.PostTurn()
  -> Vue game store.applyTurn()
  -> TerminalDisplay 渲染
```

MAUI 不加载 `Emuera.Headless.Server` 或 `Emuera.Headless.Cli`，以避免 Android 目标引入 ASP.NET Core 依赖。

## Web

`Emuera.Web/` 是 Vue 3 SPA，详细页面、组件、Store 和修改入口见 [`Emuera.Web/README.md`](Emuera.Web/README.md)。

前端有两条传输路径：

```text
HTTP 模式:
Web -> WebSocket/HTTP -> Headless.Server -> Core

MAUI 模式:
WebView -> IJsBridge/BridgeHost -> Core
```

两种模式共享：

- 回合状态和显示状态语义。
- 输入类型、按钮值和终端推进语义。
- 图片、图形和背景的结构化显示模型。

差异只在传输层：HTTP 模式使用网络端点，MAUI 模式使用进程内桥接。

## 协议和状态流

核心回合流程：

```text
游戏脚本
  -> EmueraConsole / GameLoop
  -> AgentJsonlProtocol
  -> TurnRecord
  -> HTTP/WS 或 MauiBridgeIO
  -> game store
  -> DisplayState / TerminalDisplay
```

常见状态：

- `Idle`：没有活跃游戏会话。
- `Loading`：正在加载或重建游戏。
- `WaitInput`：游戏等待用户输入。
- `Quit`：游戏正常结束。
- `Error`：游戏或协议发生致命错误。

协议版本变更时，必须同步更新 C# `TurnRecord.CurrentProtocolVersion` 和 Python 侧 `tests/emuera_server.py:PROTOCOL_VERSION`。

## 构建边界

- `Emuera.Headless.Cli` 的 Vue 构建通过 `build/VueBuild.targets` 集成。
- CLI 使用 `Emuera.Web/dist/` 和默认 Vite base。
- MAUI 使用独立的 `dist-maui/` 和相对 Vite base，避免与 CLI 构建互相覆盖。
- CLI 发布时 `wwwroot/` 是随 exe 分发的外部静态文件目录，不嵌入单文件 exe。
- MAUI 不支持 `PublishSingleFile`；应使用框架依赖或独立发布。

## 相关文档

- 前端导航：[`Emuera.Web/README.md`](Emuera.Web/README.md)
- 测试说明：[`tests/README.md`](tests/README.md)
- 文件迁移历史：[`docs/2026.6.30.架构健壮性重构/T-023前置-文件结构整理方案.md`](docs/2026.6.30.架构健壮性重构/T-023前置-文件结构整理方案.md)
- NativeAOT 验证：[`docs/2026.8.4.安卓性能优化2/nativeaot-verify-report.md`](docs/2026.8.4.安卓性能优化2/nativeaot-verify-report.md)
- 终端行为教训：[`docs/LESSONS/terminal-windows.md`](docs/LESSONS/terminal-windows.md)
