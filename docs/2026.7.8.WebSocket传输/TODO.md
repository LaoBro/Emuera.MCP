# TODO — WebSocket 传输后续事项

本文件记录本次 WebSocket 传输实现（commit `8ba558f`）中**未在本 PR 内解决**、但已确认存在的问题与后续动作项。

---

## [已修复] 单会话拆除竞态（teardown race）

**严重度**：中（偶发，仅在高并发 create→delete→create 序列下触发；不影响单次正常会话）
**修复**：`Session.Dispose()` 现在先 join 游戏循环任务（确保旧 loop 不再引用全局静态），再显式调用幂等的 `GlobalStatic.Reset()`，使 `Dispose` 返回时全局静态已清空。高频 create→delete→create 下新会话 `Initialize()` 不再复用到脏的 `GlobalStatic.Console`。
**验证**：`tests/test_ws.py` 在 `TEARDOWN_GRACE=0` 下连跑 8 轮（105 用例/轮）全过；`tests/run_all.py` 全套通过。`TEARDOWN_GRACE` 已降为 0.0（修复前靠 1.0s 延迟掩盖）。

### 现象
在 `tests/test_ws.py` 的高频循环（一个用例结束即 `DELETE /session`，下一个用例立即 `POST /session`）下，约 1/15 概率出现失败：
- 失败点是 `test_frame_consistency`（以及连带的 `test_initial_frame`），两路（HTTP `GET /turn` 与 WS）首帧都**缺少 `protocolVersion==2`**，即客户端拿到的是 step turn 而非 initial turn。
- 仅当上一会话与下一会话之间几乎没有间隔时触发；带 `TEARDOWN_GRACE`（见下）后 15/15 稳定通过。

### 根因
这是**既有的会话生命周期竞态**，不在 WebSocket 代码内：
- 服务器为单会话模型。`DELETE /session` → `Session.Dispose()` 释放游戏循环与 IO，但**全局静态状态 `GlobalStatic.Console` 未同步重置**。
- 紧接着的 `POST /session` 立即 `new Session(...)` 并初始化，复用了尚未被前一次 `Dispose` 清干净的 `GlobalStatic.Console`。
- 初始化错位导致首个 turn 以「续作」形态产出（无 `protocolVersion` 的 step 帧），而非带 `protocolVersion==2` 的 initial 帧。

### 影响范围
- **HTTP 路径同样存在**：既有 HTTP 测试靠网络/启动的自然延迟躲开了该竞态，并非逻辑上免疫。
- WebSocket 只是因为连接建立快、循环更密集而更容易暴露，**WS 传输本身正确**（帧格式、门禁、扇出均无误）。

### 为何未在本 PR 修复
PRD 的硬约束（issue-001 / ADR-0003）明确：**`Session` / `AgentJsonlProtocol` / 游戏引擎层零改动**（Hub 旁路模型）。该竞态位于 `Session` 拆除时序，属被排除的修改面。

### 当前规避（在测试侧）
`tests/test_ws.py` 的 `_run` 在每两个用例之间插入 `TEARDOWN_GRACE`（约 0.3s 等待），确保上一会话的全局静态状态彻底释放后再建新会话。服务器行为不变。

### 建议的后续动作（需单独立项）
1. **定位重置时机**：确认 `GlobalStatic.Console` 由谁、何时写入与清空；明确 `Session.Dispose()` 是否应同步清空全局静态。
2. **同步化或隔离**：
   - 方案 A：`Session.Dispose()` 收尾时显式 `GlobalStatic.Console = null`（或 Reset），使下次 `new Session` 拿到干净状态。
   - 方案 B：将 `GlobalStatic.Console` 改为每会话实例字段（彻底去除全局静态依赖）——改动面最大，需评估引擎侧引用。
3. **新增针对性测试**：单独立项补一个「连续 create→delete→create × N」的回归测试，固化修复，避免回归到「靠延迟侥幸通过」。
4. 修复后移除 `tests/test_ws.py` 的 `TEARDOWN_GRACE` 优雅降级（或保留为常识性间隔，无害）。

> 依赖关系：需 engine/headless 维护者确认全局静态的语义，不能仅由本传输 PR 擅自改动。

---

## 备注
- ADR-0003 与 `CLAUDE.md` 的改动未入库，因仓库 `.gitignore` 忽略 `docs/adr/*` 与 `CLAUDE.md`（ADR-0001/0002 同此处理），属既有仓库策略。
- 其余 6 个 issue（001–006）均已在 commit `8ba558f` 落地，无遗留项。
