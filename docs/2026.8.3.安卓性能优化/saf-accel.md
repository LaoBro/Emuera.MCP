# SAF 路径加速计划（Android 存档界面 5s+ → 目标 <200ms）

> 日期：2026.8.3
> 背景：Android（MAUI）游戏内点存档/取档进入存档位置选择界面时，文件列表读取 5s+；uemuera（本地文件系统）对照体感瞬时。
> 根因（已定位，详见 android-perf.md 嫌疑清单 S6）：SAF 每次 `ContentResolver.Query`/`OpenInputStream` 是跨进程 Binder IPC（单次 50~150ms 常见）；存档界面（游戏 ERB 自绘，headless 无系统对话框）典型 N 槽位 × 每槽 `FILEEXIST` 1~2 次 Query + 读存档头 2 次 IPC = **60~120 次 IPC ≈ 3~9s**。
> 约束：游戏文件（ERB/CSV）不可修改；ADR-0019 SAF 迁移不推翻（MANAGE_EXTERNAL_STORAGE 不可行，SAF 是唯一合规路径）；问题在「把 IPC 当 syscall 用」（N+1 + 无缓存），不在架构。
> 关联：`current_plan/2026.8.3.android-perf.md` 嫌疑清单 S6 / 阶段 2.4。

## 原则

1. **取证先于优化**：先量化「总 IPC 次数 × 单次耗时」，区分「单次慢（Provider 实现）」与「次数多（调用模式 N+1）」——两者优化手段不同。
2. **常态零成本**：缓存命中路径零 IPC、零分配；失效 O(1) 字典操作；命中路径不写日志。
3. **精确失效优先于 TTL**：引擎内所有文件写/删都收敛在 `SafGameDirAccessor.OpenWrite`（落盘）/`Delete` 两个入口（已验证：`DelData` → `SafCompat.Delete`、`SAVETEXT` → `SafCompat.WriteAllText` → `OpenWrite`），失效信号可从 C# 拿到；TTL 仅兜底进程外修改（文件管理器/USB）。
4. **每次改动独立可回滚**：A1/O1/O3/O4 各自独立提交、独立回归。

## 阶段 A：取证（先做，产出数据）

### A0 AgentLog 默认关闭 + UI 开关（A1 前置）

> 现状：`AgentLog`（Core/Agent/AgentLog.cs）默认开（`EMUERA_AGENT_LOG` 环境变量），Android 无法设环境变量 → MAUI 上"默认开且不可控"。性能最大化原则下应默认关闭、按需开启。

- [x] `AgentLog` 改可运行时切换：加静态 `Configure(bool enabled)`（启动早期调用决定初值）+ `Enabled` 由 `readonly` 改可变（UI 开关即时生效，无需重启）。
- [x] MAUI 启动最早处（`MainActivity.OnCreate` / `MauiProgram`，须在首次 `AgentLog.Instance` 访问之前）读 Preferences（`agent_log_enabled`，默认 false）→ `Configure`。
- [x] UI 开关：设置页（Vue → bridge → Preferences + 运行时切换 `AgentLog.Enabled`）；取证流程 = 开开关 → 复现 → 拉 `agent.log` → 关开关。
- [x] CLI / Server 不调 `Configure`，保留 `EMUERA_AGENT_LOG` 环境变量语义（默认开），调试与 CI 能力不降级。
- [x] 验证：Android 默认无 `agent.log`；开开关后 A1 取证日志正常落盘。（真机项待 A1 一并验证；PC 侧单测已锁定行为）

### A1 SAF 操作耗时日志

- [x] **落点**：`SafGameDirAccessor` 内全部 `ContentResolver.Query` / `OpenInputStream` / `OpenOutputStream` 包装点——`ResolveSubPath`、`OpenRead`、`GetMimeType`、`EnumerateUri`（递归每层各记一行）、`TryQueryDocument`、`FindChildDocument`、`DeferredSafWriteStream.Dispose`（跳过 dead code `TryOpenOutputStream`）。
- [x] **记录**：`[saf] <op> <path> <detail> ms=<N>[ FAIL <msg>]`；`op`=方法名（隐式调用方标识），`path`=docId 去 `primary:` 前缀（如 `emuera/TK/sav/global.sav`），`detail`=bytes= / children= / hits= / mime=；**先 `sw.Stop()` 再写日志**（日志微秒级开销不污染测量）；失败路径同样记录（FAIL + ms）。
- [x] **输出**：`AgentLog`（`AppDataPaths/agent.log`，app 私有目录，本地 `File.*` 写入，**不经过 SAF**——取证设施不依赖被排查的慢路径）。日志与缓存三层隔离（写入路径/失效信号/数据范围），互不影响。
- [x] **开关**：复用 A0——Android 默认关，取证时设置页「ファイルログ」开启（app 内查看器「最新を表示」读取，**无需 adb**）；CLI 保留 `EMUERA_AGENT_LOG` 环境变量语义。
- [x] **验收**：真机进存档界面 → 设置页「最新を表示」→ 得到「次数 × 耗时」分布；确认是「单次慢」还是「次数多」。（PC 侧：Android TFM 编译 0 错误 0 新增警告；run_all.py 14/14 无影响）

### 取证结果（2026.8.3 真机，agent.log 1.48MB / 10351 行，会话 19:50:34~19:51:47）

> 取回方式：设置页「エクスポート」（FileProvider 分享）→ 完整文件；分析脚本 `tests/analyze_saf_log.py`（聚合/时间窗口/N+1 检测）。

**总览：10343 次 IPC / 累计 227119ms / 平均 22ms → 判定：次数多主导（N+1），叠加局部单次慢**

| 操作 | 次数 | 总 ms | 平均 ms | 最大 ms | 说明 |
|------|-----:|------:|--------:|--------:|------|
| OpenRead | 2731 | 97195 | 35.6 | 116 | 读文件（启动加载为主） |
| EnumerateUri | 1411 | 83302 | 59.0 | **9772** | 枚举；单次最慢 9.7s（见下） |
| TryQueryDocument | 3020 | 19686 | 6.5 | 71 | 解析文件（每 OpenRead 前 2 次） |
| GetMimeType | 2731 | 15486 | 5.7 | 78 | 每 OpenRead 前 1 次 |
| ResolveSubPath | 217 | 6292 | 29.0 | 56 | |
| FindChildDocument | 231 | 5119 | 22.2 | 67 | 不存在槽位的目录枚举 |
| WriteDispose | 2 | 39 | 19.5 | 20 | 写档落盘（本次仅 2 次） |

**存档界面（19:51:36 起）＝纯 N+1**：每槽 **4 次 IPC**（`TryQueryDocument`×2 + `GetMimeType` + `OpenRead` 读档头；不存在的槽位 +`FindChildDocument` 目录枚举）。sav 目录 435 次 ≈ **2~3s**。

**启动加载（19:50 段）＝最大头**：8762 次 / 累计 165s。`OpenRead` 2722 次（读全部 ERB/CSV），每文件伴随 3 次 IPC（TryQueryDocument×2 + GetMimeType）→ 启动累计 ~8000 次 IPC。

**单次慢炸弹（Provider 冷启动/传输异常）**：`EnumerateUri ERB/children`（**仅 15 子项**）单次 **9772ms / 7948ms / 7652ms**；`口上/children`（126 子项）4294ms；`SKILLS/children`（170 子项）2746ms。O1 缓存只消重复（首枚举 8s 仍在）→ **O4 预取价值确认**。

**FAIL 231 条**：`TryQueryDocument <根目录> query-ex Unsupported Uri ...tree/primary:emuera/document/primary:emuera/...`——对根目录 URI 的 Query 报 Unsupported Uri（无害但频繁，可顺手减少根路径检查）。

**O1 收益预估（基于实测）**：
- 存档界面：每槽 4 次 → 1 次（只 OpenRead）→ 2~3s → **~1.2s**；
- 启动加载：8000 次 → ~1400 次（每目录 1 枚举 + 每文件 1 OpenRead）→ 累计 165s → **~60s**；
- 两者合计 227s 累计 → **~75s 累计**（首枚举单次 8s 由 O4 覆盖）。

### O1 落地后验证结果（2026.8.3 真机，agent2.log 484KB / 3249 行，会话 23:41:00~23:41:41）

> 与 A1 同机同游戏同流程（启动加载 + 进存档界面存/读档）。`tests/analyze_saf_log.py` 分析。

**总览：3245 次 IPC / 累计 80182ms / 平均 24.7ms —— 较基线 -69% 次数 / -65% 累计耗时**

| 操作 | 基线次数 | O1 后 | 变化 | 说明 |
|------|-----:|-----:|-----:|------|
| OpenRead | 2731 | 2731 | 0% | 读文件内容（O1 不缓存内容，不可避免；单次 35.6→24.1ms 为 Provider 系统缓存受益） |
| EnumerateUri | 1411 | 473 | **-66%** | 472 个唯一目录；仅根目录 2 次（写档失效后重枚举，非 N+1） |
| TryQueryDocument | 3020 | 31 | **-99%** | 只剩冷缓存前 4 槽 + 不存在槽位 FAIL |
| GetMimeType | 2731 | 8 | **-99.7%** | 同上 |
| ResolveSubPath | 217 | 0 | -100% | miss 查询合并为 EnumerateUri op |
| FindChildDocument | 231 | 0 | -100% | 同上 |
| WriteDispose | 2 | 2 | 0% | 写档落盘 |
| FAIL | 231 | 1 | **-99.6%** | 仅 save04.sav 不存在（无害） |

**存档界面（23:41:40，整段 29 次 IPC / 622ms）**：冷缓存首 4 槽（global/save00-03）仍 4 次 IPC/槽（一次性，sav 目录未缓存）；`EnumerateUri sav children=10`（23:41:40.743）建立缓存后，save1000~1004 **每槽仅 1 次 OpenRead**——O1 目标达成。不存在的槽位在缓存建立后 0 IPC（不再打 FAIL）。

**启动加载（23:41:00~30，3216 次 / 79.6s）**：枚举/解析类 IPC（EnumerateUri+TryQueryDocument+GetMimeType+ResolveSubPath+FindChildDocument）~8380 → **495 次**（472 目录枚举 + 23 次解析），正好符合「每目录 1 枚举 + 每文件 1 读」模型；OpenRead 2721 次读文件内容为下限，与基线 2722 一致。

**失效信号实证**：`WriteDispose time.log`（23:41:36.839）后游戏根目录 23:41:40.377 重新枚举（1 次 35ms）——写档失效生效；全日志仅此 1 处重复枚举，无 N+1 残留。

**观察项**：覆盖已存在文件也失效父目录（名/mime 不变本可免）→ 每次存档后父目录缓存清空，下次枚举重查（本次根目录重枚举 1 次佐证；sav 同理 ~33ms）。无害，O4 可优化为仅 createdDocument 分支失效。剩余卡顿源：OpenRead 读档头（600KB 档 40-66ms/槽）为真实 IO，O1 不缓存内容；目标 <200ms 尚未达成（本次 ~700ms 含冷缓存一次性开销），O4 预取（进存档界面预热 sav 缓存）可消冷缓存首 4 槽。

### O1 修复回归 + sav 重建验证（2026.8.4 真机，agent3.log，会话 00:46:00~00:47:31）

> 场景：文件管理器删除 sav 文件夹（app 已退出）→ 重启 → 继续游戏（无存档）→ 重新开始 → 新游戏自动存档 → 取档加载。覆盖 code-review 修复的 `EnsureRealDirectoryUri` 失效 key bug（P0）与写档失效路径。

**总览：3238 次 IPC / 66952ms / 平均 20.7ms**（基线 10343/227119ms，-69%/-70%）；FAIL 4 条全为「不存在路径」无害查询（sav 删除初期 / global.sav 未建 / save9999 槽位），无真实错误。

**sav 重建（修复点验证）**：`TryQueryDocument sav FAIL`（00:46:05，已删除）→ `EnumerateUri sav children=0`（00:46:36，游戏经 `EnsureRealDirectoryUri` 重建成功且立即可枚举）——失效 key 修正后新建目录立即可见。

**写档后立即可见（写后一致性）**：`WriteDispose save1000.sav`（00:47:10.959）→ **+31ms** `EnumerateUri sav children=1`；`WriteDispose global.sav`（00:47:11.027）→ `EnumerateUri sav children=2`（00:47:11.529）。每次落盘失效 → 重枚举即见新档。

**取档/加载（00:47:20 起）＝5 次 IPC / 132ms**：纯 OpenRead（save1000×3 + global×2），零伴随 TryQueryDocument/GetMimeType——缓存命中路径每文件 1 次读取。

**EnumerateUri 重复**：仅 sav ×3 与游戏根 ×3（各 = 1 次首枚举 + 2 次写档失效重枚举，符合设计）；其余 470+ 目录全部 1 次，无 N+1 残留。

### O4 真机验证结果（2026.8.4 agent4.log，会话 01:54:24~01:55:23）

> 场景：进游戏 → 加载完成**等一会**（后台预取窗口）→ 点继续游戏进存档界面 → 加载自动存档 → 存档到另一槽位 → 再进存档界面确认。

**总览：3230 次 IPC / 70640ms / 平均 21.9ms**；FAIL 1 条（save9999.sav 不存在，无害）。

**预取实证**：`EnumerateUri sav children=2`（01:54:48.462，加载完成后交互前——后台预取回填；游戏根已在 Preload 时缓存，预取命中跳过无日志）。

**进存档界面（01:55:05，用户"一下子就进入"）= 2 次 IPC / 58ms 纯 OpenRead**：global.sav（32ms）+ save1000.sav（26ms），**0 次 EnumerateUri / TryQueryDocument / GetMimeType**——sav 缓存预取命中，对比 agent2 同场景 29 次 IPC / 622ms（含冷缓存 2 次枚举）。

**写档立即可见**：存档到新槽 save00（`WriteDispose` 01:55:21.954，新建文档）→ **+476ms** `EnumerateUri sav children=3`（createdDocument 失效 → 重枚举见新档）✓

**再进存档界面（01:55:23）＝3 次 OpenRead / 72ms**：global + save00 + save1000 各 1 次，**仍 0 次枚举**——写档后 sav 缓存保留（覆盖写不失效微优化生效，新建档仅 createdDocument 失效）。

**结论**：O4 达成——交互路径（进存档界面）枚举 IPC 从 agent2 的 2 次（冷缓存）降到 **0 次**，只剩每档 1 次真实 OpenRead；写档后立即再进仍 0 次枚举。saf-accel 计划全部阶段（A0/A1/O1/O2/O3/O4）落地完成。

## 阶段 B：优化（按取证结果落地）

### O1 目录级子项缓存（核心）

- **位置**：`SafGameDirAccessor` 内部使用；缓存逻辑提取为 **Core 纯 C# 类**（无 Android 依赖，`Emuera.Headless.Tests` 可直接单测）。
- **存储**：目录 docId → `List<(docId, name, mime)>` 子项列表。**一份枚举喂三种查询**（`GetFiles`/`GetDirectories`/`FileExists`）。
- **key**：目录 docId（不含 pattern——pattern 是过滤视图，不参与 key，pattern 变化不失效缓存）。
- **命中路径**：
  - `GetFiles`/`GetDirectories`：子项列表 + pattern 过滤（O3 快速匹配）→ 零 IPC；
  - `FileExists`：解析父目录 docId + 文件名（`TryGetParentAndName` 已有）→ 父目录缓存命中则查名字 → 零 IPC；未命中才走原 Query 路径。
- **失效（精确信号，按目录粒度）**：
  - `OpenWrite` 落盘成功（`DeferredSafWriteStream.Dispose` 后，非 `OpenWrite` 返回时——延迟写此刻未落盘）→ 失效该文件所在目录；
  - `Delete` 成功后 → 失效所在目录；
  - `CreateDirectory` 成功后 → 失效父目录。
- **兜底**：可选长 TTL（如 60s）防进程外修改；可先不设，观察取证数据再定。
- **线程安全**：`lock` 保护字典（游戏线程为主，HTTP/预取线程低频访问）。
- **预期（取证校准）**：存档界面每槽 4 次 → **1 次**（只 OpenRead，2~3s → ~1.2s）；启动加载 8000 次 → ~1400 次（165s → ~60s 累计）；命中路径零 IPC。

### O2 消除 N×FILEEXIST（由 O1 吸收，不单独实现）

- N×`FILEEXIST` 变为 N×缓存命中（O(1)）。
- 验证指标 = O1 预期：agent.log 前后对照 IPC 次数 60~120 → ≤1。

### O3 通配匹配零正则化（低优先级，随 O1 同文件落地）

- **现状**：`MatchWildcard` 每次 `new Regex`（构造+编译），`EnumerateUri` 对每个子项调用。
- **方案**：`*`/`?` 两级通配改手写 O(n) 匹配（零分配零编译），或静态 `Dictionary<pattern, Regex>` 缓存。
- **叠加**：O1 命中路径的过滤每次调用 → 手写匹配直接受益（重建枚举时目录越大收益越明显）。

### O4 后台预取（O1 落地后实施，取证已确认价值）

- **目的**：消除 O1 后唯一剩余的「首枚举」单次 IPC——**取证实测：`EnumerateUri ERB/children`（15 子项）单次 8~9.7s**，差 Provider 下首枚举远超可感知阈值 → **价值确认，O1 落地后即实施（非可选）**。
- **方案**：游戏加载完成 / 进入 WaitInput 空闲时，后台线程预热 sav + 常用 ERB 目录子项缓存（零接口改动，`lock` 已有）。
- **触发**：sav 目录路径从引擎 `Config.SavDir` 取；ERB 常用目录可枚举 `Config` 已知目录。
- **预期**：进存档界面 / 游戏内触发目录枚举时首查命中缓存，8s 级单次枚举不再出现在交互路径。

## 明确不做：完整异步化

- **理由**：`IGameDirAccessor` 是同步接口，ERB 是同步调用模型（脚本无 await），完整异步化要改接口 + 全部调用点 + 线程模型，改动面大；O1+O4 已把关键路径从 N×IPC 降到 ≤1 次 IPC，异步化边际收益小。
- **记录为观察项**：若 O1+O4 落地后仍有可感知卡顿（如 Provider 单次 Query 常驻 >200ms），再评估接口异步化或 UI 层先行渲染。

## 验收

| 项 | 标准 |
|----|------|
| 取证数据 | agent.log 前后对照（基线已取：10343 次 / 227s 累计 → O1 后存档界面每槽 4 次 → 1 次，启动 8000 → ~1400 次） |
| 性能 | 同一存档界面操作 5s+ → **目标 <200ms**（含差 Provider 首枚举） |
| 单测 | 缓存类（Core 纯逻辑）：命中 / 写删失效 / TTL 兜底 / pattern 过滤 / 目录粒度 / 多目录隔离 |
| 回归 | xUnit 全绿 + `run_all.py` 14/14（Windows 走 `FileSystemGameDirAccessor`，不受影响） |

## 进度

| # | 条目 | 状态 | 结果摘要 |
|---|------|------|----------|
| A0 | AgentLog 默认关闭 + UI 开关 | ✅ 完成 | `AgentLog.Configure(bool)` + `Enabled` 可变（Lazy 已初始化时立即应用）；`MauiProgram` 启动读 Preferences(`emuera.agentLogEnabled`, 默认 false)→Configure；设置页开关（Vue `setAgentLogEnabled` → bridge → Preferences + 运行时切换，config 消息回推同步权威状态）；CLI/Server 不调 Configure 保留环境变量语义。**真机取日志补充**：app 内查看器（getAgentLog 读取截断 200K 推 Vue）+ 导出按钮（exportAgentLog：FileProvider + ACTION_SEND 分享，绕开 WebView 剪贴板 200K 限制）。单测：`AgentLogTests`(时序无关) + 前端 9 用例；xUnit 427 + MAUI.Tests 15 + vitest 393 + vue-tsc 0 err + run_all 14/14 + Android 编译 0 错。真机落盘/查看/导出已验证 |
| A1 | SAF 耗时日志 | ✅ 完成（取证已产出） | `SafGameDirAccessor.cs` 单文件：7 包装点 + `LogSaf` 辅助（docId 去 `primary:` 前缀、先 Stop 再写、失败记 FAIL）；复用 A0 开关 + app 内查看器/导出（无 adb）。Android TFM 编译 0 错、run_all 14/14。**取证（见 A1 节）**：10343 次 / 227s 累计；判定 N+1 主导 + EnumerateUri 单次 9.7s 炸弹；存档界面每槽 4 次 IPC；启动加载 8000 次 IPC |
| O1 | 目录级子项缓存 | ✅ 完成（真机已验证） | 缓存类 `DirectoryChildCache`（Core 纯 C#，ConcurrentDictionary copy-on-write，无 TTL）+ `ChildEntry`；`SafGameDirAccessor` 全路径接入（EnumerateUri 重构缓存优先 + key 修正、FindChildDocument/ResolveSubPath/GetMimeType/ResolveExistingFileUri/DirectoryExists 缓存快速路径）；失效信号（OpenWrite 建档/Dispose 落盘/Delete/EnsureRealDirectoryUri×2/PickDirectoryAsync 全清）。**已核实关键点**：ResolveDocId(childrenUri) 带 `/children` 后缀，key 统一由父文档 URI 推导。单测 `DirectoryChildCacheTests` 9 用例 + `WildcardTests` 12 用例全绿；xUnit 448/448；Android TFM 编译 0 错 0 新警告；run_all 14/14。**真机（agent2.log）**：10343→3245 次 IPC（-69%）、227s→80s（-65%）、FAIL 231→1；存档界面缓存建立后每槽仅 1 次 OpenRead（整段 29 次/622ms）；启动枚举/解析类 8380→495；EnumerateUri 472 唯一目录仅根目录重复 1 次（写档失效实证），无 N+1 残留。**code-review 修复后回归（agent3.log，删 sav 重建场景）**：EnsureRealDirectoryUri 失效 key 修正验证（sav 重建立即可枚举）；写档后 +31ms 重枚举即见新档；取档加载 5 次 IPC/132ms 纯 OpenRead；FAIL 4 条全为不存在路径无害查询 |
| O2 | 消除 N×FILEEXIST | ✅ 由 O1 吸收 | N×FILEEXIST → N×缓存命中（ResolveExistingFileUri 父目录缓存查名，0 IPC）；验证指标见 O1 真机项 |
| O3 | 通配匹配零正则 | ✅ 随 O1 落地 | `Wildcard.cs`（Core 手写两指针带回溯，* / ?，IgnoreCase，零分配）替换原私有 MatchWildcard（每子项 new Regex）；`WildcardTests` 12 用例 |
| O4 | 后台预取 | ✅ 完成（真机已验证） | `SafGameDirAccessor.WarmDirectoryCache(path)`（internal，内部走 GetOrQueryChildren 回填，try/catch 静默）；`BridgeHost` runLoop lambda（游戏加载完成后）`#if ANDROID` 预取启动（同步取 GamePaths.Current.ExeDir → `_ = Task.Run(TryPrefetchSaveDirectories)`，sav 用 Config.ForceSavDir 零新增 using）；**写档失效微优化**：Dispose 成功路径仅在 `_createdDocument` 时失效父目录（覆盖写不再清缓存，保护预取成果；Delete/建目录失效保留）。Android TFM 编译 0 错 0 新警告；xUnit 448/448；run_all 14/14。**真机（agent4.log）**：预取实证（sav 在交互前回填）；进存档界面 0 次枚举 IPC、2 次 OpenRead/58ms（用户体感"一下子就进入"）；写档（新槽）后 +476ms 重枚举即见新档；再进存档界面仍 0 次枚举（覆盖写不失效微优化生效） |

## 备注

- 相关文档：`docs/adr/0019-android-saf-file-access.md`（SAF 迁移决策）、`current_plan/2026.8.3.android-perf.md`（S6/2.4）。
- I-12 质量护栏：不引入新 warning；取证日志与热路径隔离（命中路径零日志）；缓存类放 Core 且不依赖 Android SDK。
- 一致性与安全：缓存只存「枚举结果视图」，不缓存文件内容（读路径不动）；进程外修改盲区由 TTL 兜底（可选）。
