# T-022 阶段 4：Shared/ 历史代码非 nullable 警告清理（精简工作备忘）

**创建**：2026-07-04
**范围**：[`Emuera.Headless/Shared/**`](../../Emuera.Headless/Shared/) 子树
**前置**：T-022 阶段 3 已完成所有 CS86xx（nullable 引用类型）抑制规则撤销
**目标**：逐步撤销 `.editorconfig` 中 `[Emuera.Headless/Shared/**]` 段剩余的 CS/CA/SYSLIB 抑制规则，使历史代码尽量通过 `TreatWarningsAsErrors=true` 构建。无法机械清理的规则需明确保留理由。

## 1. 背景与约束

- **构建环境**：`<Nullable>enable</Nullable>` + `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>` + `EnableNETAnalyators=true` + `AnalysisMode=Minimum`。
- **策略**：历史代码（`Shared/`）原则不重构逻辑，只做"让编译器静默"的最小改动，保持运行时行为不变。与阶段 3 同构。
- **保留项**：`CA1416`（平台兼容性）是 Headless 必用 Windows API 的合理抑制，**不清理**，最终保留在 .editorconfig。

## 2. 现状数据（2026-07-04 实际构建所得）

临时将 `[Emuera.Headless/Shared/**]` 段所有抑制改为 `warning` 后构建（`-p:TreatWarningsAsErrors=false`），统计结果：

- **总计 292 条警告 / 22 个 ID / 38 个 (ID, 文件) 对**
- 22 个抑制 ID 中有 **6 个自然消亡**（构建无警告），可直接删除规则行
- 警告呈强文件-ID 聚集：头部 5 文件占总警告数的 65%

### 2.1 按 ID 聚合

| ID | 警告数 | 文件数 | 类别 | 备注 |
| :--- | :---: | :---: | :--- | :--- |
| CA1854 | 84 | 6 | 性能 | `ContainsKey + indexer` → `TryGetValue` |
| CA1822 | 66 | 3 | 性能 | 成员可标记 static（GraphicsImage.cs 占 52） |
| CA1834 | 44 | 7 | 性能 | `StringBuilder.Append("x")` → `Append('x')` |
| CA1069 | 26 | 1 | 可靠性 | 枚举值不应重复（VariableCode.cs） |
| CA1507 | 12 | 1 | 性能 | 字符串字面量 → `nameof(...)` |
| CA1862 | 8 | 2 | 性能 | `string.Equals` 加 `StringComparison` |
| CA2211 | 6 | 1 | 可靠性 | 非私有静态字段（EncodingHandler.cs） |
| CA1514 | 6 | 2 | 性能 | 去掉 `Substring` 冗余长度参数 |
| CA1853 | 6 | 1 | 性能 | 字典 lookup 模式优化（VariableData.cs） |
| CA1829 | 6 | 1 | 性能 | `.Count()` → `.Length`/`.Count` |
| CA2208 | 4 | 1 | 可靠性 | `ArgumentException` 参数名 |
| CA1846 | 4 | 2 | 性能 | `Substring` → `AsSpan` |
| CS0164 | 2 | 1 | 编译器 | 未引用标签（LogicalLineParser.cs:591） |
| CS0162 | 2 | 1 | 编译器 | 不可达代码（LogicalLineParser.cs:591，与 CS0164 同行） |
| CA1858 | 2 | 1 | 性能 | `IndexOf == 0` → `.StartsWith` |
| CA1845 | 2 | 1 | 性能 | `Substring` → `AsSpan` |
| CA1864 | 2 | 1 | 性能 | `ContainsKey + Add` → `TryAdd` |
| CA1875 | 2 | 1 | 性能 | `Regex.Matches().Count` → `Regex.Count` |
| CA1830 | 2 | 1 | 性能 | StringBuilder 强类型重载 |
| CA2249 | 2 | 1 | 性能 | `IndexOf >= 0` → `.Contains` |
| CA2263 | 2 | 1 | 用法 | 优先泛型方法重载 |
| SYSLIB0014 | 2 | 1 | 过时 API | WebClient → HttpClient（Instraction.Child.cs） |

### 2.2 自然消亡 ID（0 警告，可直接删除规则）

| ID | 注释 |
| :--- | :--- |
| CS0472 | 表达式恒为某值 |
| CS0649 | 字段从未赋值（Shared 子树） |
| CA1806 | 未使用方法返回值 |
| CA1816 | Dispose 未调用 GC.SuppressFinalize |
| CA1835 | `ReadAsync(Memory<byte>)` |
| CA1859 | 变量类型收窄 |
| CA2016 | CancellationToken 传递 |

### 2.3 头部 5 文件按 ID 分布

| 文件 | 总数 | 主 ID（条数） |
| :--- | :---: | :--- |
| GraphicsImage.cs | 52 | CA1822 (52) |
| Creator.Method.cs | 50 | CA1854 (34) + CA1862 (6) + CA1834 (2) + 其他 |
| HtmlManager.cs | 38 | CA1834 (30) + CA1514 (2) + CA1845/CA1846/CA1862 |
| VariableData.cs | 30 | CA1854 (24) + CA1853 (6) |
| VariableCode.cs | 26 | CA1069 (26) |

## 3. 清理路线图（按批次）

每批沿用阶段 3 SOP：**暴露 → 定位 → 修复 → 验收 → 删除规则 → 再次构建确认**。

### 第 1 批：零源码改动（直接删规则，6 个 ID）

直接从 `.editorconfig` 删除以下行，重建验证 0 警告：

```
dotnet_diagnostic.CS0472.severity = none
dotnet_diagnostic.CS0649.severity = none
dotnet_diagnostic.CA1806.severity = none
dotnet_diagnostic.CA1816.severity = none
dotnet_diagnostic.CA1835.severity = none
dotnet_diagnostic.CA1859.severity = none
dotnet_diagnostic.CA2016.severity = none
```

### 第 2 批：低难度机械批量（~38 条，11 个 ID）

模式化修复，风险低，可考虑 SubAgent 并行：

| ID | 数 | 修复模式 |
| :--- | :---: | :--- |
| CA1834 | 44 | `sb.Append("x")` → `sb.Append('x')`（单字符字符串转 char） |
| CA1829 | 6 | `.Count()` on array/List → `.Length`/`.Count` |
| CA2249 | 2 | `IndexOf(...) >= 0` → `.Contains(...)` |
| CA1858 | 2 | `IndexOf(...) == 0` → `.StartsWith(...)` |
| CA1864 | 2 | `ContainsKey + Add` → `TryAdd` |
| CA1514 | 6 | 去掉 `Substring` 冗余长度参数 |
| CA1845 | 2 | `Substring` → `AsSpan` |
| CA1846 | 4 | `Substring` → `AsSpan` |
| CA1862 | 8 | `string.Equals(..., StringComparer)` 加 `StringComparison` |
| CA1830 | 2 | StringBuilder 强类型重载 |
| CA1875 | 2 | `Regex.Matches(...).Count` → `Regex.Count(...)` |

### 第 3 批：CA1854（84 条，6 文件，需引入 out 变量）

`ContainsKey + indexer` → `TryGetValue` 模式，需注意：
- 引入 `out` 变量后续作用域
- `else` 分支的 fallback 逻辑保持不变
- 涉及文件：Creator.Method.cs(34)、VariableData.cs(24)、其他 4 文件各 ~6 条

### 第 4 批：CA1822（66 条，3 文件，加 static 修饰符）

最大单文件 GraphicsImage.cs(52) 需谨慎：
- 修改方法签名为 `static`，需验证所有调用点已通过类名访问
- 调用点可能存在于其他文件，必须全量构建验证
- 若某方法实际依赖实例状态（被分析器误报），用 `#pragma warning disable CA1822` 局部抑制

### 第 5 批：可靠性类（逐 ID 人工判断）

| ID | 数 | 风险点 |
| :--- | :---: | :--- |
| CA1069 | 26 | 枚举值重复，可能影响序列化/外部契约，需核对 VariableCode.cs 枚举语义 |
| CA2208 | 4 | `ArgumentException` 参数名核对 |
| CA2211 | 6 | EncodingHandler.cs 非私有静态字段改为 `readonly`/`internal`，需检查外部访问 |
| CS0162/CS0164 | 2+2 | LogicalLineParser.cs:591 同行触发，疑 `#if DEBUG` 死代码，判断保留/删除 |
| CA2263 | 2 | 优先泛型方法重载 |
| CA1507 | 12 | 字符串字面量 → `nameof(...)`，需确认字符串与符号名一致 |

### 第 6 批：SYSLIB0014（最后，WebClient → HttpClient 迁移）

- 唯一涉及运行时行为改动的批次
- 涉及文件：Instraction.Child.cs（2 处）
- 需要重写 HTTP 调用：`WebClient.DownloadString` → `HttpClient.GetStringAsync` 等
- 需要确认调用上下文是否同步/异步
- 建议放最后，单独 PR 评审

### 永久保留项

| ID | 理由 |
| :--- | :--- |
| CA1416 | Headless 必用 Windows 专用 API（Font/PrivateFontCollection/Console.BufferWidth），合理抑制 |

## 4. 标准操作流程（SOP）

每批按以下流程：

1. **暴露**：`.editorconfig` 中该 ID 的 `severity` 改为 `warning`。
2. **定位**：全量构建捕获日志（`-p:TreatWarningsAsErrors=false`），Grep `CSxxxx/CAxxxx` 按文件聚合统计。
3. **修复**：优先 `replace_all`；复杂上下文用精确 `Edit`（整行替换）；大批量（>200）用 SubAgent 并行。
4. **验收**：全量构建 `0 警告 0 错误`（恢复 `TreatWarningsAsErrors=true`）。
5. **删除规则**：从 `.editorconfig` 删除该 ID 行。
6. **再次构建确认**：0 警告 0 错误。

## 5. 踩坑记录（沿用阶段 3 + 阶段 4 新增）

- **日志捕获**：`dotnet build ... 2>&1 | Out-String`，避免重定向丢失输出。同一日志可能重复 2 次，实际唯一警告数为显示数的一半。
- **SubAgent 核实**：SubAgent 声称"已修复"不代表实际生效，需人工 Grep 复核。
- **CA1822 调用点**：标记 `static` 前必须全局 Grep 调用点，确认无 `instance.Method()` 调用。
- **CA1069 枚举值**：若枚举被序列化（JSON/二进制），改值会破坏兼容性，需检查是否落盘/读盘。
- **SYSLIB0014**：`WebClient` 是同步阻塞 API，迁移到 `HttpClient` 需注意线程模型，可能需引入 `async` 链路。
- **CA1854 + CS8600 陷阱**：`Dictionary.TryGetValue(key, out T)` 的 `out` 参数带 `[MaybeNullWhen(false)]` 特性，当目标变量是非 null 引用类型（如 `XmlDocument doc`）时，`out doc` 会触发 CS8600。修复模式：用临时变量 + null-forgiving，`if (dict.TryGetValue(key, out var temp)) doc = temp!;`。
- **CA1822 + CS0176 陷阱**：将实例方法/属性改为 static 后，调用方 `instance.StaticMember()` 会触发 CS0176 错误（非警告，构建失败）。修复模式：调用方改为类型名限定 `ClassName.StaticMember()`。C# 不允许实例引用访问 static 成员（与 C++/Java 不同）。HEADLESS 存根方法加 static 后，需全局搜索调用点改为 `GraphicsImage.Method()` 形式。
- **CA1069 + VariableCode 计数器语义**：`VariableCode.__COUNT_*` 系列是有意的计数器边界，每种变量类型从 0 开始独立计数，导致与 `__NULL__`（0x00）或其他 `__COUNT_*` 成员值重复。这些值被强制转换为 int 用作数组长度（如 `new long[(int)VariableCode.__COUNT_INTEGER_ARRAY__]`），改值会破坏数组初始化。存档以枚举名序列化，不依赖数值唯一性。修复方式：在枚举定义前后用 `#pragma warning disable/restore CA1069` 局部抑制，并加注释说明理由。
- **CA2211 + readonly**：`EncodingHandler` 的 `public static Encoding` 字段被 CA2211 标记为"非常量字段应当不可见"。这些字段初始化后不再修改，加 `readonly` 即可满足规则，同时保持 public 可见性。
- **CS0162/CS0164 死代码遗留**：`LogicalLineParser.cs` 中 `err:` 标签原本被 `goto err` 引用，goto 被注释后遗留了未引用标签（CS0164）和死代码 return（CS0162）。catch 块已处理异常路径，直接删除 `err:` 标签和后续 return 语句。

## 6. 修复原则

- **最小改动**：不重构历史逻辑，优先机械修复。
- **保留运行时行为**：`static` 化、`TryGetValue` 改写不得改变执行路径。
- **接口契约**：实现成员签名必须严格匹配接口定义。
- **永久保留项需注释**：`CA1416` 等保留抑制行需在 `.editorconfig` 注释中说明理由。

## 7. 验证标准

- `dotnet build Emuera.Headless/Emuera.Headless.csproj --no-incremental` 结果为 **0 警告 0 错误**。
- 对应 ID 已从 `.editorconfig` 删除（保留项除外）。
- `[Emuera.Headless/Shared/**]` 段最终仅保留 `CA1416`（及后续新增的合理保留项）。

## 8. 进度跟踪

| 批次 | ID | 标注 | 实际 | 状态 |
| :--- | :--- | :---: | :---: | :--- |
| 1 | CS0472/CS0649/CA1806/CA1816/CA1835/CA1859/CA2016 | 0 | 0 | ✅ 已完成 |
| 2 | CA1834/CA1829/CA2249/CA1858/CA1864/CA1514/CA1845/CA1846/CA1862/CA1830/CA1875 | 38 | 80 | ✅ 已完成 |
| 3 | CA1854 | 84 | 84 | ✅ 已完成 |
| 4 | CA1822 | 66 | 66 | ✅ 已完成 |
| 5 | CA1069/CA2208/CA2211/CS0162/CS0164/CA2263/CA1507 | 50 | 27 | ✅ 已完成 |
| 6 | SYSLIB0014 | 2 | - | ⬜ 待办 |
| - | CA1416（永久保留） | - | - | ⏸️ 保留 |
