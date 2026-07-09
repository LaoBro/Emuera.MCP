# 测试方案：CLI 鼠标功能测试覆盖

- **日期**: 2026-07-09
- **关联**: [experiment-results.md](./experiment-results.md)（实验证明 PTY 拦截 SGR mouse）、ADR-0006、ADR-0007
- **状态**: 推荐方案，待实施

## 背景

[experiment-results.md](./experiment-results.md) 实验确认：pywinpty/ConPTY 在输入侧拦截 SGR mouse 序列，无法通过 PTY 注入测试鼠标功能。ADR-0006 PRD 计划的 11 个 PTY 鼠标测试用例（L190-207）不可行。

需要替代方案覆盖 CLI 鼠标功能的应用逻辑。

## 当前测试覆盖

### 已覆盖（键盘热键，[tests/test_cli_scroll.py](../../tests/test_cli_scroll.py)）

- PgUp/PgDn/Home/End 进入/退出 Scroll Mode
- Scroll Mode 状态栏渲染
- Scroll Mode 输入锁（键盘输入不回显）
- Scroll Mode 光标隐藏
- offset=0 时 End no-op
- ConsumeNeedFullRefresh 归零 offset
- auto-follow via FullRefresh
- auto-follow on new output（TINPUT 超时触发）
- TINPUT 超时触发 + 倒计时递减

### 未覆盖（鼠标功能）

- **滚轮事件分发**：`ESC[<64;...M`（上）/`ESC[<65;...M`（下）→ `DispatchWheel` → `ApplyScrollChange`
- **按钮点击命中**：`ESC[<0;Cx;Cy M` → `HitTest(row, col)` 命中 → `DispatchMouseClick`
- **点击未命中**：`ESC[<0;Cx;Cy M` → `HitTest` 未命中 → `DispatchMouseMiss`
- **Scroll Mode 鼠标门卫**：offset>0 时非滚轮鼠标事件被忽略
- **按钮 Generation 过期**：`RefreshButtonRegions` 更新 Generation，旧 Generation 的 HitTest 不命中
- **primitive 模式鼠标**：`IsWaitingPrimitive` 下 `DispatchPrimitiveMouseKey` → `InputMouseKey`

### PTY 集成测试不可行的部分

- 通过 pywinpty 注入 `ESC[<...M/m` 字节序列（ConPTY 拦截，见实验结果）

## 推荐方案：C+D 组合

### C: .NET 单元测试（核心价值）

新建 .NET 测试项目，直接调应用层方法，绕过 PTY 字节路径。

**项目位置**: `tests/Emuera.Headless.Tests/`（或 `Emuera.Headless.Tests/`，与主项目同级）

**测试框架**: xUnit + FluentAssertions（或 NUnit，按仓库现有约定）

**测试目标与用例**:

#### C1: VtParser SGR mouse 解析

直接 `VtParser.Feed` 字节，断言 `OnMouseEvent` 回调收到正确的 `(row, col, cb, isPress)`。

- `Feed(ESC[<64;1;1M)` → `OnMouseEvent(0, 0, 64, isPress=true)`
- `Feed(ESC[<0;5;3M)` → `OnMouseEvent(2, 4, 0, isPress=true)`
- `Feed(ESC[<0;5;3m)` → `OnMouseEvent(2, 4, 0, isPress=false)`（释放）
- `Feed(ESC[<65;10;20M)` → `OnMouseEvent(19, 9, 65, isPress=true)`（滚轮下）
- 坐标 1-based → 0-based 转换验证
- 不查 `?1000h` 标志验证（无条件解析 `ESC[<`）

#### C2: HitTest 按钮命中

构造 `ButtonRegionTracker`，调 `RecordLineRegions` 记录已知按钮位置，调 `HitTest(row, col)` 断言命中/未命中。

- 按钮在 row=49, col=[0,4]：`HitTest(49, 0)` 命中，`HitTest(49, 4)` 命中，`HitTest(49, 5)` 未命中
- 不同 row 的按钮：`HitTest(48, 0)` 未命中
- 倒序匹配（后记录的优先）

#### C3: Generation 过期

- 调 `RefreshButtonRegions` Generation A，记录按钮
- 调 `RefreshButtonRegions` Generation B（按钮位置变了）
- `HitTest` 用旧 Generation 的坐标 → 未命中（或命中新位置）

#### C4: Scroll Mode 鼠标门卫

构造 `VtInputHandler`，设 `offset > 0`（Scroll Mode），`Feed(ESC[<0;1;1M)` → 不触发 `DispatchMouseClick`，只 `DispatchWheel` 能穿透。

#### C5: DispatchMouseClick 分发

Mock `AgentCliProtocol` 或用最小 fixture，设 `console.State == WaitInput`，`Feed(ESC[<0;Cx;CyM)` 命中按钮 → 断言 `SubmitInput` 被调用，参数是按钮的 `ConsoleButtonString`。

#### C6: DispatchMouseMiss 分发

`Feed(ESC[<0;Cx;CyM)` 未命中按钮 → 断言 `DispatchMouseMiss` 被调用（推进 AnyKey/EnterKey）。

#### C7: DispatchWheel → ApplyScrollChange

`Feed(ESC[<64;1;1M)` → 断言 `offset += 3`，`Feed(ESC[<65;1;1M)` → 断言 `offset -= 3`（不小于 0）。

#### C8: primitive 模式鼠标

`IsWaitingPrimitive = true`，`Feed(ESC[<0;1;1M)` → 断言 `DispatchPrimitiveMouseKey` → `InputMouseKey(1, windowsButton=1, ...)`。

**优势**:
- 稳定、可重复、CI 友好
- 不受 PTY 拦截影响
- 直接测试逻辑，失败定位精确
- 覆盖应用逻辑（HitTest、Generation、门卫、分发）

**成本**: 新建测试项目 + 8 类测试用例。~半天。

### D: 更新 PTY 测试注释（低成本收尾）

更新 [test_cli_scroll.py:11-12](../../tests/test_cli_scroll.py#L11-L12) 注释，把"ConPTY 拦截"说法改为基于实验的准确描述，避免未来重复调查。

**新注释**:
```python
# SGR 鼠标事件 (cb=64/65 滚轮, cb=0 点击) 无法通过 pywinpty PTY 注入测试。
# 实验验证（docs/2026.7.9.cli-mouse-testing/experiment-results.md）：
#   - pywinpty ConPTY + winpty 两个 backend 都拦截 SGR mouse 输入字节
#   - 最小 EchoMouse 实验（设 ENABLE_VIRTUAL_TERMINAL_INPUT + raw ReadFile）确认
#     键盘 CSI 序列到达但 SGR mouse 序列不到达——拦截发生在 PTY host 层
#   - 应用端 VtParser 无 ?1000h 检查，若字节到达必然解析
# 鼠标功能覆盖改为 .NET 单元测试（docs/2026.7.9.cli-mouse-testing/testing-plan.md C 部分）。
# PTY 集成测试仅覆盖键盘热键（DispatchWheel/DispatchScroll 共享 ApplyScrollChange 逻辑）。
```

**成本**: 改注释。5 分钟。

## 不采用的方案及理由

### B（已验证失败）: 用 .NET 直接写 ConPTY host + WriteFile

实验 B1/B2 已证明：即使绕过 pywinpty 用 SteamCD.ConPTY 或最小 P/Invoke host，ConPTY 仍拦截 SGR mouse。此路径不可行。

### WriteConsoleInput 注入 Win32 MOUSE_EVENT_RECORD

- Emuera 用 `ENABLE_VIRTUAL_TERMINAL_INPUT` 模式，不接受 Win32 事件
- 这条路径在生产中不会发生，测试它没有意义
- ConPTY host 会把 Win32 事件转 VT，但 SGR mouse 序列仍被拦截（与 B 同样问题）

### pexpect / wsltty

- 主要面向 Unix PTY，Windows 下支持有限
- 不解决 ConPTY 拦截问题

## 实施顺序

1. **D（先做）**: 更新 test_cli_scroll.py 注释，记录实验结论。5 分钟。
2. **C（后做）**: 新建 .NET 测试项目，按 C1-C8 顺序实现。半天。
3. **验收**: C 完成后，PRD L190-207 计划的 11 个用例的"应用逻辑部分"由 C 覆盖；PTY 字节路径部分标注"不可测，靠真实终端手动验证（spike-results.md 已做过）"。

## 验收标准

- [ ] test_cli_scroll.py 注释更新为基于实验的准确描述（D）
- [ ] .NET 测试项目创建，C1-C8 全部实现并通过（C）
- [ ] C1-C8 覆盖：VtParser 解析、HitTest、Generation、Scroll Mode 门卫、DispatchMouseClick/Miss/Wheel、primitive 模式
- [ ] CI 集成（若仓库有 CI）
