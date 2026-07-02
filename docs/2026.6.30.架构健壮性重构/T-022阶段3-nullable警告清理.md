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

### 2.4 CS8601（标注 98，实际 49 位置 / 98 诊断）
**模式**：可能的 null 引用赋值——`T?` 表达式赋给 `T` 目标。分两类：
1. BCL API 返回 `T?`（运行时可能 null，签名声明 `T` 或 `T?`）赋给 `T`：`Environment.ProcessPath`、`Path.GetFileName/GetDirectoryName`、`LinkedListNode<T>.Next/Previous/First/Last`、`JsonSerializer.Deserialize<T>`、`as` 表达式等。
2. **`ref` 参数传递**：局部 `string x = null` 传给 `ref string`（非 null）参数，编译器视为"赋值 null 给非 null"。

**修复**（13 文件 49 位置）：
- BCL 返回值 / `as` 表达式 / `default`：加 `!`，如 `ExePath = Environment.ProcessPath!;`、`Pointer = Pointer.Next!;`、`(term as SingleTerm)!`、`_buffer[_tail] = default!;`。
- `ref` 参数：被传 `ref` 的局部/字段用 `null!` 初始化（如 `string errMes = null!;`、`int[] margin = null!, ...;`），运行时仍为 null，由被调方内部 `CreateIfNull` 或后续判空兜底。
- `??=` 配合：`Pointer ??= Collection.First!;`。

**要点**：
- 构建输出每条诊断打印两遍，49 位置 = 98 行 CS8601，与 `.editorconfig` 标注数吻合。
- `!` 不能加在 `ref` 实参上（`ref x!` 非法），须在变量声明处用 `null!`。
- `as` 表达式须整体加 `!`：`(expr as Type)!`，而非 `expr as Type!`。
- 字段 `ref` 传递场景（`ConsoleDivPart.cs`）用 `null!` 初始化保持延迟初始化语义，不改变运行时行为。

**分布**（13 文件）：`HtmlManager.cs`(17)、`WordCollection.cs`(12)、`ErbLoader.cs`(5)、`Sys.cs`(4)、`LexicalAnalyzer.cs`(2)、`Creator.Method.cs`(2)、`JSONConfig.cs`(1)、`Process.CalledFunction.cs`(1)、`DefineMacro.cs`(1)、`Instraction.Child.cs`(1)、`CircularBuffer.cs`(1)、`VariableParser.cs`(1)、`ConsoleDivPart.cs`(1)。

### 2.5 CS8604（标注 176，实际 81 位置 / 162 诊断）
**模式**：传入可能 null 引用实参——`T?` 表达式作为 `T` 形参传入。分四类：
1. **BCL API 返回 `T?`**：`Enum.GetName`、`Exception.StackTrace`、`PropertyInfo.GetValue` 等，加 `!`。
2. **`as` 表达式**：`(array as long[])!`、`(token as StrFormWord)!`，整体加 `!`。
3. **三元表达式**：`(cond ? value : null)!`，整体加 `!`（值类型 `MixedNum?` 同模式）。
4. **局部变量/字段传参**：`exm`、`currentLine`、`lastLabelLine`、`lastLine`、`filename`、`subId`、`name`、`term3/4/5/6` 等，加 `!`。

**修复**（19 文件 81 位置）：全部用 `!`（null-forgiving）在调用点断言非 null，保持运行时行为不变。

**要点**：
- `as` 表达式须整体加 `!`：`(expr as Type)!`，而非 `expr as Type!`。
- 三元表达式同理：`(cond ? value : null)!`，整体包裹。
- 同一调用点多参数分别加 `!`（如 `new SpTInputsArgument(terms[0], terms[1], term3!, term4!, term5!, term6!)`）。
- **首遍修复后可能有遗漏**：v1 构建日志 grep 到 79 位置，修复后 v2 构建又暴露 2 位置（`Process.State.cs` 495/497，首遍 grep 漏数）。按 SOP 步骤 5 "修复一批 → 重建 → 看是否还有"迭代，v3 构建确认 0。

**分布**（19 文件，按位置数降序）：`ArgumentBuilder.cs`(22)、`Creator.Method.cs`(11)、`Instraction.Child.cs`(7)、`ErbLoader.cs`(6)、`CharacterData.cs`(4)、`VariableParser.cs`(3)、`Process.State.cs`(3)、`VariableIdentifier.cs`(3)、`ConfigData.cs`(3)、`HtmlManager.cs`(3)、`StrForm.cs`(2)、`VariableEvaluator.cs`(2)、`ExpressionParser.cs`(2)、`Process.cs`(2)、`VariableData.cs`(1)、`LogicalLineParser.cs`(1)、`Lang.cs`(1)、`LexicalAnalyzer.cs`(1)、`Process.ScriptProc.cs`(1)。

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
| 1 | CS8602 | 350 | 解引用可能空引用 | 高 | 数量大，需逐处判断 null 防护是否充分 |
| 2 | CS8600 | 674 | null 转 non-null 类型 | 高 | 多为 `(T)x` 显式转换或赋值，`!` 可解大部分 |
| 3 | CS8603 | 606 | 可能返回 null 引用 | 高 | 返回类型标注与实现不符，需调整签名或加 `?` |
| 4 | CS8618 | 686 | 构造函数未初始化非 null 字段 | 高 | 多为字段初始化，`= null!` 或 `= default!` 过渡 |
| 5 | CS8625 | 550 | null 字面量转非 null 引用 | 高 | 多为 `= null` 赋值，改 `= null!` 或调整类型 |

总剩余约 2866 条。建议每批一个 CS ID，按上表顺序推进。

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
| CS8601 | 98 | 49 | ✅ 已完成 | 2026-07-03 |
| CS8604 | 176 | 81 | ✅ 已完成 | 2026-07-03 |
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

**CS8601**（13 文件）：
- [`HtmlManager.cs`](../../Emuera.Headless/Shared/UI/Game/HtmlManager.cs)（17 处：`null!` 局部变量传 `ref`、`state.LastButtonTag = state.CurrentButtonTag!`、`buttonTag.ButtonValueStr = value!`）
- [`WordCollection.cs`](../../Emuera.Headless/Shared/Runtime/Script/Parser/WordCollection.cs)（12 处：`Pointer = Collection.First!/Last!`、`Pointer = Pointer.Next!`、`Pointer = lastPointer!`、`Pointer ??= Collection.First!`、`Pointer = next!`）
- [`ErbLoader.cs`](../../Emuera.Headless/Shared/Runtime/Script/Loader/ErbLoader.cs)（5 处：`gotoLabel.ParentLabelLine = lastLabelLine!`、`subNames[i] = (term as SingleTerm)!`、`defs[i] = def!`、`string FunctionNotFoundName = null!`）
- [`Sys.cs`](../../Emuera.Headless/Shared/Runtime/Utils/Sys.cs)（4 处：`ExePath/ExeDir/ExeName/emueraVer` 加 `!`）
- [`LexicalAnalyzer.cs`](../../Emuera.Headless/Shared/Runtime/Script/Parser/LexicalAnalyzer.cs)（2 处：`wc.Pointer = wc.Pointer.Next!`、`macroWC.Pointer = macroWC.Pointer.Next!`）
- [`Creator.Method.cs`](../../Emuera.Headless/Shared/Runtime/Script/Statements/Function/Creator.Method.cs)（2 处：`array[i] = node.Value!`、`string errMes = null!`）
- [`JSONConfig.cs`](../../Emuera.Headless/Shared/Runtime/Config/JSON/JSONConfig.cs)（1 处：`Data = JsonSerializer.Deserialize<JSONConfigData>(json)!`）
- [`Process.CalledFunction.cs`](../../Emuera.Headless/Shared/Runtime/Script/Process.CalledFunction.cs)（1 处：`convertedArg[i] = term!`）
- [`DefineMacro.cs`](../../Emuera.Headless/Shared/Runtime/Script/Data/DefineMacro.cs)（1 处：`IDWord = (Statement.Current as IdentifierWord)!`）
- [`Instraction.Child.cs`](../../Emuera.Headless/Shared/Runtime/Script/Statements/Instraction.Child.cs)（1 处：`callArg.CallFunc = call!`）
- [`CircularBuffer.cs`](../../Emuera.Headless/Shared/Runtime/Script/Statements/CircularBuffer.cs)（1 处：`_buffer[_tail] = default!`）
- [`VariableParser.cs`](../../Emuera.Headless/Shared/Runtime/Script/Statements/Variable/VariableParser.cs)（1 处：`terms = [op1!, op2]`）
- [`ConsoleDivPart.cs`](../../Emuera.Headless/Shared/Runtime/Utils/EvilMask/ConsoleDivPart.cs)（1 处：`int[] margin = null!, padding = null!, radius = null!, border = null!` 字段传 `ref`）

**CS8604**（19 文件）：
- [`ArgumentBuilder.cs`](../../Emuera.Headless/Shared/Runtime/Script/Statements/ArgumentBuilder.cs)（22 处：`SpPrintImgArgument`/`SpHtmlPrint`/`SpArraySortArgument`/`SpTInputsArgument`/`SpVarSetArgument`/`SpCVarSetArgument`/`SpArrayShiftArgument`/`RefArgument` 构造调用点加 `!`）
- [`Creator.Method.cs`](../../Emuera.Headless/Shared/Runtime/Script/Statements/Function/Creator.Method.cs)（11 处：`OutPutNode`/`Output`/`Remove`/`Insert`/`SetNode`/`Replace` 调用点 `nodes[i]!`/`nodes[0]!`/`(array as string[])!`）
- [`Instraction.Child.cs`](../../Emuera.Headless/Shared/Runtime/Script/Statements/Instraction.Child.cs)（7 处：`PrintImg` 的 `strb!`/`strm!`/三元 `!`、`caseExp.GetBool(Is!, exm)`、`state.ReturnF(ret!)`）
- [`ErbLoader.cs`](../../Emuera.Headless/Shared/Runtime/Script/Loader/ErbLoader.cs)（6 处：`labelDic.AddLabel(label!)`、`LogicalLineParser.ParseLine(..., lastLabelLine!)`、`addLine(nextLine, lastLine!)`、`(exc ... as ...)!`/`null)!`、`ParserMediator.Warn(..., func!)`）
- [`CharacterData.cs`](../../Emuera.Headless/Shared/Runtime/Script/Statements/Variable/CharacterData.cs)（4 处：`reader.ReadIntArray((array as long[])!, true)` 等 `as` 转换加 `!`）
- [`VariableParser.cs`](../../Emuera.Headless/Shared/Runtime/Script/Statements/Variable/VariableParser.cs)（3 处：`ReduceVariable(id, op1!, op2!, op3!)`）
- [`Process.State.cs`](../../Emuera.Headless/Shared/Runtime/Script/Process.State.cs)（3 处：`srcArgs.SetTransporter(exm!)`、`call.TopLabel.Arg[i].SetValue(..., exm!)` x2）
- [`VariableIdentifier.cs`](../../Emuera.Headless/Shared/Runtime/Script/Statements/Variable/VariableIdentifier.cs)（3 处：`nameDic.Add(..., Enum.GetName(...)!)` x3）
- [`ConfigData.cs`](../../Emuera.Headless/Shared/Runtime/Config/ConfigData.cs)（3 处：`ConfigWarn(..., exc.StackTrace!)` x2、`Warn(..., exc.StackTrace!)`）
- [`HtmlManager.cs`](../../Emuera.Headless/Shared/UI/Game/HtmlManager.cs)（3 处：`new StringStyle(..., fontname!)`、`ConsoleDivPart(..., tagInfo.StyledBox!)`、`new ConsoleImagePart(src, srcb!, srcm!, ...)`）
- [`StrForm.cs`](../../Emuera.Headless/Shared/Runtime/Script/Data/StrForm.cs)（2 处：`[operand, second!, third!]` x2）
- [`VariableEvaluator.cs`](../../Emuera.Headless/Shared/Runtime/Script/Statements/Variable/VariableEvaluator.cs)（2 处：`CheckDataByFilename(filename!, type)` x2）
- [`ExpressionParser.cs`](../../Emuera.Headless/Shared/Runtime/Script/Statements/Expression/ExpressionParser.cs)（2 处：`GetVariableToken(idStr, subId!, true)`、`stack.Add(ToStrFormTerm((token as StrFormWord)!))`）
- [`Process.cs`](../../Emuera.Headless/Shared/Runtime/Script/Process.cs)（2 处：`handleExceptionInSystemProc(ec, currentLine!, true)`、`handleException(ec, currentLine!, true)`）
- [`VariableData.cs`](../../Emuera.Headless/Shared/Runtime/Script/Statements/Variable/VariableData.cs)（1 处：`UserDefinedCharaVarList.Add(ret!)`）
- [`LogicalLineParser.cs`](../../Emuera.Headless/Shared/Runtime/Script/Parser/LogicalLineParser.cs)（1 处：`new StrAsignArgument(varName, varData.Lengths, value!)`）
- [`Lang.cs`](../../Emuera.Headless/Shared/Runtime/Utils/EvilMask/Lang.cs)（1 处：`(prop.GetValue(null, null) as TranslatableString)!`）
- [`LexicalAnalyzer.cs`](../../Emuera.Headless/Shared/Runtime/Script/Parser/LexicalAnalyzer.cs)（1 处：`wc.Collection.Remove(wc.Pointer.Previous!)`）
- [`Process.ScriptProc.cs`](../../Emuera.Headless/Shared/Runtime/Script/Process.ScriptProc.cs)（1 处：`((StrDataArgument)func.Argument).Var.SetValue(str!, exm)`）

**配置**：
- [`.editorconfig`](../../.editorconfig)：删除 `CS8605`、`CS8767`、`CS8629`、`CS8601`、`CS8604` 五条抑制规则
