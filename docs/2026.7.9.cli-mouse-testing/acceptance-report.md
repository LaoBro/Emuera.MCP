# 验收报告：CLI 鼠标功能测试覆盖（testing-plan.md）

- **验收日期**：2026-07-11
- **被验收文档**：`docs/2026.7.9.cli-mouse-testing/testing-plan.md`
- **结论**：✅ 通过（C1–C8 全绿；D 注释已修正；CI N/A）

## 验收清单

| 项 | 标准 | 证据 | 结果 |
|----|------|------|------|
| **C — .NET 测试项目** | 新建项目，C1–C8 全部实现并通过 | `Emuera.Headless.Tests/` 已建（xUnit, net10.0）；`dotnet test` → **84 passed / 0 failed** | ✅ |
| **C1 VtParser SGR 解析** | `Feed(ESC[<...)` → 正确 `(row,col,cb,isPress)`；1-based→0-based；无条件解析 | `VtParserTests.cs`：press/release/wheel-up cb64/wheel-down cb65/无效参数；坐标 (4,9) from `1;10;5` | ✅ |
| **C2 HitTest 命中** | 区域内命中、边界命中、区域外未命中、错行未命中、后记录优先 | `ButtonRegionTrackerTests.cs`：C2_* 9 例（含 right-edge / leading-width / overlap-last-wins） | ✅ |
| **C3 Generation 过期** | 旧 Generation 坐标不命中；Generation 变更清旧区域 | `ButtonRegionTrackerTests.cs`：C3_* 4 例（matching / mismatched / change-clear / non-button） | ✅ |
| **C4 Scroll Mode 门卫** | offset>0 时非滚轮鼠标事件被忽略，热键/滚轮仍穿透 | `VtInputHandlerTests.cs`：C4_* 4 例（key-dropped / hotkeys-route / normal-route / mouse-click-ignored） | ✅ |
| **C5 DispatchMouseClick** | 命中按钮 → `DispatchMouseClick(btn)` 被调用 | `VtInputHandlerTests.cs`：C5_* 5 例（hit / miss / wrong-row / multi / release-no-dispatch） | ✅ |
| **C6 DispatchMouseMiss** | 未命中 → `DispatchMouseMiss` 被调用 | `VtInputHandlerTests.cs`：C6_* 3 例（miss / release / non-left-ignored） | ✅ |
| **C7 DispatchWheel→ApplyScrollChange** | `cb64`→`offset+=3`，`cb65`→`offset-=3`（钳 0 与 max） | `VtInputHandlerTests.cs` C7b（wheel delta +3/-3，Scroll Mode 仍穿透）+ `ScrollControllerTests.cs` C7a（`ScrollBy(3)→offset 3`，`ScrollBy(-3)` 递减并钳位） | ✅ |
| **C8 primitive 模式鼠标** | `IsWaitingPrimitive` → `InputMouseKey(type=1, windowsButton=...)` | `VtInputHandlerTests.cs`：C8_* 6 例（key→PressPrimitiveKey / ctrl-c-exit / click→InputMouseKey / release-ignored / wheel→type2 / right-click→0x400000） | ✅ |
| **D — test_cli_scroll.py 注释** | 更新为基于 experiment-results.md 的准确描述 | 原注释（commit dbfd458）仍称"ConPTY 在应用启用 ?1000h 后会拦截"——experiment 已证伪该说法（拦截在 PTY host 层，VtParser 无 ?1000h 检查）。**已修正**为 plan D 指定文本。 | ✅（修正后） |
| **CI 集成** | 若仓库有 CI | 仓库无 `.github` / `.gitlab-ci.yml` / `azure-pipelines.yml` / `.circleci` → N/A | ⚪ N/A |

## 遗留 / 备注

- **D 原始状态为不通过**：git blame 显示该注释自 ADR-0006 实现提交（dbfd458）后再未改动，仍带实验已证伪的 `?1000h` 误述。本次验收已按 plan D 原文重写（纯注释，零代码风险）。
- **WebSocket 端到端测试**在 Python 回归中 SKIP（缺 `websockets` pip 包），属环境缺失，非代码失败；C# 单测与 CLI/HTTP 回归全绿。
- **计划外收益**：`VtParserTests`/`VtInputHandlerTests`/`ButtonRegionTrackerTests`/`ScrollControllerTests` 同时支撑 ADR-0009（ScrollController 解耦）与 ADR-0010（VtParser/IVtHost 可测性），与候选 1（GlobalStatic）共享同一测试基建。
