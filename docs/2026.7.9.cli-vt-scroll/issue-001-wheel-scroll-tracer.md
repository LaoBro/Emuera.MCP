# Issue 001: 滚轮滚动 tracer（核心端到端骨架）

## Parent

[PRD: CLI VT 备用屏鼠标滚轮滚动](./PRD.md)

## What to build

最薄的端到端滚动 tracer bullet。鼠标滚轮上/下（SGR mouse cb=64/65）改变
`AgentCliVtScreen` 持有的 Scroll Offset；`TerminalRenderer.FullRefresh` 按 offset
渲染 `Display Line List` 的更早切片；`FlushBuffer` 检测到新游戏输出时 auto-follow
归零 offset。

本切片**不含**输入锁定、状态栏、键盘热键、edge-case hardening——那些由后续切片
补齐。本切片的目标是验证 offset → re-render → auto-follow 端到端路径打通。

滚轮步进固定 3 行。offset clamp 到 `[0, max(0, lines.Count - visibleLines)]`，
其中 offset=0 时 `visibleLines = consoleHeight - 1`。

## Acceptance criteria

- [ ] 鼠标滚轮上（cb=64）使 Scroll Offset +3，钳到 max；`TerminalRenderer`
      重新渲染更早切片，原先在视口上方的标记行变得可见。
- [ ] 鼠标滚轮下（cb=65）使 Scroll Offset -3，钳到 0；offset 归零后重新渲染
      底部切片。
- [ ] offset>0 时游戏产生新输出（`FlushBuffer` 检测 `currentLineNo > lastRenderedLineNo`），
      Scroll Offset auto-follow 归零并渲染新底部切片。
- [ ] offset=0 时滚轮下（cb=65）为 no-op（不进入负偏移，无视觉抖动）。
- [ ] `Display Line List` 长度 ≤ 视口高度时，滚轮上无法进入 Scroll Mode
      （max offset = 0）。
- [ ] PTY 集成测试（`tests/test_cli_scroll.py`）验证：滚轮上滚看到较早标记行；
      滚轮下滚回底；新输出 auto-follow 归零。

## Blocked by

None - can start immediately.
