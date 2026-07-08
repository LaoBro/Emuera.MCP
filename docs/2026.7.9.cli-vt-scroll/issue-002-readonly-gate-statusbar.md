# Issue 002: Scroll Mode 只读门卫 + 状态栏

## Parent

[PRD: CLI VT 备用屏鼠标滚轮滚动](./PRD.md)

## What to build

当 Scroll Offset > 0 时，CLI 进入 Scroll Mode——只读检查状态。键盘输入锁定
（offset>0 时丢弃所有非滚动键；滚动热键在 Issue 003 引入，故本切片暂锁定全部
按键）。按钮命中区清空（`ClearRegions`）并跳过 `SyncButtonState`。
`CountdownRenderer` 在 offset>0 时跳过 `Update`，offset 从 >0 转回 0 时调用
`Reset()`。光标在 offset>0 时隐藏（`ESC[?25l`），归零时恢复（`ESC[?25h`）。

新建 `ScrollStatusBarRenderer` 类：offset>0 时在视口最底行写入
`[scroll -N] End=resume PgUp/PgDn`（N 为当前 offset），offset=0 时清空该行。
鼠标左键点击（cb=0）在 offset>0 时忽略。

## Acceptance criteria

- [ ] offset>0 时，键盘输入字符在捕获的 PTY 输出中无回显（输入锁定）。
- [ ] offset>0 时，鼠标左键点击（cb=0）不分发 input（游戏不推进）。
- [ ] offset>0 时，按钮命中区已清空（点击不触发 `DispatchMouseClick`）。
- [ ] offset>0 时，`CountdownRenderer.Update` 为 no-op（不在历史行上覆盖倒计时）。
- [ ] offset 从 >0 转回 0 时，调用 `CountdownRenderer.Reset()`，下次 `Update`
      重新探测倒计时行位置。
- [ ] offset>0 时发射 `ESC[?25l` 隐藏光标；offset 归零时发射 `ESC[?25h` 恢复。
- [ ] offset>0 时，视口最底行显示 `[scroll -N] End=resume PgUp/PgDn`（N=当前 offset）。
- [ ] offset 归零时，状态栏行被清空。
- [ ] PTY 测试验证：滚轮上滚 → 状态栏文本出现、打字无回显、点击无效、光标隐藏；
      新输出归零 → 状态栏清空、光标恢复、倒计时重新探测。

## Blocked by

- [Issue 001: 滚轮滚动 tracer](./issue-001-wheel-scroll-tracer.md)
