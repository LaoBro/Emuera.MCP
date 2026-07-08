# Issue 002 — 端到端 WS 双向帧（tracer bullet）

> **Parent**: [PRD-WebSocket传输.md](./PRD-WebSocket传输.md) · Solution / Wire Format / 生命周期
> **Part of**: WebSocket 传输实现（纵向切片 2/6）

## What to build

这是打通全层的 tracer bullet：在 Kestrel server 上落地一个**可工作的** `GET /ws`
WebSocket 端点，连上后能完整收发——验证"旁路传输 + 裸 JSON 帧"整条链路。

- `KestrelGameServer`：`builder` 构建后调用 `app.UseWebSockets()`；server 持有当前
  session 的 `OutputHub` 引用（生命周期与现有 `_session` 一致）。
- 新增 `MapGet("/ws", handler)`：
  - **升级门禁（生命周期 5a）**：无活跃 session 时以关闭帧退回（建议
    `WebSocketCloseStatus` 自定义 4004 / `InternalServerError`），客户端应提示
    "请先创建会话"；有 session 时接受升级。
  - **发送循环**：从 `OutputHub.Subscribe()` 取 reader，每读到一个 turn 即
    `WebSocket.SendAsync` 发一帧**裸 JSON 文本**（内容 = `TurnRecord` v2 JSON，与
    `GET /turn` 响应体字节一致）。
  - **接收循环**：读文本帧 → `JsonSerializer.Deserialize` → 校验 `type == "input"`
    → `HttpSessionIO.EnqueueInput(value)` 喂进同一条输入 Channel。
  - 任一侧结束 / WS 关闭 → 取消另一循环并 `Unsubscribe`。
  - **Session 结束**以 WS 关闭帧通知（无额外 payload）。

客户端约定：先 `POST /session`（与 HTTP 对称，拿到 `201` 并启动游戏循环），再
`GET /ws` 附着。单会话模型下 URL 不带 `?sessionId=`。

## User stories covered

- US1（持久连接，服务端主动推送，无需 50ms 轮询）
- US2（WS 帧与 `GET /turn` 是同一份 v2 JSON，可复用 op 渲染器）
- US3（发送 `{"type":"input","value":"..."}` 走与 HTTP 相同输入 Channel）
- US5（协议/引擎层零改动）

## Acceptance criteria

- [ ] `UseWebSockets()` 已启用，`/ws` 端点存在。
- [ ] 未 `POST /session` 直接连 `/ws` → 连接被关闭（断言 close 状态/原因）。
- [ ] `POST /session` 后连 `/ws`，收到的**首帧**为 `TurnRecord` v2 JSON：
  `protocolVersion == 2`、`ops` 为 list 且非空、不含旧版 `text`/`buttons` 字段。
- [ ] 发送 `{"type":"input","value":"..."}` 帧 → 收到下一 turn 帧（step turn **不带**
  `protocolVersion`）。
- [ ] 同一局的 WS 首帧与 `GET /turn` 首帧**语义等价**（同样断言 `ops[]` 结构），
  验证"WS 帧 = HTTP 响应体"。
- [ ] Session 结束 / `POST /session` DELETE → WS 收到关闭帧。
- [ ] `dotnet build` 成功；可用一个最简 WS 客户端（如 Python `websockets`）手动演示
  上述收发。

## Blocked by

- Issue 001（OutputHub 主干已就位，本端点才能订阅到输出流）
