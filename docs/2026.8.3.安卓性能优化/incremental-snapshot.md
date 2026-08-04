# 3.3 增量快照实施计划（S0 修复：DisplayState 引用缓存）

> 日期：2026.8.3
> 关联：`current_plan/2026.8.3.android-perf.md` 的 3.3 条目与嫌疑清单 S0。
> 状态：**已调研未实施**。本文档仅计划；批准后再动手。

## 目标

把 `DisplayState.ComputeDiff` 每回合的 O(总行数) 全量重建 + 深比较，降为 O(变更行数)。**协议零改动**（前端 `LineOp` 流与 diff 语义不变），CLI/Web/MAUI 三端同收益。

## 原理（已验证）

引擎行对象 `ConsoleDisplayLine` 满足引用缓存条件：

- **身份稳定**：行创建时设 `LineNo`（`ConsolePrintManager.cs:135`），进入 `displayLineList` 后**无原地内容修改路径**：
  - `IsLineEnd` 仅 flush 时设一次（`PrintStringBuffer.cs:172`）
  - `SetAlignment` 有 `aligned` 一次性守卫（`ConsoleDisplayLine.cs:59-63`）
  - `ChangeStr` 仅末行合并路径且在 `Add` 前（`ConsolePrintManager.cs:138-144`）
  - `IsTemporary`/`IsLogicalLine` 为 readonly
- **变更信号**：新行/末行编辑必触发 `PrintOp`；`CLEAR/CLEARLINE/ShiftHead` 有权威 op（`ConsolePrintManager.cs:147-222`）
- **构建函数纯**：`BuildPrintOpsForLine` 只依赖行对象 + 字体名（`ConsolePrintManager.cs:593-646`），同输入必同输出
- **引用相等 = 内容相等已被采纳为约定**：`TerminalRenderer` 用 `SourceLine` 引用比对检测行替换（`TerminalRenderer.cs:17-18`）

**核心机制**：`DisplayState` 维护 `Dictionary<ConsoleDisplayLine, DisplayLine>`（引用键）。每回合遍历 `displayLineList`（O(n) 字典查找，~ns 级），命中复用已构建的 `DisplayLine`（含 segments/几何/LineNo/SourceLine/AlignOffset），未命中才跑 `BuildPrintOpsForLine`。`CommonPrefix` 加 `ReferenceEquals` 短路跳过深比较。

**等价性保证**：缓存产物与全量重建**值相等** → diff 输出与现状逐字节一致（JSON 序列化只读不修改缓存对象）。CLI 侧 `SourceLine`/`LineNo`/`AlignOffset` 引用相同，不受影响。

## 步骤 0：前置审计（不可变性验证）

- [ ] 确认 `HtmlManager.cs:646-648` 的 `SetAlignment` 调用**不发生在行入列之后**（`aligned` 守卫外的唯一风险点）
- [ ] 确认 `ConsoleButtonString` 入列后无字段修改（`Generation`/`Inputs`/`IsButton`/`StrArray` 写点全查）
- [ ] 若审计失败 → 降级方案：缓存键改为 `(LineNo, 内容指纹)`，失效逻辑按位置比较（复杂度显著上升，尽量避免）

## 实施步骤（✅ 全部完成）

1. **缓存结构**（`DisplayState.cs`）：`Dictionary<ConsoleDisplayLine, DisplayLine> _lineCache`
   - 引用相等键（`ConsoleDisplayLine` 未重写 `Equals`，默认即引用相等）
   - 与 `_gate` 同生命周期，重建/清空均持锁
2. **`BuildSnapshotIncremental` 增量构建**：
   - `BuildSnapshot` 提取为转发（空缓存）→ 等价于全量重建，既有测试语义不变
   - 命中缓存 → 复用 DisplayLine（含 entries/几何/LineNo/SourceLine/AlignOffset）
   - 未命中 → `BuildDisplayLine`（提取自原 foreach 体）构建 + 入缓存
3. **失效策略**（简化实现——兜底方案已够）：
   - 行对象替换天然失效：CLEAR/CLEARLINE/ShiftHead/末行编辑均以新行对象替换旧对象 → 旧条目成孤儿，不会被访问（引用键保证不误命中）
   - 兜底：`Rebuild` 中 `_lineCache.Count > linesCopy.Count + maxLog`（maxLog 取 `Config.MaxLog`，测试/异常环境 5000）→ 全清，下一回合全量重建一次（O(n)）后恢复命中，每 MaxLog 行才触发一次
4. **`LinesEqual` 引用短路**：入口 `ReferenceEquals(a, b) → true`（同引用必然同值）
5. **保持不动**：`_gate` 锁、`linesCopy` 竞态重试拷贝、`DrainAndClassifyClears` 权威信号、`GetSnapshotJson`、`TurnClearSignal` 逻辑——全部未改

## 测试与回归（✅ 已执行）

- [x] `DisplayStateTests` 现有用例全绿（值相等性 → 缓存与全量重建 diff 必须一致）
- [x] 新增单测 7 个：缓存命中复用（segments/CLI 字段引用相同）、新行 miss 构建、CLEARLINE 尾部删除、ShiftHead 头部删除、末行编辑引用替换重建、增量与全量产物逐字段值相等、CommonPrefix 引用短路（反射直测）
- [x] `dotnet test Emuera.Headless.Tests` → **406 用例全绿**（DisplayStateTests 35 个）
- [x] `tests/run_all.py` → **11/13 PASS**。2 个 FAIL（I-11 exit survival、WebSocket transport）**均为基线复现的既有问题**（stash 回基线重建后同样失败），与本次改动无关——失败场景均为 Reset（load-game 二次会话），且失败时测试残留僵尸服务器进程锁 DLL
- [x] `Emuera.Web` `npm test` → **18 文件 383 用例全绿**（前端协议零改动确认）

## 性能验证（✅ 已执行）

长会话压测：临时游戏副本，ERB 灌 5200 行（MaxLog=5000 触发滚动），server 模式 GET /turn / GET /snapshot 墙钟计时，baseline（stash 重建）vs current 各 3 轮：

| 场景 | baseline | current | 提升 |
|------|----------|---------|------|
| fill turn（5200 行首回合）| 0.317s | 0.114s | **2.8x** |
| steady turn AfterInput1 | 0.025s | 0.020s | 1.25x |
| steady turn AfterInput2 | 0.019s | 0.012s | 1.6x |
| GET /snapshot（5000 行）| 0.027s | 0.020s | 1.35x |

全场景无变慢。稳态回合提升 25-60%——实际帧构建差值约 5ms（被 HTTP 轮询/脚本执行的固定成本掩盖），符合预期：缓存消除了 O(5000 行) 重建+深比较大头，但 TurnRecord 固定成本仍在。

## 风险与降级

| 风险 | 处置 |
|------|------|
| 步骤 0 审计发现行对象可变 | ✅ 已审计通过：`LineNo`/`IsLineEnd`/`Align`/`buttons` 写点均在 Add 之前（`ConsolePrintManager.cs:131-145` 入列路径），`SetAlignment` 有 `aligned` 一次性守卫，`ChangeStr`/`ShiftPositionX` 仅末行合并（Add 前）——无需降级方案 |
| 缓存内存增长（孤儿行） | ✅ 兜底阈值 `Count > 活跃行 + MaxLog` 全清；孤儿仅来自 CLEARLINE/ShiftHead/末行编辑，量级受 MaxLog 约束 |
| 竞态（游戏线程写列表 vs 缓存操作） | ✅ 全部操作在 `_gate` 内；`linesCopy` 重试机制不变 |
| CLI 渲染依赖引用（SourceLine/AlignOffset） | ✅ 命中缓存复用同一引用，行为与现状一致；值相等性单测逐字段验证 |
| 测试基础设施（I-11/WS 失败） | ⚠️ 既有问题（基线复现），与本改动无关，单独跟踪 |
