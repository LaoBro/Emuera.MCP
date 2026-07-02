# T-022 阶段 3：Shared/ 历史代码 nullable 警告清理

> 创建：2026-07-03
> 范围：[`Emuera.Headless/Shared/**`](../../Emuera.Headless/Shared/) 子树（T-023 物理迁移自 `Emuera/` 的历史共享源码）
> 目标：逐步撤销 [`.editorconfig`](../../.editorconfig) 中 `[Emuera.Headless/Shared/**]` 段的 CS86xx 抑制规则，让历史代码 nullable-clean，最终全量通过 `TreatWarningsAsErrors=true` 构建。

## 1. 背景与约束

- [`Emuera.Headless.csproj`](../../Emuera.Headless/Emuera.Headless.csproj) 已启用 `<Nullable>enable</Nullable>` + `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>`。
- 历史共享源码（`Shared/`）无 nullable 标注，I-12 阶段 1 通过 `.editorconfig` 按 CS86xx ID 逐条 `severity = none` 抑制，避免 Headless 构建被历史警告淹没。
- 阶段 3（本任务）逐步清理警告并撤销抑制，每批一个 CS ID。
- **维护边界**：`Shared/` 是历史代码，原则上不重构逻辑，只做"让编译器静默"的最小改动，保持运行时行为不变。

## 2. 已完成清理（经验样本）

### 2.1 CS8605（标注 2，实际 0）
- 当前代码已不存在该警告，`.editorconfig` 注释中的统计数字过时。
- 直接删除抑制规则即可。

### 2.2 CS8767（标注 6，实际 3 条 / 2 位置）
**模式**：接口实现的引用类型 null 性与隐式实现成员不匹配。
- `IComparable<T>.CompareTo(T? other)` 实现参数未标 nullable。
- `IEqualityComparer<T>.Equals(T? x, T? y)` 实现参数未标 nullable。

**修复**（[`LogicalLine.cs`](../../Emuera.Headless/Shared/Runtime/Script/Statements/LogicalLine.cs)）：
- `FunctionLabelLine.CompareTo(FunctionLabelLine other)` → `CompareTo(FunctionLabelLine? other)`，并补 `if (other is null) return 1;`（IComparable 契约：null 排在非 null 之前）。
- `GotoLabelLine.Equals(GotoLabelLine x, GotoLabelLine y)` → `Equals(GotoLabelLine? x, GotoLabelLine? y)`，原方法体已有 `if (x == null || y == null) return false;`，逻辑无需调整。

**要点**：参数改 nullable 后，方法体内访问该参数成员前必须确认已有 null 防护，否则会引入新的 CS8602。

### 2.3 CS8629（标注 52，实际 26 位置 / 52 诊断）
**模式**：访问 `Nullable<T>.Value`。全部为 `ScriptPosition?.Value`（`Position` 属性类型为 `ScriptPosition?`，见 `LogicalLine.cs:23`）。历史代码在调试日志/排序场景假设 Position 非 null（debug 命令的 Line 除外，但不进入这些路径）。

**修复**：null-forgiving 运算符 `!`，保持运行时行为完全不变（若 Position 真为 null 仍抛 `InvalidOperationException`，与原 `.Value` 访问一致）：
- `Xxx.Position.Value.Yyy` → `Xxx.Position!.Value.Yyy`
- 局部变量/参数：`pos.Value` → `pos!.Value`、`position.Value` → `position!.Value`

**分布**（10 文件 26 位置）：`Process.State.cs`(9)、`ErbLoader.cs`(4)、`Process.cs`(3)、`LogicalLine.cs`(2)、`Process.CalledFunction.cs`(2)、`Instraction.Child.cs`(2)、`PluginManager.cs`(1)、`LabelDictionary.cs`(1)、`IdentifierDictionary.cs`(1)、`Creator.Method.cs`(1)。

## 3. 标准操作流程（SOP）

每批一个 CS ID，按以下步骤：

1. **暴露警告**：`.editorconfig` 中该 `CSxxxx` 的 `severity = none` → `severity = warning`。
2. **捕获完整构建输出**：用 `Start-Process -RedirectStandardOutput`（见下方"踩坑"），不要用 `2>&1 | Out-File`。
3. **Grep 定位**：在 log 中搜 `CSxxxx`，按文件分组统计。注意输出可能重复（build 多次打印同一诊断）。
4. **逐文件修复**：`replace_all` 优先；若失败改用精确行上下文 `Edit`。
5. **全量构建验证**：`dotnet build --no-incremental`，确认 0 警告 0 错误。
6. **撤销抑制**：从 `.editorconfig` 删除该 `CSxxxx` 规则行。
7. **再次全量构建**确认（删除规则后默认 severity 仍为 warning，行为不变，但需确认无遗漏）。

## 4. 踩坑记录

1. **统计数字过时**：`.editorconfig` 注释里的警告数（如 `CS8605（2）`、`CS8767（6）`）与实际不符，必须以实际构建输出为准。
2. **PowerShell 输出捕获**：
   - `dotnet build ... 2>&1 | Out-File` 会丢失大部分输出（仅留总结行）。
   - `cmd /c "... > log 2>&1"` 在本环境被安全策略禁用。
   - 可靠方式：`Start-Process -FilePath dotnet -ArgumentList ... -RedirectStandardOutput build.log -RedirectStandardError build.err.log -Wait -PassThru -NoNewWindow`，并设置 `[Console]::OutputEncoding = [System.Text.Encoding]::UTF8`。
3. **Edit `replace_all` 短字符串失败**：对 `Position.Value` 这类短 `old_string`，`replace_all` 在部分文件报 "String to replace not found"（Grep 能匹配但 Edit 找不到，原因未明）。改用包含缩进与同行上下文的精确 `Edit`（`replace_all=false`）可靠。建议 `old_string` 至少包含一整行带缩进。
4. **`TreatWarningsAsErrors` 双刃**：`severity=warning` 立即变 error，构建失败即暴露位置；但依赖链断裂时编译器可能漏报下游文件，需"修复一批 → 重建 → 看是否还有"迭代。
5. **诊断合并**：同一行多个 `.Value` 访问可能只报一处列号，修复时整行所有同类访问都要改，否则下一轮重建仍报错。
6. **临时文件清理**：构建 log 用完即删，避免污染工作区（已被 gitignore 覆盖也无妨，保持干净）。

## 5. 修复原则

- **最小改动**：只做让编译器静默的必要修改，不重构历史逻辑。
- **保持运行时语义**：优先用 `!`（null-forgiving）过渡，不强行加 null 检查改变分支行为。
- **接口契约优先**：接口实现参数 nullable 标注必须匹配接口定义（如 `IComparable<T>.CompareTo(T?)`、`IEqualityComparer<T>.Equals(T?, T?)`）。
- **显式表达意图**：历史代码"假设非 null"的场景，用 `!` 明确标注；真有可能为 null 的，补 `null` 检查或 `GetValueOrDefault()`。
- **整行清理**：一行内同类警告一次改完，避免迭代往返。

## 6. 剩余清理计划

剩余抑制的 CS86xx（按建议顺序，由少到多 / 由易到难）：

| 序 | CS ID | 标注数 | 模式（预估） | 难度 | 备注 |
|---|---|---|---|---|---|
| 1 | CS8601 | 98 | 可能 null 引用赋值 | 中 | 数量适中，模式较集中，适合作为下一批 |
| 2 | CS8604 | 176 | 传入可能 null 实参 | 中 | 跨文件调用点，按文件分组修复 |
| 3 | CS8602 | 350 | 解引用可能空引用 | 高 | 数量大，需逐处判断 null 防护是否充分 |
| 4 | CS8600 | 674 | null 转 non-null 类型 | 高 | 多为 `(T)x` 显式转换或赋值，`!` 可解大部分 |
| 5 | CS8603 | 606 | 可能返回 null 引用 | 高 | 返回类型标注与实现不符，需调整签名或加 `?` |
| 6 | CS8618 | 686 | 构造函数未初始化非 null 字段 | 高 | 多为字段初始化，`= null!` 或 `= default!` 过渡 |
| 7 | CS8625 | 550 | null 字面量转非 null 引用 | 高 | 多为 `= null` 赋值，改 `= null!` 或调整类型 |

总剩余约 3140 条。建议每批一个 CS ID，按上表顺序推进。

### 大批量批次策略
- 单批 >200 条时，先用 Grep log 按文件聚合，识别 top-N 高频文件。
- 可用 `Task` subagent（`subagent_type=general_purpose_task`）并行分析多个文件的修复方案，但**实际 Edit 仍由主会话执行**（精确替换需可控）。
- 同一文件的多个 Edit 必须顺序执行（避免 old_string 失配）；不同文件可并行 Edit。

## 7. 验证标准

每批清理完成的判定：
1. `dotnet build Emuera.Headless/Emuera.Headless.csproj -c Debug --nologo --no-incremental` 全量构建 **0 警告 0 错误**。
2. 该 `CSxxxx` 已从 `.editorconfig` `[Emuera.Headless/Shared/**]` 段删除。
3. 删除规则后再次全量构建仍 **0 警告 0 错误**。

可选回归：
- `python tests/run_all.py --binary D:/LaoBro/Emuera.MCP/Emuera.Headless/bin/Debug/net10.0/Emuera.Headless.exe --game-dir test_game`
- nullable 修复多为编译期，运行时行为不变，回归风险低；但 CS8767 类（接口契约）改动建议跑一次 server 模式冒烟。

## 8. 进度跟踪

| CS ID | 标注 | 实际 | 状态 | 完成日期 |
|---|---|---|---|---|
| CS8605 | 2 | 0 | ✅ 已完成 | 2026-07-03 |
| CS8767 | 6 | 3 | ✅ 已完成 | 2026-07-03 |
| CS8629 | 52 | 26 | ✅ 已完成 | 2026-07-03 |
| CS8601 | 98 | — | ⬜ 待清理 | — |
| CS8604 | 176 | — | ⬜ 待清理 | — |
| CS8602 | 350 | — | ⬜ 待清理 | — |
| CS8600 | 674 | — | ⬜ 待清理 | — |
| CS8603 | 606 | — | ⬜ 待清理 | — |
| CS8618 | 686 | — | ⬜ 待清理 | — |
| CS8625 | 550 | — | ⬜ 待清理 | — |

> "实际"列以清理时构建输出为准；"标注"列来自原 `.editorconfig` 注释，可能过时。

## 9. 附：本次已修改文件清单

**CS8767**：
- [`Emuera.Headless/Shared/Runtime/Script/Statements/LogicalLine.cs`](../../Emuera.Headless/Shared/Runtime/Script/Statements/LogicalLine.cs)（`FunctionLabelLine.CompareTo`、`GotoLabelLine.Equals`）

**CS8629**（10 文件）：
- [`Process.State.cs`](../../Emuera.Headless/Shared/Runtime/Script/Process.State.cs)
- [`ErbLoader.cs`](../../Emuera.Headless/Shared/Runtime/Script/Loader/ErbLoader.cs)
- [`Process.cs`](../../Emuera.Headless/Shared/Runtime/Script/Process.cs)
- [`LogicalLine.cs`](../../Emuera.Headless/Shared/Runtime/Script/Statements/LogicalLine.cs)
- [`Process.CalledFunction.cs`](../../Emuera.Headless/Shared/Runtime/Script/Process.CalledFunction.cs)
- [`Instraction.Child.cs`](../../Emuera.Headless/Shared/Runtime/Script/Statements/Instraction.Child.cs)
- [`PluginManager.cs`](../../Emuera.Headless/Shared/Runtime/Utils/PluginSystem/PluginManager.cs)
- [`LabelDictionary.cs`](../../Emuera.Headless/Shared/Runtime/Script/Data/LabelDictionary.cs)
- [`IdentifierDictionary.cs`](../../Emuera.Headless/Shared/Runtime/Script/Data/IdentifierDictionary.cs)
- [`Creator.Method.cs`](../../Emuera.Headless/Shared/Runtime/Script/Statements/Function/Creator.Method.cs)

**配置**：
- [`.editorconfig`](../../.editorconfig)：删除 `CS8605`、`CS8767`、`CS8629` 三条抑制规则
