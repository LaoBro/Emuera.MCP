# Issue 003 — 输出扇出 + HTTP/WS 并存

> **Parent**: [PRD-WebSocket传输.md](./PRD-WebSocket传输.md) · Solution（并存 / 共享 OutputHub）/ US4
> **Part of**: WebSocket 传输实现（纵向切片 3/6）

## What to build

验证并保证 `OutputHub` 的**广播语义**：一个 turn 必须同时、无损地送达**所有**订阅者
（多个 WS 连接 + 一个 HTTP 长轮询客户端），且任一客户端提交输入都只推进 session
一次。本切片以"共享同一输出流"为验收目标，生产代码通常无需新增（002 已实现
`Subscribe`/广播），重点是扇出正确性与并存的回归验证。

- 多个 WS 订阅者各自拿到独立的 `ChannelReader`，`Publish` 向**全部** reader 各推一次。
- HTTP 长轮询客户端（读 `_output` Channel）与 WS 客户端消费的是**同一份** turn 字符串。
- 任意客户端（WS 或 HTTP）提交一次输入 → session 仅前进一个回合，不产生重复/丢失的
  turn。

## User stories covered

- US4（观察者连上 `/ws` 也能实时收到每个 turn——输出扇出）
- US6（HTTP 长轮询端点继续工作，与 WS 并存）

## Acceptance criteria

- [ ] 同一 session 上同时开 **2 个 WS 客户端 + 1 个 HTTP 长轮询客户端**，三者收到的
  turn 流内容一致（按序比较）。
- [ ] 任一客户端提交一次输入，session 仅前进一个回合；三个客户端各自恰好多收到一个
  新 turn，无重复、无丢失。
- [ ] 关闭一个 WS 客户端（`Unsubscribe`）后，其余订阅者不受影响、继续正常接收。
- [ ] 上述场景可借现有 `tests/test_jsonl.py` 的 HTTP seam + 一个临时 WS 客户端脚本
  复现（正式自动化见 Issue 004）。

## Blocked by

- Issue 002（端到端 WS 收发已通，才能验证多客户端扇出）
