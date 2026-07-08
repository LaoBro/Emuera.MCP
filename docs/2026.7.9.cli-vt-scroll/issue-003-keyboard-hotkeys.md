# Issue 003: 键盘滚动热键（PgUp/PgDn/Home/End）

## Parent

[PRD: CLI VT 备用屏鼠标滚轮滚动](./PRD.md)

## What to build

为 Scroll Mode 补充键盘导航。`VtParser` 新增 `CSI ~` terminator 解析
（param 5 = PgUp，param 6 = PgDn）。Issue 002 引入的 `OnKeyEvent` offset>0
门卫被 amend：放行 PgUp/PgDn/Home/End 并路由到新的 `DispatchScroll(action)`
路径，其余按键仍锁定。

- Home → offset = max（滚到顶）
- End → offset = 0（回底，退出 Scroll Mode）
- PgUp → offset += visibleLines（整屏）
- PgDn → offset -= visibleLines（整屏）

`DispatchScroll` 复用 Issue 001 的 offset 更新 + FullRefresh + 状态栏渲染路径。

## Acceptance criteria

- [ ] `VtParser` 解析 `ESC[5~` 为 PgUp、`ESC[6~` 为 PgDn（路由到滚动路径）。
- [ ] PgUp 使 offset +visibleLines（钳到 max）；PgDn 使 offset -visibleLines（钳到 0）。
- [ ] Home 使 offset = max（滚到顶，最早标记行可见）；End 使 offset = 0（回底）。
- [ ] offset=0 时按 End 为 no-op（无状态栏闪现、无重绘抖动）。
- [ ] offset>0 时仅 PgUp/PgDn/Home/End 通过门卫，其余按键仍被丢弃。
- [ ] PTY 测试验证：PgUp/PgDn 整屏翻页；Home 显示最早标记；End 回底；
      End 在底部时 no-op。

## Blocked by

- [Issue 002: Scroll Mode 只读门卫 + 状态栏](./issue-002-readonly-gate-statusbar.md)
