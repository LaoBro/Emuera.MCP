# Agent 文件夹重构剩余任务

> 基于 2026-06-24 的全面代码重构分析，记录已完成项与待办项。
> 已完成项见文末"已完成"章节。

## 优先级说明

- **P1**：高优先级，短期（1-2 周）内完成
- **P2**：中优先级，按需推进
- **P3**：低优先级，长期改进

---

## P1 - 高优先级

### 1. 提取重复代码（已完成）

**状态**：三处重复均已消除。`LeadingDisplayWidth` 统一到 `TerminalDisplayWidth`；`TryWrite(string)` 统一到 `TerminalCursor`；Win32 P/Invoke 提取为 `Win32ConsoleInterop` 静态类。

**待办**：

- [x] 统一 `LeadingDisplayWidth` 到 `TerminalDisplayWidth`，移除 `ButtonRegionTracker.cs#L50` 与 `ButtonSelectionMode.cs` 中的重复实现
- [x] 统一 `TryWrite(string)` 到 `TerminalCursor`（已存在），移除 `AgentCliVtScreen.cs#L91`、`AgentCliVtInput.cs#L534` 中的重复
- [x] 提取 Win32 P/Invoke 声明为内部 `Win32ConsoleInterop` 静态类（当前仅 `WindowsVtInput` 一处，但建议规范化）

**预期收益**：消除 3 处代码重复，便于统一维护
**工作量**：0.5 天
**风险**：低 - 纯机械重构

---

### 2. 引入 IConsoleStateView 解耦（已完成）

**状态**：已定义 `IConsoleStateView` 接口封装"读后清零"消费语义，`EmueraConsole` 实现该接口，Agent 层所有直接 `internal` 字段访问已消除。

**待办**：

- [x] 定义 `IConsoleStateView` 接口，封装"读后清零"消费语义
- [x] `EmueraConsole` 实现该接口
- [x] `AgentCliProtocol`/`TerminalRenderer`/`ButtonSelectionMode` 改为依赖接口

**预期收益**：消除对 `internal` 字段的直接依赖，便于 mock 测试
**工作量**：1 天
**风险**：中 - 需确保消费语义（读后清零）保持一致

---

### 3. 建立 C# 单元测试项目

**现状**：整个 Agent 文件夹无任何 C# 单元测试，所有测试为 Python 集成测试。

**待办**：

- [ ] 新建 `Emuera.Headless.Tests` 项目
- [ ] 优先覆盖纯逻辑类：
  - `VtParser` 状态机（VT 序列解析）
  - `ButtonRegionTracker.HitTest`
  - `TerminalDisplayWidth.IsWideChar` / `ReplaceForTerminal`
  - `ButtonSelectionMode` 导航算法（需先完成接口解耦以便 mock）
  - `AgentProtocolBase.DispatchInput`

**预期收益**：为后续重构提供回归保护网
**工作量**：2-3 天（建立项目 + 首批测试）
**风险**：低

---

### 4. 封装 TerminalDisplayWidth 全局可变状态

**现状**：[TerminalDisplayWidth.cs#L17-L20](file:///d:/LaoBro/Emuera.MCP/Emuera.Headless/Agent/TerminalDisplayWidth.cs#L17) 使用 `internal static` 可变字段：

```csharp
internal static bool BoxDrawingIsWide = false;
internal static bool GeometricIsWide = false;
internal static bool MiscSymbolsIsWide = false;
internal static bool BlockElementsIsWide = false;
```

全局可变状态导致无法并发使用、测试需重置状态、隐式依赖初始化顺序。

**待办**：

- [ ] 封装为 `TerminalCharWidthConfig` 实例配置对象
- [ ] `Detect()` / `ApplyWidthHint()` 返回配置实例
- [ ] `ReplaceForTerminal` 接收配置参数
- [ ] 调用方（`EmueraConsole.AgentBridge` 的 `FormatLineForTerminal`）持有配置实例

**预期收益**：消除全局可变状态，提升可测试性
**工作量**：0.5 天
**风险**：低 - 纯内部重构

---

## P2 - 中优先级

### 5. 主循环逻辑模板化

**现状**：`RunVtMainLoop` 与 `RunConsoleKeyLoop` 共享大量相同逻辑（检查 `_needFullRefresh`、超时处理、`FlushBuffer`/`SyncButtonState` 调用序列），仅输入读取方式不同。

**待办**：

- [ ] 提取通用循环模板，仅输入读取作为策略注入
- [ ] 验证 VT/非 VT 路径行为一致

**预期收益**：减少约 30 行重复代码
**工作量**：1 天
**风险**：中 - 需验证两条路径行为等价

---

### 6. 按钮导航算法重构

**现状**：`ButtonSelectionMode.ProcessButtonModeKey` 中四个方向 `case` 块结构几乎相同，仅方向判断条件不同（约 90 行）。

**待办**：

- [ ] 提取通用 `FindNextButton` 方法，通过 `isCandidate` 谓词与 `ranker` 比较器参数化方向
- [ ] 补单元测试验证导航正确性

**预期收益**：90 行 → 30 行，逻辑集中可测试
**工作量**：0.5 天
**风险**：中 - 需测试固化当前导航行为

---

### 7. AgentLog 缓冲优化

**现状**：[AgentLog.cs#L41](file:///d:/LaoBro/Emuera.MCP/Emuera.Headless/Agent/AgentLog.cs#L41) 每次调用 `File.AppendAllText`，每次开关文件，在高频轮询循环中产生不必要 I/O。

**待办**：

- [ ] 改用带缓冲的 `StreamWriter`（`AutoFlush=false`）
- [ ] 实现 `IDisposable`，进程退出时 flush
- [ ] 注册 `AppDomain.ProcessExit` 钩子确保落盘

**预期收益**：减少 I/O 开销
**工作量**：1 小时
**风险**：低

---

## P3 - 低优先级

### 8. VtParser 独立文件

**现状**：`AgentCliVtInput.cs` 共 571 行，包含 4 个类（`AgentCliVtInput`/`WindowsVtInput`/`UnixVtInput`/`VtParser`）。

**待办**：

- [ ] `VtParser` 拆分到独立文件 `VtParser.cs`
- [ ] 评估 `UnixVtInput` 空桩是否删除（违反 LSP，`TryCreate` 永远返回 null）

**预期收益**：文件职责清晰
**工作量**：0.5 小时
**风险**：低

---

### 9. 命名规范统一

**待办**：

- [ ] `s_log` 静态字段改为 `Log` 或 `_log`（[AgentCliVtInput.cs#L114](file:///d:/LaoBro/Emuera.MCP/Emuera.Headless/Agent/AgentCliVtInput.cs#L114)）
- [ ] `ButtonListEquals` 重命名为 `ButtonInputKeysEqual`（当前仅比较 `InputKey`，命名误导）
- [ ] 统一 `record struct` 命名风格

**工作量**：0.5 小时
**风险**：低

---

## 已完成

| 日期 | 任务 | 产出 |
|------|------|------|
| 2026-06-25 | 完成 P1.2 引入 IConsoleStateView 解耦 | 新建 `IConsoleStateView.cs`（`ConsumeNeedFullRefresh`/`ConsumePendingEraseRows`/`AppendToAgentBuffer`）；`EmueraConsole` 实现接口；`AgentCliProtocol`(3处)/`TerminalRenderer`/`AgentProtocolBase` 改用接口方法，消除全部直接 `internal` 字段访问 |
| 2026-06-25 | 完成 P1.1 提取重复代码 | `LeadingDisplayWidth` 统一到 `TerminalDisplayWidth`；`TryWrite(string)` 统一到 `TerminalCursor`（移除 `AgentCliVtScreen`/`WindowsVtInput` 重复）；新建 `Win32ConsoleInterop.cs` 集中 P/Invoke 声明 |
| 2026-06-24 | 删除 `AgentCliMouseInput` 废弃代码 | 移除 275 行死代码 |
| 2026-06-24 | 拆分 `AgentCliProtocol` God Class | 新建 `ButtonSelectionMode.cs`(367行)、`CountdownRenderer.cs`(95行)、`TerminalRenderer.cs`(120行)；`AgentCliProtocol` 从 919 行降至 329 行（-64%） |

---

## 风险提示

1. **接口解耦（任务 2）** 需保持"读后清零"消费语义，否则会导致刷新逻辑异常
2. **主循环模板化（任务 5）** 需验证 VT/非 VT 路径在超时、resize、按钮同步等场景行为完全一致
3. **按钮导航重构（任务 6）** 有隐式假设（同行按钮按出现顺序排列），需用测试固化当前行为
4. 项目使用 `Nullable=disable`，重构时需保持一致风格
