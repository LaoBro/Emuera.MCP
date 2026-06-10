# Emuera.Headless 测试说明

本目录用于 `Emuera.Headless` 的自动化验收与回归测试。

当前测试覆盖：

- JSONL 协议流程与 buttons schema。
- CLI 协议 redirected stdin/stdout 自动测试。
- server 单会话 HTTP API。
- TINPUT timeout server 场景。

## 测试游戏目录

新增或修订回归测试时，应使用仓库根目录的 `test_game` 文件夹作为测试游戏来源：

- 直接运行 JSONL / CLI 测试时，使用 `--game-dir test_game`。
- 不建议依赖开发者本机安装的其他 Emuera 游戏资源。
- 如 `test_game` 当前 ERB/CSV 场景不足以覆盖新增行为，应先补充 `test_game` 的测试资源，再扩展测试脚本。
- `test_game` 目前保留在仓库根目录，作为项目级 fixture；`tests/` 内的脚本通过 `PROJECT_DIR / "test_game"` 或 `--game-dir` 引用它。TINPUT timeout 测试会复制一份临时游戏目录，避免修改原始 `test_game`。

当前测试与 `test_game` 的耦合点：

- `test_jsonl.py` 依赖 `test_game/erb/TEST.ERB` 中的菜单文本和按钮值。
- `test_cli.py` 依赖 `test_game/erb/TEST.ERB` 中的 CLI 输出文本和按钮文本。
- `test_server_single_session.py` 通过 `tests/emuera_server.py` 固定使用根目录 `test_game`。
- `test_tinput_timeout.py` 会从根目录 `test_game` 复制临时副本，再覆盖 `erb/TEST.ERB` 构造 TINPUT 场景。

因此，这些测试确实与 `test_game` 强相关；目前没有必要把整个 `test_game` 移进 `tests/`，更适合保持根目录 fixture，并在测试 helper 中集中管理路径。

## 常用测试

构建后可运行单个测试：

```bash
python tests/test_jsonl.py --binary Emuera.Headless/bin/Debug/net10.0/Emuera.Headless.exe --game-dir test_game
python tests/test_cli.py --binary Emuera.Headless/bin/Debug/net10.0/Emuera.Headless.exe --game-dir test_game
python tests/test_server_single_session.py
python tests/test_tinput_timeout.py
```

也可以用一个入口运行常规回归：

```bash
python tests/run_all.py --binary Emuera.Headless/bin/Debug/net10.0/Emuera.Headless.exe --game-dir test_game
```

`run_all.py` 会顺序执行：

1. JSONL 流程 + buttons schema 测试。
2. CLI protocol redirected stdin/stdout 测试。
3. server 单会话测试。
4. TINPUT timeout 测试。

如需跳过 CLI protocol 测试：

```bash
python tests/run_all.py --skip-cli-protocol --binary Emuera.Headless/bin/Debug/net10.0/Emuera.Headless.exe --game-dir test_game
```

在 Windows 命令行中，如果相对路径启动失败，请使用绝对路径，例如：

```bash
D:/LaoBro/Emuera.MCP/Emuera.Headless/bin/Debug/net10.0/Emuera.Headless.exe
```

## JSONL 协议测试

`test_jsonl.py` 通过 `tests/emuera_agent.py` 启动：

```bash
Emuera.Headless.exe --ExeDir <game-dir> --protocol jsonl
```

测试流程：

1. 读取初始 JSONL turn。
2. 校验 `state`、`text`、`inputType`、`needValue`、`buttons` 字段。
3. 校验按钮包含 `label` 和整数 `value`。
4. 发送 `{"type":"input","value":"0"}` 推进游戏。
5. 验证第二轮菜单和最终 `Quit` 状态。

`emuera_agent.py` 已显式传入 `--protocol jsonl`，因此 JSONL 测试不再依赖 `stdin=PIPE` 自动检测。

## CLI protocol 测试

`test_cli.py` 使用 redirected stdin/stdout 测试 CLI 协议：

```bash
Emuera.Headless.exe --ExeDir <game-dir> --protocol cli
```

测试流程：

1. 启动 `Emuera.Headless.exe --protocol cli`。
2. 从 stdout 读取初始 CLI 输出。
3. 校验输出包含 `Agent Test Start`、`[0] Hello`、`[1] Quit`。
4. 向 stdin 写入 `0\n`。
5. 校验 CLI 输出包含 `You entered: 0`、`[0] World`、`[1] Exit`。
6. 再次向 stdin 写入 `0\n`。
7. 校验 CLI 输出包含 `Agent Test End` 并进入结束状态。

该测试不依赖真实 TTY，也不需要 ConPTY/WinPTY。真实终端交互仍由 `AgentCliProtocol` 的 `Console.KeyAvailable` / `Console.ReadKey(true)` 路径覆盖；CI 自动测试使用 redirected stdin 路径。

旧的 TTY-only CLI smoke 已移除：

- 不再使用 `--force-cli-smoke`。
- 不再使用 `--skip-cli-smoke`。
- 不再根据 `sys.stdin.isatty()` 跳过 CLI 测试。

## TINPUT timeout 测试

`test_tinput_timeout.py` 会从仓库根目录的 `test_game` 复制一份临时游戏目录，并在副本中写入 TINPUT timeout 场景，以避免修改原始 `test_game` 文件。

## Server 测试

`test_server_single_session.py` 使用仓库根目录的 `test_game` 启动 `Emuera.Headless --server`，验证：

- 第一个 `POST /sessions` 返回 `201`。
- 活跃会话期间第二个 `POST /sessions` 返回 `409`。
- 输入、turn 拉取、删除会话和删除后重建会话正常。

`test_tinput_timeout.py` 同样通过 server 模式验证 timeout turn 不会吞掉后续输入。
