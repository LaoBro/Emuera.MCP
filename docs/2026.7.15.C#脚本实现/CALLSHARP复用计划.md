## IScriptHost 实现计划（第三次修订版）

> 基于 [IScriptHost 接口计划.md](IScriptHost%20接口计划.md) 的三次 grill 修订结果。
> - 第一次修订（2026-07-15）：原计划 → 4 接口 + CALLCS + Roslyn 编译
> - 第二次修订（2026-07-15）：发现已有 CALLSHARP + PluginManager 机制，改为先验证已有路径
> - 第三次修订（2026-07-15）：经逐分支 grill，敲定 Phase 0 落地细节（见下文「Phase 0 设计决议」）

---

### 目标

为 C# 脚本功能打基础。C# 脚本用于逐步替换老游戏的 ERB 代码，
通过 `CALLSHARP` 指令从 ERB 调度 C# 方法。

---

### 核心设计

#### 调用机制

- 复用已有的 `CALLSHARP` 指令 + `PluginManager` 机制
- ERB → C#：`CALLSHARP "MethodName", arg1, arg2`
- C# → ERB：`PluginManager.GetInstance().ExecuteLine("CALL @Label, args...")`
- 不需要新增 `CALLCS` 指令

#### C# 插件模型

- DLL 放入 `{ExeDir}/Plugins/*.dll`
- 每个 DLL 需包含 `class PluginManifest : PluginManifestAbstract`，在构造函数中注册 `IPluginMethod`
- 接口固定：`void Execute(PluginMethodParameter[] args)`
- 返回值手动写入：`PluginManager.GetInstance().SetIntVar("RESULT", value)`

#### 参数传递

- `PluginMethodParameter` 二态联合体：`{ isString, strValue, intValue }`
- 按值传递，但对 `VariableTerm` 参数有写回效果（`CALLSHARP_Instruction` 在调用后自动回写）

---

### Phase 0 设计决议（grill 共识）

以下 9 条为 Phase 0 的已敲定决策，逐条对应文档末尾「决策清单」：

1. **范围 = 最小 PoC**：只做死代码清理 + SamplePlugin + 端到端验证；**不预建** `IScriptHost` / `[ErbClass]` / 属性注册等架构接缝，避免 Phase N 形态未定下的返工。
2. **DLL 不进 git**：`SamplePlugin.dll` 由测试阶段 `dotnet build` 后随 `copy_test_game_with_erb` 夹具复制进**临时副本**的 `Plugins/`，仓库只跟踪 `.csproj` / `.cs`。
3. **SamplePlugin 内容**：仅演示三类——打印（`Print`）、变量写回（`SetIntVar`）、ERB 回调（`ExecuteLine`）；每个方法打印**独特标记串**（如 `[SAMPLE_PRINT_OK]`、`[SAMPLE_VAR_OK:42]`、`[SAMPLE_ERB_OK]`）以便断言。**不演示** `VariableTerm` 形参写回（该机制留待 Phase N「RESULT 自动写回」一并验证）。
4. **传输通道 = server + JSONL**：`--server` 启动 headless，Python 经 HTTP/jsonl 回合读输出断言；跨平台、无需 ConPTY，契合 CLAUDE.md「脚本/自动化走 --server」约定。
5. **触发隔离**：`CALLSHARP` 放在独立标签 `@TEST_CALLSHARP`；e2e 在 `copy_test_game_with_erb` 复制出的**副本**的 `@SYSTEM_TITLE` 顶部注入 `CALL TEST_CALLSHARP`，DLL 与 `pluginsAware.txt` 也只进副本。**提交的 `test_game/` 完全不动**，现有 CLI 套件零干扰。
6. **旧 `EmueraPluginExample` 整个目录删除**：已不在 sln、引用已删除的 `Emuera/Emuera.csproj` 而不可构建、且依赖 WinForms/`MessageBox`；SamplePlugin 作为其干净的 headless 继任者，删掉消除歧义。
7. **单例风险从 Phase 0 移除**：`LoadPlugins()` / `SetParent` 在 `Process.cs:562-563` 的游戏进程初始化时调用，而 `KestrelGameServer` 为单活跃会话（第二 `POST /sessions` 返回 409），任意时刻仅一个 `PluginManager` 实例在用，无并发冲突。该风险只在 Phase N 引入多会话时才成真，届时再议。
8. **项目接线**：`SamplePlugin/` 为独立目录、**不加入 `Emuera.sln`**，保持主解决方案图干净（避免插件反向依赖宿主 csproj 污染主构建）；由 e2e 按需显式 `dotnet build SamplePlugin/SamplePlugin.csproj`。
9. **测试登记**：新建 `tests/test_callsharp.py`，**自包含**（脚本内构建 SamplePlugin → 复制夹具 → 注入 CALL → 启动 server → 断言标记）；`dotnet` 不在 PATH 时整套 SKIP（与 xUnit 套件行为一致）。在 `run_all.py` 增加一行登记（带 `--binary` / `--game-dir`），不改主流程结构。

---

### 阶段划分

#### Phase 0：验证现有路径（~1.5d）

| # | 任务 | 文件 / 位置 |
|---|------|------|
| 1 | 删除 `CALLSHARP_Instruction.CreateArgument` 死代码（构造函数已设非 null `ArgBuilder`，`ArgumentParser.SetArgumentTo` 永远走 `ArgBuilder` 分支，该 override 不可达） | `Instraction.Child.cs:1223-1237` |
| 2 | 删除 `EmueraPluginExample/` 整个目录 | `EmueraPluginExample/` |
| 3 | 新建 `SamplePlugin/` 独立项目（`net10.0`，引用 `Emuera.Headless.csproj`，**不进 sln**，无 WinForms） | 新建 |
| 4 | 实现 SamplePlugin 方法：打印 / `SetIntVar` / `ExecuteLine` 回调，各打标记串；不含 `VariableTerm` 写回 | `SamplePlugin/Plugin.cs` |
| 5 | 构建 SamplePlugin，输出 DLL 供测试使用（**不提交**） | — |
| 6 | 新建 `tests/test_callsharp.py`：自包含构建 + `copy_test_game_with_erb` 副本注入 CALL + 副本 `Plugins/` 放 DLL 与 `pluginsAware.txt` + 启动 server(jsonl) + 断言标记 | `tests/test_callsharp.py` |
| 7 | 在 `run_all.py` 登记该套件（`dotnet` 缺失则 SKIP） | `tests/run_all.py` |

#### Phase N：后续（延后，按优先级排列）

按顺序——每个子项独立决策是否进入下一轮：

1. `[ErbClass]` / `[ErbMethod]` 属性标记注册（扩展 PluginManager 注册方式）
2. RESULT/RESULTS 自动写回（CALLSHARP_Instruction 调用后反射返回值；可一并验证 `VariableTerm` 形参写回）
3. IScriptHost 接口定义 + EmueraScriptHost 转发层
4. WaitInput 支持（给 PluginManager 或 IScriptHost 新增 API）
5. Roslyn 编译 `.cs` → 内存程序集（注册到 PluginManager 同一注册表）
6. 编译缓存、`AssemblyLoadContext` 隔离、超时机制

---

### 风险

| 风险 | 缓解 |
|------|------|
| CALLSHARP_Instruction 的 `CreateArgument` 死代码有其他调用路径 | 已确认：`ArgBuilder` 路径是唯一入口（`Instraction.Child.cs:1219` 设 `ArgBuilder`），`Instruction.CreateArgument` 不可达 |
| SamplePlugin 构建后需复制到 test_game | 由 `test_callsharp.py` 在 e2e 夹具副本内完成，不污染提交的 `test_game/` |
| `pluginsAware.txt` 安全检查被遗漏 | e2e 在副本内显式创建，仅当 DLL 存在时触发 |
| 现有 CLI 套件受 CALLSHARP 干扰 | CALLSHARP 仅注入 e2e 副本，提交版 `test_game/` 保持干净（见决议 5） |
| ~~PluginManager 单例在多会话 server 模式下状态冲突~~ | Phase 0 已是单会话验证，且当前架构结构性消除并发冲突；多会话留待 Phase N（见决议 7） |

---

### 决策清单

| # | 决策 | 结论 |
|---|------|------|
| 1 | C# 脚本执行模型 | 从 ERB 的 CALLSHARP 调用 |
| 2 | 调度方式 | 复用现有 CALLSHARP 指令，不新增 CALLCS |
| 3 | Phase 0 范围 | 最小 PoC：死代码清理 + SamplePlugin + 端到端验证；不预建接缝 |
| 4 | SamplePlugin DLL 交付 | 不进 git，由测试阶段 build 后随夹具复制进**副本** |
| 5 | SamplePlugin 内容 | 打印 / `SetIntVar` / `ExecuteLine` 回调 + 标记串；不含 `VariableTerm` 写回 |
| 6 | e2e 传输通道 | server + JSONL（跨平台、可进 `run_all.py`） |
| 7 | CALLSHARP 触发隔离 | `@TEST_CALLSHARP` 独立标签，仅注入 e2e 副本；提交版 `test_game/` 干净 |
| 8 | 旧 EmueraPluginExample | 整个目录删除 |
| 9 | PluginManager 单例风险 | 从 Phase 0 风险表移除，留待 Phase N 多会话 |
| 10 | SamplePlugin 项目位置 | 独立目录，引用 Emuera.Headless.csproj，**不进 sln** |
| 11 | e2e 测试文件 | 新建 `tests/test_callsharp.py`（自包含），`run_all.py` 加一行登记；`dotnet` 缺失则 SKIP |
| 12 | 后续优先级 | 属性注册 → 自动返回值 → IScriptHost → WaitInput → Roslyn（Phase 0 不预建） |
| 13 | C#→ERB 回调 | 使用现有 `PluginManager.ExecuteLine` |
| 14 | `[ErbClass]` / `[ErbMethod]` | 延后（Phase N）|
| 15 | IScriptHost 接口 | 延后（Phase N）|
| 16 | WaitInput 等 API | 延后（Phase N）|
| 17 | Roslyn 编译 | 延后（Phase N）|
