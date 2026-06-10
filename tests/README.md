# Emuera.Headless 测试说明

本目录用于 Emuera.Headless 的自动化验收与回归测试。

## 测试游戏目录

新增或修订回归测试时，应使用仓库根目录的 `test_game` 文件夹作为测试游戏来源：

- 直接运行 JSONL 测试时，使用 `--game-dir test_game`。
- 不建议依赖开发者本机安装的其他 Emuera 游戏资源。
- 如 `test_game` 当前 ERB/CSV 场景不足以覆盖新增行为，应先补充 `test_game` 的测试资源，再扩展测试脚本。
- `test_game` 目前保留在仓库根目录，作为项目级 fixture；`tests/` 内的脚本通过 `PROJECT_DIR / "test_game"` 或 `--game-dir` 引用它。TINPUT timeout 测试会复制一份临时游戏目录，避免修改原始 `test_game`。

当前测试与 `test_game` 的耦合点：

- `test_jsonl.py` 直接依赖 `test_game/erb/TEST.ERB` 中的菜单文本和按钮值。
- `test_server_single_session.py` 通过 `tests/emuera_server.py` 固定使用根目录 `test_game`。
- `test_tinput_timeout.py` 会从根目录 `test_game` 复制临时副本，再覆盖 `erb/TEST.ERB` 构造 TINPUT 场景。

因此，这些测试确实与 `test_game` 强相关；目前没有必要把整个 `test_game` 移进 `tests/`，更适合保持根目录 fixture，并在测试 helper 中集中管理路径。

## 常用测试

构建后可运行单个测试：

```bash
python tests/test_jsonl.py --binary Emuera.Headless/bin/Debug/net10.0/Emuera.Headless.exe --game-dir test_game
python tests/test_server_single_session.py
python tests/test_tinput_timeout.py
```

也可以用一个入口运行常规回归：

```bash
python tests/run_all.py --binary Emuera.Headless/bin/Debug/net10.0/Emuera.Headless.exe --game-dir test_game
```

`run_all.py` 会顺序执行：

1. JSONL 流程 + buttons schema 测试。
2. 非管道 CLI 启动 smoke。
3. server 单会话测试。
4. TINPUT timeout 测试。

如需跳过 CLI smoke：

```bash
python tests/run_all.py --skip-cli-smoke
```

当前自动化环境通常没有真实 TTY；`run_all.py` 会在 stdin 非 TTY 时跳过 CLI smoke 并标记为 `SKIP`。如需强制检查，可运行：

```bash
python tests/run_all.py --force-cli-smoke
```

在 Windows 命令行中，如果相对路径启动失败，请使用绝对路径，例如：

```bash
D:/LaoBro/Emuera.MCP/Emuera.Headless/bin/Debug/net10.0/Emuera.Headless.exe
```

## CLI smoke 测试

`run_all.py` 默认包含一个轻量 CLI smoke：

- 以非管道 stdin 启动 `Emuera.Headless.exe --ExeDir <game>`。
- 确认进程能保持运行一段时间。
- 确认没有输出 JSONL turn，避免把管道模式误判为 CLI 模式。

当前环境无法稳定模拟真实 TTY 的 Backspace / Enter / Escape 输入，因此 CLI smoke 只覆盖启动与非 JSONL 行为；完整 TTY 输入行为仍建议人工验证。

## TINPUT timeout 测试

`test_tinput_timeout.py` 会从仓库根目录的 `test_game` 复制一份临时游戏目录，并在副本中写入 TINPUT timeout 场景，以避免修改原始 `test_game` 文件。

## Server 测试

`test_server_single_session.py` 使用仓库根目录的 `test_game` 启动 `Emuera.Headless --server`，验证：

- 第一个 `POST /sessions` 返回 `201`
- 活跃会话期间第二个 `POST /sessions` 返回 `409`
- 输入、turn 拉取、删除会话和删除后重建会话正常
