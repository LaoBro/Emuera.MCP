# 移动端性能（Android 回合卡顿）诊断与优化计划

> 日期：2026.8.3
> 背景：Android（MAUI）上点击按钮后，下一回合画面延迟出现；期间前端可正常操作（UI 线程未被占），问题在游戏循环线程的同步计算时间（`AgentJsonlProtocol.StepAsync` 中 `ui.Invoke(DispatchInput)` → `BuildTurn()`，见 `Emuera.Headless.Core/Agent/AgentJsonlProtocol.cs:67-69`）。
> 约束：**游戏文件（ERB/CSV）不可修改**，只允许提升 C# 引擎执行效率。

## 原则

1. **取证先于优化**：先让"慢回合"能自动转化为可分析数据，再谈改什么。
2. **常态零成本**：所有诊断设施常驻开销必须可忽略（每秒百次读一个字段量级）；有数据落盘仅发生在慢回合之后。
3. **不做常规计时层**：不给每个回合计时打日志（慢的回合体感已足够明显）；只留一个 Stopwatch 作为"慢回合扳机"（超阈值才触发取证，低于阈值仅一次毫秒比较、零 I/O）。
4. **每次改动可单独验证**：一个条目 = 一次实验，改动独立可回滚，用 checklist 逐项勾销。

---

## 阶段 1：慢回合取证系统（先建，产出数据）

### 1.1 慢回合扳机

- [ ] 在 `AgentJsonlProtocol.StepAsync` 用一个 Stopwatch 包住 `DispatchInput`（`AgentJsonlProtocol.cs:67`）；耗时 > 阈值（如 800ms，可配置，环境变量或 appsettings）时，把该回合标记为"慢回合"并触发下方各转储。
- [ ] 转储内容统一写入独立文件（如 ExeDir 或 AppData 的 `slow_turn_<n>.log`），格式自含：回合序号、触发输入、耗时、存档名、协议版本。

### 1.2 ERB 级 PC 采样器（核心取证手段）

> 不改游戏文件，但可以采样解释器的"程序计数器"——解释器每执行一行即更新 `currentLine`（`Emuera.Headless.Core/Shared/Runtime/Script/Process.State.cs:158` 的 `ShiftNextLine`）。

- [ ] 常驻后台采样线程（10ms 间隔）：`Volatile.Read` 当前 `currentLine`（文件+行号），累加 `(file, line) → sample count` 直方图。
- [ ] 回合结束时若被判为慢回合，把直方图冻结写盘，得到"该回合脚本火焰图"（1 秒慢回合 ≈ 100 个样本，足以热定位）。
- [ ] 采样器默认开启但只在慢回合写盘；提供开关可完全关闭。

### 1.3 指令族计数

- [ ] 采样器顺带记录当前指令类型（`ScriptProc.cs:132-891` 的 switch 分发处已有类型信息），聚合 `PRINTFORM×340 / CSV查询×500 / SET×1200` 这类分布，写进同一份 dump——判断是"打印密集"还是"查询密集"。

### 1.4 CSV 查询热度

- [ ] 在 `VariableEvaluator` 的 CSV 查询入口累计 `(csv 文件名 → 次数+ms)`（先例：`VariableEvaluator.cs:2249` 附近 Save/Load 计时模式），慢回合时一并输出。

### 1.5 分配/GC 统计

- [ ] 慢回合内记录 `GC.GetTotalAllocatedBytes()` 差值——区分"CPU 密集"与"分配过猛引发 GC 暂停"（后者是引擎侧池化/Span 化的主攻方向）。

### 1.6 输入序列记录 → PC 回放

- [ ] dump 中记录触发输入与回合序列；攒够后整理成输入序列，**在 Windows 上用 CLI 模式回放同一存档**（PC 上 `dotnet-trace collect` 零门槛拿方法级火焰图），把 Android 上难剖析的 C# 热点搬到 PC 复现。

**阶段 1 验收**：在 PC CLI 上用 test_game 或真实游戏复现一个慢回合，产出包含行号直方图 + 指令族分布 + CSV 热度的 dump；Android 上同一操作能产出同样格式的 dump。

---

## 阶段 2：零风险固定开销清理（可不依赖取证直接做）

### 2.1 去掉 16ms 帧同步忙等

- [x] `ConsoleRefreshHandler.cs:54`（及 `ConsolePrintManager.cs:566-568`）的 `while (elapsed < msPerFrame) _ui.ProcessEvents()` 在 headless/移动端是纯空转（`ProcessEvents` 为 no-op），每回合固定浪费最多 16ms。改为 `msPerFrame<=0` 时直接跳过或立即退出。
- [x] 验证：`tests/run_all.py` 全绿 + 移动端体感。

### 2.2 按钮匹配全表扫描改索引

- [x] `ConsoleInputHandler.cs:211-300` 输入时 `Reverse(displayLineList)` 扫全部历史行匹配按钮；改为维护"当前回合可见按钮"索引（`LastButtonGeneration` 已有失效机制，ADR-0018），点击命中 O(1)。

### 2.3 Android 构建启用 Profiled AOT

- [x] `Emuera.Maui` csproj 加 `RunAOTCompilation=true`（或 profiled AOT），对解释器这类指令密集负载预期 2-4x。纯配置改动，先跑一次真机对比。
- [x] 注意：确认 Release 构建行为与包体积变化，回归 `Emuera.Maui.Tests`。

### 2.4 SAF 存档目录缓存 + 消除 N×FILEEXIST（对应 S6）

> 详细计划见 `current_plan/2026.8.3.saf-accel.md`（A1 取证日志 + O1 目录子项缓存 + O2 消除 N×FILEEXIST + O3 通配零正则 + O4 后台预取可选；完整异步化明确不做）。

- [x] 取证：`SafGameDirAccessor` 每次 `ContentResolver.Query` / `OpenInputStream` 加耗时日志（含调用方标识），真机进存档界面量化「总 IPC 次数 × 单次耗时」，区分「单次慢」与「次数多」。
- [x] 目录级枚举缓存：`ENUMERATEFILES` 结果按 (目录+pattern) 缓存（短 TTL 或变更失效，与 2.2 索引同思路）；`FileExists` 先查缓存，消除逐槽 1~2 次 Query。
- [x] `MatchWildcard` 正则复用（pattern 固定时避免每文件 `new Regex`）。
- [x] 验证：真机进存档界面 A/B（5s+ → 目标百毫秒级）；`run_all.py` 全绿。

**阶段 2 验收**：每项独立提交、独立回归；2.1/2.2 应有 C# 单测覆盖，2.3 以真机 A/B 对比（同存档同操作，录屏或取 WS 帧间隔）为准。

---

## 嫌疑清单（2026.8.3 解释器代码核查）

> 基于对 `Emuera.Headless.Core/Shared/Runtime/Script/` 的逐项核查。排序按嫌疑度。**已排除项**见文末，避免重复调研。

### S0【高·非解释器】回合级全量快照重建 + 深 diff（最大嫌疑）

- **位置**：`DisplayState.cs:183-467`（`ComputeDiff`/`Rebuild`/`BuildSnapshot`）+ `AgentJsonlProtocol.cs:241-279`（`BuildTurn` 序列化）
- **问题**：只要有新打印，每回合全量重建快照：遍历全部显示行，每行 `BuildPrintOpsForLine` 重算 segments/几何 + 深拷贝行对象 + `CommonPrefix` segment 级深比较 + JSON 序列化。**随会话行数（MaxLog=5000）线性增长**；回合内打印越多越贵。
- **解释器框架本身无嫌疑**（变量索引/跳转表分发/StrForm 拼接均高效，见已排除项）——这解释了 2.3 Full AOT 真机无体感差别的现象：瓶颈不在指令外壳，在指令语义与回合级外围。
- **对应优化**：3.3 增量快照。

### S1【中高】CSV 模板线性扫描

- **位置**：`ConstantData.cs:1222-1231` `GetCharacterTemplateFromCsvNo`（foreach O(n)，被 `VariableEvaluator.cs:1063` `AddCharacterFromCsvNo` 即 ADDCHARA 路径 + `Creator.Method.cs:2055+` CSVxxx 方法族每次调用）；`ConstantData.cs:1202-1210` `GetCharacterTemplate` 同样线性；`GetCharacterTemplate_UseSp`（`:1212-1220`）用 BinarySearch 但每次调用分配 Comparer lambda。
- **问题**：角色模板数百~数千时，每次 ADDCHARA/CSVxxx 是 O(n) 扫描；叠加 `AddCharacterFromCsvNo` 每次 `new CharacterData` 全量分配数组。
- **对应优化**：3.1。

### S2【中】`GetJoinedStr` 循环 `+=` 拼接（O(n²) 分配）

- **位置**：`VariableEvaluator.cs:277-309`
- **问题**：1D 数组走 `string.Join`（高效），但 2D/3D/int 数组走 `sum += value.ToString()` 逐元素拼接，每次迭代分配新字符串。JOIN/数组转字符串路径触发。
- **对应优化**：3.2。

### S3【中低】`Html2PlainText` 未缓存正则

- **位置**：`HtmlManager.cs:654-658`——静态 `Regex.Replace` 每次构造解释器（未走 `RegexFactory` 缓存）。仅 HTML 转纯文本函数（`Creator.Method.cs:5133`）触发，低频。
- **对应优化**：3.2。

### S4【低】`CheckEscape` 每 PRINT 固定分配

- **位置**：`ExpressionMediator.cs:81-82`——每次 PRINT 都 `new CharStream + new StringBuilder`，且无论有无转义都走一遍循环。每指令固定小开销。
- **对应优化**：3.2。

### S5【低】`FindChara` 每次 `new FixedVariableTerm` + `GetCharacterTemplate_UseSp` Comparer 分配

- **位置**：`VariableEvaluator.cs:1263`、`ConstantData.cs:1212-1220`
- **对应优化**：3.2（若热度图显示调用密集）。

### S6【高·外部 IO】SAF 存档文件列表 N+1 Binder IPC（真机 5s+）

- **位置**：`Emuera.Maui/Services/SafGameDirAccessor.cs`（`EnumerateUri` / `ResolveExistingFileUri` / `FindChildDocument`）+ 脚本层 `Creator.Method.cs:226` `EnumFilesMethod`（ENUMERATEFILES）、`:1258` `ExistFileMethod`（FILEEXIST）→ `SafCompat` → `IGameDirAccessor`
- **问题**：每次 `ContentResolver.Query` / `OpenInputStream` 是跨进程 Binder IPC（单次 50~150ms 常见）。存档界面（游戏 ERB 自绘，headless 无系统对话框）典型 N 槽位：`ENUMERATEFILES` 1 次 + 每槽 `FILEEXIST` 1~2 次 Query（`ResolveExistingFileUri` 先 `TryQueryDocument`、未中再 `FindChildDocument` 全目录遍历）+ 读存档头 2 次 IPC → 30 槽 ≈ 60~120 次 IPC ≈ 3~9s。uemuera 走本地文件系统（syscall 微秒级），对照体感瞬时。
- **备注**：ADR-0019 决策正确（MANAGE_EXTERNAL_STORAGE 不可行，SAF 是唯一合规路径），问题在把 IPC 当 syscall 用（N+1 + 无缓存）；`FileExists` 路径内部无耗时日志，先取证区分「单次慢」与「次数多」。
- **对应优化**：2.4。

### 已排除（不必再查）

- **运行期文件 IO**：CSV/ERB/ERH 启动时一次性读入 `Preload.cs` 缓存（`EraStreamReader.cs:42-53`），运行期零文件读取；emuera.log/time.log/AgentLog 均非常规每回合触发。
- **运行期文本重解析**：词法/语法/参数解析全部在加载期完成（`LogicalLineParser`/`ExpressionParser`/`ArgumentBuilder`），运行期 `VariableToken.GetIntValue` 是纯数组索引（`VariableToken.cs:597-605`）。
- **正则缓存**：`RegexFactory.cs:10-32` 静态缓存 + `Compiled`；`ButtonStringCreator.cs:175` static readonly；仅 S3 例外。
- **指令分发**：`ScriptProc.cs:132-137` `switch (FunctionCode)` → JIT 跳转表 O(1)。
- **PRINTFORM 拼接**：`StrForm.cs:203-215` 用 `DefaultInterpolatedStringHandler`，常量段直接返回原串。
- **编码转换**：仅加载期（`EncodingHandler.cs:27-45`）。

---

## 阶段 3：数据驱动的引擎优化（按嫌疑清单对症下药）

### 3.1 CSV 查找优化（对应 S1）

- [x] `ConstantData.GetCharacterTemplateFromCsvNo`（`ConstantData.cs:1222-1231`）与 `GetCharacterTemplate`（`:1202-1210`）的 O(n) 线性扫描改为索引（`No` → 模板字典，或复用 `GetCharacterTemplate_UseSp` 的二分思路且消除每次 Comparer 分配）。
- [x] `AddCharacterFromCsvNo`（`VariableEvaluator.cs:1063`）若热度高，评估 `CharacterData` 数组复用/懒分配。
- [ ] 收益验证：同一慢回合 dump 前后对比（样本数应显著下降）。（2026.8.4 按用户要求跳过）

### 3.2 热路径减分配（对应 S2/S3/S4/S5）

- [x] `GetJoinedStr`（`VariableEvaluator.cs:277-317`）2D/3D/int 数组路径改 `StringBuilder` 或预分配 char[]，消除 O(n²) `+=`。
- [x] `Html2PlainText`（`HtmlManager.cs:654-658`）改走 `RegexFactory` 静态缓存。
- [x] `CheckEscape`（`ExpressionMediator.cs:81-82`）无转义快速路径：先扫描是否存在 `\` 再分配，或复用缓冲。
- [ ] 对照 1.5 的分配字节数验证。（2026.8.4 跳过：1.5 取证系统未实施，无基线可比）

### 3.3 增量快照 diff（对应 S0）✅ 已完成（详见 `current_plan/2026.8.3.incremental-snapshot.md`）

- [x] `DisplayState` 加 `_lineCache`（行对象 → `DisplayLine` 引用缓存），`BuildSnapshotIncremental` 命中复用、未命中才 `BuildPrintOpsForLine`；孤儿兜底阈值清理；`LinesEqual` 引用短路。
- [x] 步骤 0 审计通过（行对象入列后不可变）；协议/前端零改动。
- [x] 单测 7 新增 + 406 全绿；`run_all.py` 11/13（2 FAIL 为基线复现的既有问题）；前端 383 全绿。
- [x] 性能：5200 行长会话压测（3 轮均值）fill turn **0.317s→0.114s（2.8x）**、稳态回合 25-60% 提升、GET /snapshot 1.35x，无变慢。

### 3.4 GC 配置微调（可选）

- [ ] 若 1.5 显示 GC 暂停为主：回合执行期间临时 `GCSettings.LatencyMode = SustainedLowLatency` 或 Android 上合适的 GC 配置，回合结束恢复。

---

## 进度记录

| # | 条目 | 状态 | 结果摘要 |
|---|------|------|----------|
| 1.1 | 慢回合扳机 | ☐ | |
| 1.2 | ERB 级 PC 采样器 | ☐ | |
| 1.3 | 指令族计数 | ☐ | |
| 1.4 | CSV 查询热度 | ☐ | |
| 1.5 | 分配/GC 统计 | ☐ | |
| 1.6 | 输入序列 → PC 回放 | ☐ | |
| 2.1 | 16ms 忙等清理 | ✅ 完成 | `msPerFrame` 默认 `1000/60`→`0`（headless 禁用帧同步），`ConsoleRefreshHandler.cs:54` 与 `ConsolePrintManager.cs:566` 忙等加 `msPerFrame>0` 保护；WinForms 旧引擎不动。单测 3 新增 + xUnit 409 全绿；run_all.py 14/14 全绿。期间修复 I-11 既有失败（根因：测试 `start_server` 从不读 stdout PIPE，第二次加载游戏时 4KB 缓冲写满 → `Console.Out.Flush()` 阻塞 → server 卡死；修复：`emuera_server.py` 加 stdout 排空线程） |
| 2.2 | 按钮匹配索引 | ✅ 完成 | 新增 `ButtonIndex`（当前代按钮 int/string 字典，O(1) 命中）；失效双信号：行结构变更（`ConsolePrintManager` 3 处 `Invalidate()`）+ 代切换（`lastButtonGeneration`，ADR-0018）。`ConsoleInputHandler` 的 IntButton/StrButton 主扫描改索引查询，escapedParts div 回退保留。等价性：Gen 单调 ⇒ 旧代行后无当前代按钮；同值多按钮覆盖保留最新行。单测 12 新增 + xUnit 423 全绿；run_all.py 14/14 全 PASS（含 JSONL+buttons） |
| 2.3 | Profiled AOT | ✅ 完成（无体感差异） | 已提交 `0f11076`。Full AOT 生效确认（`android-arm64/aot/` 含 `Emuera.Headless.Core.dll.so`），真机 A/B 体感无显著差别 → 瓶颈不在 JIT/解释外壳，见嫌疑清单 S0 |
| 2.4 | SAF 存档目录缓存 | ✅ | |
| 3.1 | CSV 查找优化 | ✅ 完成 | `ConstantData` 三查询方法索引化：`_noMap`/`_csvNoMap` 字典（`EnsureCharacterMaps`，加载末尾构建一次 + 幂等懒兜底），`GetCharacterTemplate`/`GetCharacterTemplateFromCsvNo` O(n)→O(1)，`GetCharacterTemplate_UseSp` 弃 BinarySearch+每次 Comparer 分配与 `(int)` 截断（sp 参数忽略为既有语义，不动）；重复 No/csvNo 保留列表序第一个，与原线性扫描语义一致。`CharacterData` 池化评估结论：不做（生命周期随增删/Dispose 变化，清零成本≈新建，收益未取证）。收益验证按用户要求跳过。单测 10 新增（CsvLookupTests）+ xUnit 458 全绿；run_all.py 14/14 全 PASS；编译零新 warning；SAF IO 基线行号随行偏移更新（221→224、1699→1715） |
| 3.2 | 热路径减分配 | ✅ 完成 | `GetJoinedStr`（VariableEvaluator.cs:277）4 个慢分支（string 2D/3D、int 1D/2D/3D）合并为单循环 StringBuilder + 复用 long[] 索引缓冲（顺带消除每次迭代的 collection-expression 数组分配），1D string 的 string.Join 分支保留；`Html2PlainText`（HtmlManager.cs:654）改走 RegexFactory 静态缓存（Compiled，常量模式仅 1 实例）；`CheckEscape`（ExpressionMediator.cs:79）加无转义快速路径（null→""、无 `\` 直接返回原引用），有转义路径原样保留。S5 `FindChara` 的 FixedVariableTerm 分配评估结论：不做（无热度数据，Comparer 分配已由 3.1 消除）。对照 1.5 验证按用户要求跳过（1.5 未实施）。单测 21 新增（HotPathTests）+ xUnit 479 全绿；run_all.py 14/14 全 PASS；编译零新 warning |
| 3.3 | 增量快照 diff | ✅ 完成 | 引用缓存 `_lineCache` + 引用短路，协议零改动；单测 406 全绿 + 前端 383 全绿；压测 fill turn 2.8x、稳态 25-60%、snapshot 1.35x（无变慢）。详见 `2026.8.3.incremental-snapshot.md` |
| 3.4 | GC 配置 | ☐ | |

## 备注

- 相关既有文档：`docs/LESSONS/`（终端行为回归教训）、`docs/adr/0018-button-generation-invalidation-v7.md`（按钮失效机制）、`docs/adr/0019-android-saf-file-access.md`（SAF 迁移决策，S6/2.4 背景）。
- 全部改动保持 I-12 质量护栏：不引入新 warning；阶段 1 的诊断代码与引擎热路径隔离（条件编译或独立文件），不影响常规回合路径。
