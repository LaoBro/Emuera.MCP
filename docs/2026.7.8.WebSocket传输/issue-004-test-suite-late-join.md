# Issue 004 — WS 集成测试套件 test_ws.py + 晚加入

> **Parent**: [PRD-WebSocket传输.md](./PRD-WebSocket传输.md) · Testing Decisions
> **Part of**: WebSocket 传输实现（纵向切片 4/6）

## What to build

新增**最高层 WS 集成测试 seam**（`tests/test_ws.py`），复用现有 HTTP 集成 seam 的
"启动 `Emuera.Headless` + 指定 game-dir" 模式，并把 PRD 的断言要点固化为自动化用例。
延续 PRD-T5 "只测 wire format，不测内部实现" 的哲学。

断言要点（每条对应一个测试函数）：

- **连接前置**：未 `POST /session` 直接连 `/ws` → 连接被关闭（断言 close 状态）。
- **初始帧**：`POST /session` 后连 `/ws`，首帧 `TurnRecord` v2 JSON
  （`protocolVersion == 2`、`ops` 非空、无 `text`/`buttons`）。
- **帧格式一致**：同一局 WS 首帧与 `GET /turn` 首帧语义等价（`ops[]` 结构一致）。
- **输入往返**：发 `{"type":"input","value":"..."}` → 收到下一 turn 帧（step turn 不带
  `protocolVersion`）。
- **扇出**：2 个 WS 同时连，提交一次输入 → 两个连接都收到同一下一个 turn（验证
  `OutputHub` 广播）。
- **HTTP/WS 并存**：1 个 HTTP 长轮询 + 1 个 WS 同时消费同一 session，均收到一致 turn 流。
- **晚加入**：第二个 WS 在首个 turn 之后才连接 → 只收到后续 turn，不重复历史
  （`Subscribe()` 返回全新 Channel，之前 op 不可见）。
- **协议/引擎回归**：`tests/run_all.py` 全套通过，确认 WS 增量未破坏 server 单会话、
  TINPUT timeout、`test_jsonl.py` 等既有场景。

## User stories covered

- US7（WS 集成测试复用现有 seam，断言帧格式与扇出）
- US4 / US6（扇出与并存的可验证性）

## Acceptance criteria

- [ ] `tests/test_ws.py` 存在，覆盖上述全部断言要点，可独立运行
  `python tests/test_ws.py --binary <path> --game-dir test_game` 且全绿。
- [ ] 晚加入用例明确断言：第二个连接**未**收到其连接前已产生的 turn。
- [ ] `python tests/run_all.py --binary <path> --game-dir test_game` 全套通过。
- [ ] 测试不依赖任何内部类型/私有字段，仅通过 `ws://localhost:<port>/ws` 与
  `POST /session`、`GET /turn`、`POST /input` 公共契约验证。

## Blocked by

- Issue 003（扇出与并存行为已验证，本切片将其固化为自动化回归）
