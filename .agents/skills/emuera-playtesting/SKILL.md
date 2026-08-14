---
name: emuera-playtesting
description: Playtest Emuera games via emuera_agent. Use when driving test_game or an ERB game, handing control to the user, or recovering after a steal or CONTROL_LOST.
---

# Emuera Playtesting

用 `python -m emuera_gateway <subcommand>` 操控本仓库的 Headless 游戏。一次调用 = 一个回合；stdout 只有 JSON，错误在 stderr + 非零退出码。

术语（Controller / Spectator / Acquire / Release / Steal / Lease / Control Event / Controller Kind）以根目录 [`CONTEXT.md`](../../../CONTEXT.md)「控制权交接」为准。契约见 [`.scratch/control-handoff/spec.md`](../../../.scratch/control-handoff/spec.md)。回合字段由 `AgentJsonlProtocol` / `TurnRecord` 决定，这里不复述协议。

## 1. 启动序列

```bash
python -m emuera_gateway start --game-dir test_game
python -m emuera_gateway acquire
```

`start` 会拉起 `Emuera.Headless.Cli --server`，或复用已在跑的 server（读/写项目根 `.emuera-server.json`）。其它子命令在记录文件缺失或端口不通时直接报错退出；只有要自己开局或确认复用时才再 `start`。

完成标准：`acquire` 的 stdout 含 `controller.kind == "agent"`，以及 `state` / `turn` / `turnsAdvanced`。先读确认再 `step`。全屏细节按需另调 `GET /snapshot`。

## 2. 回合循环

```bash
python -m emuera_gateway step --value <输入>
```

每步之后看 `state`：

| `state` | 下一步 |
|---|---|
| `WaitInput` | 按 `inputType` / `needValue` / 按钮值再 `step` |
| `Quit` / `Error` | 走第 5 步收尾 |
| 其它 | `status` 确认后再决定 |

完成标准：每一次 `step` 都对应自己刚提交的输入；不要在未读确认时连打。

### 2.1 自动推进（advance）

遇到 `EnterKey` / `AnyKey`（`needValue=false`）且判定为无聊翻页时，用 `advance` 一次推进到需要真实输入，不必逐条 `step --value ""`：

```bash
python -m emuera_gateway advance --max-steps 50
```

- 返回单行 JSON：`{"turns":[...], "stopped":{...}, "advancedCount":N}`，`turns` 是每个被推进回合（含内容），`stopped` 是需要真实输入的回合。
- 结束后读 `stopped` 回合再决定下一步；被强夺时 advance 自动停止并报 `CONTROL_LOST`，照第 3 步处理。
- 只在确认是翻页时用；若该 EnterKey 是确认框/剧情节点，就正常 `step` 停下处理。

## 3. 被强夺

`step` / `GET /turn` 报 `CONTROL_LOST`，或 `GET /control/wait` 收到 `stolen`：

1. 立刻停下，不再 `step`。
2. 向用户说明停在哪一回合、最后一次输入、当前 `state`。
3. 等用户让权后再 `acquire` 读确认（看 `turnsAdvanced`）。

完成标准：被强夺后零次自动 `acquire`。

## 4. 用户持有时等待

`acquire` 报 `CONTROL_HELD_BY_USER`，或 `status` 显示 `controller.kind == "user"`：

1. 向用户说明你在等，并报当前进度。
2. 长轮询 `GET /control/wait`（204 表示本轮无事件，继续同一条长轮询）。

```bash
curl -sS "http://127.0.0.1:8080/control/wait"
```

主机和端口以 `.emuera-server.json` 为准。`released` / `lease_expired` 后再 `acquire`。若事件是 `game_ended`，走第 5 步。

完成标准：等待期间没有连续的 `acquire` / `step` 重试。

## 5. 游戏结束

`state` 为 `Quit` / `Error`，或收到 `game_ended`：会话会清掉 Controller，不必再 `release`。若 `.emuera-server.json` 里 `startedByAgent` 为真（这次 server 是你拉起的），立刻：

```bash
python -m emuera_gateway stop
```

完成标准：自己拉起的 server 已停，`.emuera-server.json` 已删除。

## 6. 任务完成或把游戏交给用户

游戏还在进行、任务结束，或用户要接手：

```bash
python -m emuera_gateway release
```

`release` 只让权，不关 server。只有你启动的 server、且整局结束时才 `stop`。

完成标准：用户侧 Web / MAUI 可以输入；你不再 `step`。

## 约束

- 同一时刻一个活跃会话、一个 Controller。第二个 agent 的 `acquire` 得 `CONTROL_HELD`——停下并告诉用户，不要并行开第二局。
- `watch` 只读旁观，不能从终端接管；接管入口在 Web / MAUI（旁观横幅或 Ctrl+T）。
- 身份 token 由 `start` 写入 `.emuera-server.json`，后续命令自动携带。文件缺失时按 CLI 报错处理；不要手造 token。
