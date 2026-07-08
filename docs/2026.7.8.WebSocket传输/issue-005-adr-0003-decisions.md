# Issue 005 — 记录决策 ADR-0003

> **Parent**: [PRD-WebSocket传输.md](./PRD-WebSocket传输.md) · Further Notes（建议补 ADR-0003）
> **Part of**: WebSocket 传输实现（纵向切片 5/6，纯文档）

## What to build

PRD 落地后补 **ADR-0003**，把本实现的三个关键决策与**被拒绝的替代方案**结构化记录，
供未来读者追溯。文档写入项目既有 `adr/` 目录（与 ADR-0001 / ADR-0002 同列）。

记录三项决策：

1. **Hub 旁路 vs 平行 SessionIO**
   - 决策：Hub 旁路（WS 是挂 `OutputHub` 的旁路传输，不实现另一个 `SessionIO`）。
   - 被拒绝：平行 `WebSocketSessionIO` 实现 `SessionIO` 4 方法——因 `Session` 硬耦合
     `HttpSessionIO` 且调用其专有方法 `ReadOutputAsync`/`EnqueueInput`，抽象
     `SessionIO` 上无这些成员，平行方案需重构 `Session`/`WaitForTurnAsync`/协议层，
     违背"协议引擎不动"。报告 2.3 的"只需 4 方法"预设已证伪。
2. **裸 JSON 文本帧 vs envelope**
   - 决策：裸帧（每 turn 一帧 = `TurnRecord` v2 JSON，输入帧 = `{"type":"input",...}`）。
   - 被拒绝：包 `{"kind":...}` envelope——主流前端框架无需；版本协商已由 initial turn
     的 `protocolVersion` 承载（ADR-0001），无需 WS 专属分发层。
3. **WS/HTTP 并存 vs 替换**
   - 决策：并存（保留 `/turn`+`/input`，新增 `/ws`，共享 `OutputHub`）。
   - 被拒绝：WS 完全替换 HTTP——会 break 现有客户端，且 HTTP 客户端本身可视为一个
     观察者，无必要互斥。

## User stories covered

- US8（未来读者需了解决策依据）

## Acceptance criteria

- [ ] `adr/0003-*.md` 已创建，标题体现"WebSocket 传输接入方式"。
- [ ] 含 Status / Context / Decision / Consequences / 被拒绝方案 五段式，与
  ADR-0001/0002 风格一致。
- [ ] 三项决策均列出"被拒绝的替代方案 + 拒绝理由"，并交叉引用 PRD-T5 / ADR-0001 /
  ADR-0002 / 架构评估报告 P0-2。
- [ ] PRD 的 Further Notes 中 "建议补 ADR-0003" 链接指向该文件。

## Blocked by

- Issue 002（决策在 002 落地后已具确定性，文档可随后补；不阻塞任何代码切片）
