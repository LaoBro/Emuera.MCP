# Emuera.Headless 测试说明

本目录用于 `Emuera.Headless` 的自动化验收与回归测试。

当前测试覆盖：

- JSONL 协议流程与 buttons schema（经 server 模式驱动，T-024 后 stdin 管道已废弃）。
- server 单会话 HTTP API。
- TINPUT timeout server 场景。
- fatal turn 脚本异常路径（THROW）。
- I-11 脚本退出后 server 存活。
- `/assets` 资源通道（issue 03）：路径消毒、扩展名白名单、缓存/CORS 头、协议帧 image segment 几何。

## 测试游戏目录

新增或修订回归测试时，应使用仓库根目录的 `test_game` 文件夹作为测试游戏来源：

- 直接运行测试时，使用 `--game-dir test_game`。
- 不建议依赖开发者本机安装的其他 Emuera 游戏资源。
- 如 `test_game` 当前 ERB/CSV 场景不足以覆盖新增行为，应先补充 `test_game` 的测试资源，再扩展测试脚本。
- `test_game` 目前保留在仓库根目录，作为项目级 fixture；`tests/` 内的脚本通过 `PROJECT_DIR / "test_game"` 或 `--game-dir` 引用它。TINPUT timeout 测试会复制一份临时游戏目录，避免修改原始 `test_game`。

当前测试与 `test_game` 的耦合点：

- `test_jsonl.py` 依赖 `test_game/erb/TEST.ERB` 中的菜单文本和按钮值。
- `test_server_single_session.py` 通过 `tests/emuera_server.py` 固定使用根目录 `test_game`。
- `test_tinput_timeout.py` 会从根目录 `test_game` 复制临时副本，再覆盖 `erb/TEST.ERB` 构造 TINPUT 场景。
- `test_force_quit_survival.py` 通过 `tests/emuera_server.py` 启动 server，分别用根目录 `test_game` 与临时副本覆盖 ERB。
- `test_assets.py` 依赖 `test_game/img/test.png`（8×4 RGBA 夹具，四角已知色）与 `TEST.ERB` 中的 `PRINT_IMG` 行。

因此，这些测试确实与 `test_game` 强相关；目前没有必要把整个 `test_game` 移进 `tests/`，更适合保持根目录 fixture，并在测试 helper 中集中管理路径。

## 常用测试

构建后可运行单个测试：

```bash
python tests/test_jsonl.py --binary Emuera.Headless/bin/Debug/net10.0/Emuera.Headless.exe --game-dir test_game
python tests/test_server_single_session.py
python tests/test_tinput_timeout.py
python tests/test_fatal_turn.py --binary Emuera.Headless/bin/Debug/net10.0/Emuera.Headless.exe --game-dir test_game
python tests/test_force_quit_survival.py
python tests/test_assets.py --binary Emuera.Headless/bin/Debug/net10.0/Emuera.Headless.exe --game-dir test_game
python tests/test_cli_basic.py --binary Emuera.Headless/bin/Debug/net10.0/Emuera.Headless.exe --game-dir test_game
python tests/test_cli_basic.py --game-dir test_game  # 自动查找 binary
```

也可以用一个入口运行常规回归：

```bash
python tests/run_all.py --binary Emuera.Headless/bin/Debug/net10.0/Emuera.Headless.exe --game-dir test_game
```

`run_all.py` 会顺序执行：

1. JSONL 流程 + buttons schema 测试（经 server 模式驱动）。
2. server 单会话测试。
3. TINPUT timeout 测试。
4. fatal turn 测试（脚本异常路径）。
5. I-11 exit survival 测试。
6. `/assets` 资源通道测试（issue 03）。
7. CLI 交互模式基础测试（happy path + ConPTY smoke tests）。

在 Windows 命令行中，如果相对路径启动失败，请使用绝对路径，例如：

```bash
D:/LaoBro/Emuera.MCP/Emuera.Headless/bin/Debug/net10.0/Emuera.Headless.exe
```

## JSONL 协议测试

`test_jsonl.py` 通过 `tests/emuera_server.py` 的 `start_server` 启动 server 模式：

```bash
Emuera.Headless.exe --server --port <port> --ExeDir <game-dir>
```

T-024 后 stdin 管道 JSONL 模式已废弃，`AgentJsonlProtocol` 仅由 server 模式的 `Session` 通过 `HttpSessionIO` 驱动。turn 结构（`text`/`state`/`inputType`/`needValue`/`buttons`）与原 stdin 管道完全一致，断言逻辑不变。

测试流程：

1. `POST /session` 创建会话。
2. `GET /turn` 读取初始 JSONL turn。
3. 校验 `state`、`text`、`inputType`、`needValue`、`buttons` 字段。
4. 校验按钮包含 `label` 和整数 `value`。
5. `POST /input {"value":"0"}` 推进游戏。
6. 验证第二轮菜单和最终 `Quit` 状态。

## CLI 交互模式（ConPTY smoke test）

T-024 后 stdin 管道 CLI 模式已废弃，`AgentCliProtocol` 仅支持交互式终端（VT 路径或 `ConsoleKey` 降级路径）。交互式 CLI 的终端渲染通过 `test_cli_basic.py` 在 Windows ConPTY（pywinpty）中进行黑盒验证。

`test_cli_basic.py` 通过 `PtyProcess.spawn` 启动 CLI 子进程，捕获原始 ConPTY 输出（含 VT 序列），验证以下路径：

| 测试 | 注入 ERB | 验证点 |
|------|----------|--------|
| happy path | 标准 TEST.ERB（PRINTL/INPUT/QUIT） | 菜单文本、按钮选择、推进到第二屏 |
| `clearline` | PRINTL → CLEARLINE 1 → PRINTL | 被删行内容不在输出中；后续行正常显示 |
| `clear` | 启动时 ClearOp | `\x1b[2J` 转义序列已发出；游戏文本正确显示 |
| `merge` | 连续两次 PRINT（无 NewLine） | "HelloWorld" 作为合并字符串出现 |
| `alignment` | PRINTC "AlignCheck" | 文本前有前导空格（PrintC 对齐填充，默认 25 字符） |
| `setbg` | SETBGCOLOR 0xFF0000 → PRINTL | `\x1b[48;2;255;0;0m` VT 背景色 escape 出现在输出字节流中 |

> **注意：** CLEAR 指令是 C# 内部调用（`ClearDisplay()`），非可用 ERB 命令；`test_cli_clear` 验证的是启动时 `ConsoleStateManager.Initialize()` 发出的系统 ClearOp。`test_cli_setbg` 仅对 VT 路径生效（ConPTY 默认开启 ANSI），非 VT 路径无背景色能力，不覆盖。

非 Windows 或无 pywinpty 时自动跳过。`run_all.py` 已包含 CLI 测试。

## TINPUT timeout 测试

`test_tinput_timeout.py` 会从仓库根目录的 `test_game` 复制一份临时游戏目录，并在副本中写入 TINPUT timeout 场景，以避免修改原始 `test_game` 文件。

## Server 测试

`test_server_single_session.py` 使用仓库根目录的 `test_game` 启动 `Emuera.Headless --server`，验证：

- 第一个 `POST /sessions` 返回 `201`。
- 活跃会话期间第二个 `POST /sessions` 返回 `409`。
- 输入、turn 拉取、删除会话和删除后重建会话正常。

`test_tinput_timeout.py` 同样通过 server 模式验证 timeout turn 不会吞掉后续输入。
`test_force_quit_survival.py` 验证 `@QUIT` / `FORCE_QUIT` 后 server 存活并能重启 session。
