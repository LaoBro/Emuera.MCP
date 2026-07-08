# Issue 004: Edge-case hardening

## Parent

[PRD: CLI VT 备用屏鼠标滚轮滚动](./PRD.md)

## What to build

加固 Scroll Offset 生命周期以应对状态变更事件：

- `ClearOp`（`FlushBuffer` drain pendingOps 时）归零 offset。
- `ConsumeNeedFullRefresh`（主循环顶部）返回 true 时归零 offset。
- `CheckResize` 后将 offset clamp 到新 max（终端缩小可能需要减小 offset；
  若新 max=0 则 offset=0）。
- 补测试验证 emergent no-op 场景：wheel-down-at-bottom 与 content-fits-viewport
  （两者均由 clamp 逻辑自然保证，但需显式测试覆盖）。

本切片独立于 Issue 002/003，可与之并行——触及的是 `FlushBuffer` 的 ClearOp 分支、
主循环的 `ConsumeNeedFullRefresh` 分支、`CheckResize` 路径，与门卫/热键/状态栏
无重叠。

## Acceptance criteria

- [ ] ERB CLEAR（`ClearOp` 在 `FlushBuffer` drain 时）归零 Scroll Offset 后再渲染。
- [ ] `ConsumeNeedFullRefresh` 返回 true 时归零 Scroll Offset 后再 FullRefresh。
- [ ] 终端 resize 后，Scroll Offset 钳到新 `max(0, lines.Count - visibleLines)`；
      若新 max=0 则 offset=0。
- [ ] offset=0 时滚轮下为验证过的 no-op（测试确认无状态栏出现、无重绘）。
- [ ] `Display Line List` 长度 ≤ 视口高度时，滚轮上为验证过的 no-op
      （测试确认无状态栏出现）。
- [ ] PTY 测试验证：CLEAR 归零滚动；resize 后偏移钳到合法范围；no-op 边界场景。

## Blocked by

- [Issue 001: 滚轮滚动 tracer](./issue-001-wheel-scroll-tracer.md)

可与 Issue 002、Issue 003 并行。
