# PRD: CLI VT 备用屏鼠标滚轮滚动

## Problem Statement

作为 CLI 玩家，我通过 `--protocol cli` 在真实终端（Windows Terminal / ConPTY / 现代 SSH）
中运行 ERB 游戏。当游戏输出的 `Display Line List` 超过终端视口高度时，超出部分滚出
屏幕顶部后**无法回看**——VT 备用屏（`ESC[?1049h`）下终端原生 scrollback 被禁用，
而当前 `TerminalRenderer.FullRefresh` 永远只渲染末尾 `WindowHeight-1` 行。

同时，鼠标滚轮事件（SGR mouse cb=64/65）在非 primitive 输入期被 `VtInputHandler`
直接丢弃（`if (!isPress || buttonCode != 0) return;`），滚轮在 CLI 普通输入期完全
无响应。玩家若想重读错过的指令说明或剧情，只能重启游戏或用 server 模式外部分析，
体验割裂。

## Solution

在 CLI VT Path 上引入**应用层视口偏移**（Scroll Offset）模型：由 `AgentCliVtScreen`
持有偏移状态，滚轮 / PgUp/PgDn / Home/End 改变偏移，`TerminalRenderer.FullRefresh`
按偏移渲染 `Display Line List` 的更早切片。偏移 > 0 时进入只读 Scroll Mode：底部
覆盖 `Scroll Status Bar` 提示当前偏移与恢复方式，按钮命中区禁用、键盘输入锁定、
倒计时隐藏；新输出 / ClearOp / 超时 / resize 触发 auto-follow 归零偏移，回到正常
跟随底部模式。

不启用 `?1007` alternate scroll——滚轮始终走 SGR mouse cb=64/65 显式路径，避免与
`ButtonSelectionMode` 的方向键导航冲突。

## User Stories

1. 作为 CLI 玩家，我想用鼠标滚轮向上滚动回看滚出屏幕顶部的历史行，这样我能重读
   错过的指令说明或剧情。
2. 作为 CLI 玩家，我想用鼠标滚轮向下滚动回到当前输入提示，这样我能恢复输入。
3. 作为 CLI 玩家，我想在 Scroll Mode 下看到明显的状态指示，这样我知道输入已锁定
   以及如何恢复（按 End）。
4. 作为 CLI 玩家，我想用 PgUp/PgDn 整屏翻页，这样我能比逐行更快地导航长历史。
5. 作为 CLI 玩家，我想用 Home 跳到 Scrollback 最顶端，这样我能看到最早可用的历史。
6. 作为 CLI 玩家，我想用 End 跳回底部，这样我能快速恢复输入。
7. 作为 CLI 玩家，我想在游戏产生新输出时自动回到底部，这样我不会错过新内容。
8. 作为 CLI 玩家，我想在 Scroll Mode 下按钮不可点击，这样我不会误触旧 Generation
   按钮导致 input rejection。
9. 作为 CLI 玩家，我想在 Scroll Mode 下键盘输入被锁定，这样杂乱按键不会污染输入
   缓冲区且无视觉反馈。
10. 作为 CLI 玩家，我想在 TINPUT 超时触发时自动回到底部，这样我能看到超时结果。
11. 作为 CLI 玩家，我想滚动范围被 `Display Line List` 实际长度限定，这样我不能滚
    到不存在的空行。
12. 作为 CLI 玩家，我想终端 resize 时滚动位置在边界内保持，这样我的阅读位置不丢失。
13. 作为 CLI 玩家，我想 Scroll Mode 下光标隐藏，这样不会有"输入会落在这里"的视觉
    误导。
14. 作为 CLI 玩家，我想状态栏显示当前 Scroll Offset 数值，这样我知道滚了多远。
15. 作为 CLI 玩家，我想滚轮每次滚动 3 行，这样兼顾效率与精准度。
16. 作为 CLI 玩家，我想 ERB CLEAR（ClearOp）触发时滚动归零，这样清屏后不会停留在
    过期历史视图。
17. 作为 CLI 玩家，我想强制全量刷新（ConsumeNeedFullRefresh）时滚动归零，这样状态
    变更后不会看到陈旧视图。
18. 作为开发者，我想 Scroll Offset 状态由 `AgentCliVtScreen` 持有，这样视口状态与
    VT 序列发射同源（ADR-0005 预决策落地）。
19. 作为开发者，我想用单一 PTY 集成测试 seam 验证滚动行为，这样不引入新 C# 测试
    工程且覆盖端到端 VT 输入到渲染输出。
20. 作为开发者，我想滚动实现不启用 `?1007`，这样滚轮事件保持独立路径，不与按钮
    方向键导航冲突。
21. 作为开发者，我想 `ScrollStatusBarRenderer` 作为独立类，这样状态栏渲染生命周期
    与 `TerminalRenderer.FullRefresh` 解耦。
22. 作为开发者，我想 `CountdownRenderer` 在 Scroll Mode 下跳过 Update，这样它不会
    覆盖历史行。
23. 作为开发者，我想 Server 模式不受此功能影响，这样滚动特性纯粹 CLI 作用域。
24. 作为 CLI 玩家，我想 Scroll Mode 下鼠标点击被忽略，这样点击既不产生困惑的无操作
    也不触发错误 Generation 的分发。
25. 作为 CLI 玩家，我想滚轮步进在所有终端上一致（3 行），这样行为可预测。
26. 作为 CLI 玩家，我想 PgUp/PgDn 精确滚动一个视口高度，这样翻页量可预测。
27. 作为 CLI 玩家，我想在底部时按 End 是 no-op，这样不产生视觉抖动。
28. 作为 CLI 玩家，我想在底部时滚轮下滚是 no-op，这样不进入负偏移。
29. 作为 CLI 玩家，想仅当历史超出视口时才允许进入 Scroll Mode，这样内容填满一屏时
    滚轮上滚不做任何事。
30. 作为开发者，我想 ADR-0006 的被拒方案被记录，这样未来重新提议 `?1007` 或"允许
    滚动期输入"时能直接引用决策依据。

## Implementation Decisions

### Scroll Offset 状态

- 由 `AgentCliVtScreen` 持有 `ScrollOffset`（int，默认 0）。
- 0 = 正常模式（跟随底部）；>0 = Scroll Mode（回看历史）。
- Clamp 到 `[0, max(0, lines.Count - visibleLines)]`。
- offset=0 时 `visibleLines = consoleHeight - 1`（无状态栏）。
- offset>0 时 `visibleLines = consoleHeight - 2`（底部 1 行留给 Scroll Status Bar）。
- max offset 依赖 visibleLines，而 visibleLines 依赖 offset——计算时按"目标 offset
  是否 >0"决定 visibleLines，再 clamp。

### 滚轮事件解析

- `VtInputHandler.OnMouseEvent` 识别 SGR mouse cb=64（滚轮上）/cb=65（滚轮下）。
- cb=64 → delta=+3；cb=65 → delta=-3。
- 调用 `_host.DispatchWheel(delta)`，由 `AgentCliProtocol` 更新 `AgentCliVtScreen`
  并触发 FullRefresh + 状态栏渲染。
- 不启用 `?1007` alternate scroll mode。

### 键盘热键

- `VtParser` 新增 `CSI ~` terminator 解析：param 5 → PgUp，param 6 → PgDn。
- Home（`CSI H`）/ End（`CSI F`）已有映射，复用。
- PgUp → offset += visibleLines（整屏）。
- PgDn → offset -= visibleLines（整屏）。
- Home → offset = max（滚到顶）。
- End → offset = 0（回底，退出 Scroll Mode）。

### Scroll Mode 只读门卫

- offset>0 时 `VtInputHandler.OnKeyEvent` 仅放行 PgUp/PgDn/Home/End，其余键盘事件
  **全部丢弃**（不进入 `ProcessKey` / `ButtonSelectionMode.HandleKey`）。
- offset>0 时 `OnMouseEvent` cb=0（左键点击）**忽略**（不调用 `DispatchMouseClick`
  / `DispatchMouseMiss`）。
- offset>0 时 `ButtonSelectionMode.RefreshButtonRegions` 走 `ClearRegions` 分支，
  不 `SyncButtonState`。
- offset>0 时 `CountdownRenderer.Update` 跳过（不渲染倒计时）。
- offset>0 时发射 `ESC[?25l` 隐藏光标；offset 归零时 `ESC[?25h` 恢复。

### Auto-follow 触发点（offset → 0）

- `TerminalRenderer.FlushBuffer` 检测到新行（`currentLineNo > lastRenderedLineNo`）。
- `ClearOp`（drain pendingOps 时）。
- `ConsumeNeedFullRefresh`（主循环顶部）。
- TINPUT 超时（`SubmitTimeout` 产生新输出，走 FlushBuffer 新行路径）。
- Resize（`CheckResize` 后 clamp 到新 max，若新 max=0 则归零）。

### Scroll Status Bar

- 新建 `ScrollStatusBarRenderer` 类，独立于 `TerminalRenderer`。
- offset>0 时在 `consoleHeight-1` 行写入：
  `[scroll -N] End=resume PgUp/PgDn`（N 为当前 offset）。
- offset=0 时清空该行（`ESC[{row};1H\x1b[2K`）。
- 由 `AgentCliProtocol` 在 offset 变化时显式调用 Render。

### CountdownRenderer 交互

- offset 从 >0 转回 0 时，`AgentCliProtocol` 调用 `CountdownRenderer.Reset()`，
  让下次 `Update` 重新探测倒计时行位置（因 `CursorTop - 1` 假设在 Scroll Mode
  期间失效）。

### TerminalRenderer 改造

- `FullRefresh` 读 `_screen.ScrollOffset`：
  - offset=0：现行行为（`visibleLines = consoleHeight - 1`，`startLine = max(0,
    lines.Count - visibleLines)`）。
  - offset>0：`visibleLines = consoleHeight - 2`，`startLine = max(0, lines.Count
    - visibleLines - offset)`。
- `FlushBuffer` 检测到新行时，若 `_screen.ScrollOffset > 0`，先归零 offset 再
  FullRefresh（增量分支假设光标在底部，offset>0 时光标在状态栏，必须走 FullRefresh）。

### 事件分发路径

- 滚轮：`VtInputHandler.OnMouseEvent` cb=64/65 → `_host.DispatchWheel(delta)` →
  `AgentCliProtocol` 更新 `_screen.ScrollOffset` → FullRefresh +
  `ScrollStatusBarRenderer.Render` + （若归零）`_countdown.Reset` +
  `_buttons.RefreshButtonRegions(force)`。
- 热键：`VtInputHandler.OnKeyEvent` offset>0 时仅放行 4 键 →
  `_host.DispatchScroll(action)` → 同上路径。

### Scrollback 深度

- 受 `Config.MaxLog` 限制（`displayLineList` 头部超出 MaxLog 的行已丢失）。
- 滚动只能在当前 `Display Line List` 范围内。
- 不引入额外 scrollback 缓冲（ADR-0005 已确认保持现状）。

### Server 模式

- 不受影响——Server 模式不走 `AgentCliProtocol`，server 客户端自行实现滚动 UI。

## Testing Decisions

### 测试原则

- 只测外部行为（捕获的 PTY 输出文本），不窥探内部状态。
- 单一 seam：扩展 `tests/test_cli_basic.py` 的 `PtyProcess.spawn` 模式。
- 不新建 C# 单元测试工程——滚动是端到端 VT 输入→渲染输出行为，PTY 全链路验证
  价值最高且与现有测试体系一致。

### 测试模块

- 新建 `tests/test_cli_scroll.py`，复用 `emuera_server.copy_test_game_with_erb`
  与 `find_binary` 辅助。
- 复用 `test_cli_basic.py` 的 `_capture_cli_with_erb` / `_run_cli_and_capture`
  PTY spawn + reader 线程模式。

### Prior Art

- `tests/test_cli_basic.py`：`PtyProcess.spawn` + reader 线程 + ANSI 文本断言
  （`test_cli_clear`、`test_cli_merge`、`test_cli_setbg`）。
- `tests/test_vt_only.py`：`subprocess.Popen` stdin=PIPE 验证 fatal 退出路径。
- ConPTY 24-bit color SGR 输出可能被消费的 warn 机制（`test_cli_setbg`）。

### 测试用例覆盖

- 滚轮上滚：注入 `ESC[<64;1;1 M`，断言 `Scroll Status Bar` 文本 `[scroll -` 出现。
- 历史回看：ERB 产出多个标记行（如 `Line1`..`Line20`），滚轮上滚后断言较早的
  `Line1` 出现在捕获输出中。
- Auto-follow：滚轮上滚进入 Scroll Mode 后，发送输入推进游戏产生新输出，断言
  状态栏消失 / 新内容可见。
- 输入锁定：Scroll Mode 下发送普通字符，断言无回显（捕获输出无该字符）。
- 按钮点击禁用：Scroll Mode 下注入 `ESC[<0;1;1 M`（左键点击），断言无 input
  分发（游戏不推进）。
- PgUp/PgDn 翻页：注入 `ESC[5~` / `ESC[6~`，断言 offset 变化（状态栏数值变化
  或历史切片变化）。
- Home/End 跳转：注入 `ESC[H` 断言滚到顶（最早标记可见），`ESC[F` 断言回底
  （状态栏消失）。
- End at bottom no-op：offset=0 时注入 `ESC[F`，断言无视觉变化（无状态栏闪现）。
- ClearOp 归零：ERB 中 `CLEAR` 后断言滚动归零（状态栏不存在）。
- 滚轮下滚到底 no-op：offset=0 时注入 `ESC[<65;1;1 M`，断言无负偏移视觉抖动。
- 内容不足视口时不进入 Scroll Mode：ERB 仅产出 1-2 行，滚轮上滚断言无状态栏。

## Out of Scope

- **Server 模式滚动 UI**：server 客户端（HTTP/WebSocket 前端）自行实现滚动，本 PRD
  仅作用于 CLI `AgentCliProtocol`。
- **超出 `Config.MaxLog` 的 scrollback**：`displayLineList` 头部超出 MaxLog 的行已
  永久丢失，不引入额外 scrollback 缓冲。
- **水平滚动**：不考虑宽行 wrap-aware 横向视口。
- **触摸板手势**：仅支持滚轮 + 键盘，不识别触控板 pinch/swipe。
- **`?1007` alternate scroll mode**：明确拒绝（见 ADR-0006 Considered Options a）。
- **C# 单元测试工程**：单一 PTY seam，不新增测试工程。
- **可配置滚轮步进**：固定 3 行，不暴露为用户可调配置。
- **Scrollback 内搜索**：不提供 `/` 搜索功能。
- **跨 turn 保持滚动位置**：auto-follow 在每次新输出时归零，不"粘住"历史位置。
- **滚动位置持久化**：进程退出后不保存滚动状态。

## Further Notes

- **ADR-0006**（`docs/adr/0006-cli-vt-scroll-viewport.md`）是本 PRD 的权威设计记录，
  包含 7 条决策与 6 个被拒方案的理由。
- **CONTEXT.md** 已更新 3 个术语：Scroll Offset / Scroll Mode / Scroll Status Bar，
  定义于 CLI Terminal Path 章节下。
- **ADR-0005** 预决策（"视口滚动状态由 `AgentCliVtScreen` 持有"）在本 PRD 中具体化
  为 `ScrollOffset` 字段 + Clamp + `ScrollBy`/`ScrollTo`/`ResetScroll` 接口。
- **ConPTY 限制**：24-bit color SGR 输出可能被 ConPTY 消费（见
  `test_cli_basic.py` `test_cli_setbg` 的 warn 机制）。SGR mouse **输入**通过 PTY
  写入是字节级透传，不受此限制；状态栏文本是纯 ASCII，可被 PTY 捕获断言。
- **`?1007` 不启用的副作用**：终端自身的滚轮在 alt screen 下无响应（终端原生行为），
  应用层 cb=64/65 路径是唯一的滚轮回看通道。这是预期行为，非缺陷。
