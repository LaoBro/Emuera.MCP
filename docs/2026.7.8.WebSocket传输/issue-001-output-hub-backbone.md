# Issue 001 — OutputHub 广播中枢 + HttpSessionIO 旁路发布（prefactor）

> **Parent**: [PRD-WebSocket传输.md](./PRD-WebSocket传输.md) · Solution / Implementation Decisions
> **Part of**: WebSocket 传输实现（纵向切片 1/6）

## What to build

新建广播中枢 `OutputHub`，作为所有「输出观察者」的唯一发布点，并把游戏循环已有的
输出写入动作旁路广播出去。这是让后续 WS 端点实现变简单的 prefactor——"make the
change easy, then make the easy change"。

- 新增 `OutputHub`：
  - `Publish(string turn)`：向所有当前订阅者推送一个 turn。
  - `Subscribe()`：返回一个全新的 `ChannelReader<string>`，供新观察者消费
    （每个 WS 连接各一个；HTTP 长轮询仍读其原有 `_output` Channel，此处不改动）。
  - `Unsubscribe(ChannelReader)`：移除订阅者（WS 断开 / session 结束时调用）。
- 修改 `HttpSessionIO`：注入 `OutputHub`；`WriteLine(text)` 在执行原有
  `_output.Writer.TryWrite(text)` **之后**追加 `_hub.Publish(text)`。
  `ReadLineAsync` / `Close` / `IsConnected` / `EnqueueInput` **保持不变**。
- **`Session` / `AgentJsonlProtocol` / 游戏引擎层零改动**（对应 US5）。

`WriteLine` 写出的 `turn` 字符串即 `TurnRecord` v2 JSON（已是合法 JSON），无需任何
转换即可被 WS 端点和 HTTP 客户端复用。

## User stories covered

- US5（Session / 引擎层完全不被触碰）
- US6（HTTP 端点继续工作，本切片不改变 HTTP 行为）
- 为 US4（输出扇出）打地基

## Acceptance criteria

- [ ] `OutputHub` 提供 `Publish(string)` / `Subscribe() → ChannelReader<string>` / `Unsubscribe(ChannelReader)`。
- [ ] `HttpSessionIO` 注入 `OutputHub`；`WriteLine(text)` 在写 `_output` 后调用 `_hub.Publish(text)`。
- [ ] `ReadLineAsync` / `Close` / `IsConnected` / `EnqueueInput` 签名与行为不变。
- [ ] `Session` / `AgentJsonlProtocol` / 游戏引擎无任何修改。
- [ ] `dotnet build Emuera.Headless/Emuera.Headless.csproj -c Debug` 成功，无新增 error。
- [ ] 一个进程内订阅者（单元/集成测试级别）收到的 turn 字符串与写入 `_output` 的完全一致，且每产生一个 turn 恰好收到一次。
- [ ] 现有 HTTP 长轮询测试（`tests/test_jsonl.py` 相关）不受影响、仍通过。

## Blocked by

None — 可立即开始。
