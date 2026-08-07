# Android 性能优化 2：低端安卓极限优化计划

> 日期：2026.8.4
> 背景：第一轮优化（`docs/2026.8.3.安卓性能优化/android-perf.md`）已将整体性能提升至超过同类产品（uEmuera/Unity 系）。本轮目标：**将低端安卓设备（1-2GB RAM、弱 CPU、可能 32 位 ARM）上的体验压榨至极限**——冷启动、内存占用（防杀进程）、每回合帧延迟、大文本回合。
> 约束：**游戏文件（ERB/CSV）不可修改**，只允许提升 C# 引擎 / 前端 / 构建配置效率；保持 I-12 质量护栏（不引入新 warning）。
> 基线：xUnit 全绿（~479+ 用例）、`run_all.py` 14/14、前端 Vitest ~224 用例、`Emuera.Maui.Tests` 全绿。

---

## 0. 上一轮结论与本轮定位

| 上一轮结论 | 对本轮的含义 |
|---|---|
| Full AOT 真机无体感差异（`android-perf.md` 2.3 / S0） | 瓶颈**不在 JIT/解释外壳**，在指令语义与回合级外围（快照/序列化/分配）——引擎侧仍有可榨空间，但启动/内存需要 NativeAOT 级别的手段 |
| 增量快照 diff：fill turn 2.8x、稳态 25-60%（3.3） | 回合级快照已不是主要矛盾；本轮把注意力转到**启动、内存、分配与渲染残留** |
| 慢回合取证系统（1.1-1.6）当时**未实施** | 本轮阶段 0 以「归因取证」为唯一前置门槛，直接继承并裁剪该设计 |
| 渲染层已做虚拟滚动（~40 可见行 DOM） | "DOM 节点数"问题已解决；残留成本是每回合可见行的布局+paint，是否值得动渲染器**必须由数据决定**（见阶段 3 门控） |

**本轮优先级排序**（与第一轮不同：引擎侧 NativeAOT 升为第一主攻，渲染侧降级为门控候选）：

```
P0 归因取证（一切优化的前置）
  ↓
P1 引擎 NativeAOT（启动 271ms vs 1200ms、内存三连降）← 本轮主攻
  ↓
P2 引擎热路径 + GC/内存调优（低内存模式、回合内低延迟 GC）
  ↓
P3 前端数据层分配（opsApplier 深拷贝，归因显示 scripting 高才做）
  ↓
P4 渲染层（门控：只有归因证明渲染是主瓶颈才动；先 DOM 低风险，后手字形图集）
  ↓
P5 启动与包体（APK 体积、资源加载）
```

---

## 1. 探索结论（决策记录，避免重复论证）

> 以下路线已在本轮讨论中论证否决或推迟，结论先落地，后续不再重复调研。如需重开论证，先读此节。

### 1.1 WASM 路线：否决 ❌

**提案**：把 C# 后端编译成 WebAssembly 以获得"最高性能"。

**否决原因**（结构性，非实现问题）：

1. **双重编译层**：IL → LLVM → WASM → V8 运行时再编译（Liftoff/TurboFan 分层），低端 CPU 上编译延迟与分层损失吃掉收益。
2. **GC 是短板**：官方 dotnet/wasm（Mono 系）GC 弱于原生；runtimelab NativeAOT-LLVM 仍是实验分支，GC 用保守模式，核心维护者 2025-07 在 issue #3140 自述 "largely untested conservative mode"，不可生产。
3. **内存预算雪上加霜**：WASM 32 位线性内存 + .NET-wasm 运行时 30-60MB 起步，与"低端机防杀进程"目标直接冲突。
4. **互操作边界**：引擎进 wasm 后渲染仍在 JS/DOM，回合数据要过 wasm↔JS 边界；渲染瓶颈一个字节都没改善。

**结论**：WASM 是"浏览器免安装分发（触达问题）"的方案，不是"性能问题"的方案。若将来做 Web 版，走官方 dotnet/wasm + `WasmEnableAOT`，**不碰 NativeAOT-LLVM**。

### 1.2 Canvas 渲染：行缓存否决，字形图集为后手

- **Canvas 行缓存（按行存 offscreen 位图）**：否决 ❌——缓存维度是"行数"，5 万行 scrollback 内存失控；比视口宽的字符画长行（`TerminalDisplay.vue` 的 `overflow-x: auto` 兜底场景）单行位图宽两三倍屏。
- **字形图集 glyph atlas（按唯一字形×颜色烘焙位图）**：可行但**推迟**——缓存维度是"字形数"（与行数无关），是 Canvas 系正确姿势（xterm.js canvas renderer 同思路）；但代价高（自建字形烘焙、网格对齐、丢原生选区/无障碍），仅当归因证明渲染是主瓶颈才评估。

### 1.3 渲染迁移整体推迟（门控）

虚拟滚动已解决节点数；Canvas 迁移收益未取证、成本高。渲染层改动**必须在阶段 0 归因数据证明渲染占帧预算主导之后**才立项（见阶段 3）。

### 1.4 纯 MAUI（移除 Vue/WebView）渲染：否决为当前手段，列为最后后手 ❌

> 2026.8.7 评估（用户提问"省掉 JS 运行时能否提性能"）。已核实：`IJsBridge` 只抽象消息通道（`PostTurn`/`PostMessage`/`InputReceived`），渲染器本体在 JS/DOM，无 C# 渲染实现——迁移 = 全量重写前端（`TerminalDisplay.vue` 574 行 + hitTest/opsApplier/snapshotReducer/InputBar/TinputCountdown + 图片通道 + 224 Vitest 用例）。

**能省（真收益）**：
- V8 堆 + Blink 渲染进程内存（低端机 WebView 进程 50-100MB+）→ 对齐"防杀进程"目标
- WebView 初始化（冷启动 100-300ms 量级）

**省不掉（换汤不换药）**：
- 渲染几何：DOM layout/paint 换成 Android measure/draw，同价
- 数据层分配：`opsApplier` 的深拷贝/逐行重建随迁 C#，分配一个字节不少（P3 的 4.2 优化目标原样存在）
- 快照 diff 协议消费（wire 不变）

**新增成本**：
- 自绘管线：纯 MAUI 在 5000 行 scrollback 下不能用控件树（Label 树会炸），必须 GraphicsView/SkiaSharp 单画布 + 字形烘焙/网格对齐（= 5.3 字形图集的 C# 版，成本只高不低）
- 丢原生选区/无障碍/文本搜索（5.3 代价清单同款）
- 双平台（Windows WebView2 + Android WebKit）渲染器重写与维护

**否决理由**：第一轮 S0 已证明瓶颈在**引擎指令语义 + 回合级快照/序列化/分配**（C# 侧），JS 运行时不在主矛盾；省 JS 外壳 ≠ 省渲染几何与数据层分配。**结论：无阶段 0 归因数据不立项**；若归因点名"内存/冷启动"主矛盾，优先引擎 NativeAOT（已在 3.2 主攻）与 WebView 进程/初始化优化，纯 MAUI 列为全部手段之后的最后后手。

---

## 2. 阶段 0：归因取证（所有优化的前置门槛）

> 目标：确定低端机上"每回合耗时"的构成——引擎执行 / 前端数据层（scripting）/ 渲染（layout+paint）/ GC。**任何阶段 1-5 的优化立项前必须先过此关**，避免凭感觉改架构。
> 继承第一轮未实施的 1.1-1.6 取证设计，按本轮归因目标裁剪。

### 2.1 可复现回合回放 harness（桌面先行）

- [ ] `tests/` 新增驱动脚本：以 `test_game` 为夹具，脚本化输入自动跑 N 个代表性回合（含：常规 PRINT 回合、大文本 PRINT 回合、整屏刷新、存读档、TINPUT 超时），输出可复现的回合序列。
- [ ] Windows CLI 模式跑同一序列 + `dotnet-trace collect`，拿到**方法级火焰图**（把 Android 难剖析的 C# 热点搬到 PC 复现；与第一轮 1.6 同思路）。
- [ ] 验收：PC 上能稳定复现"最慢的几个回合"并给出 top 热点方法清单。

### 2.2 前端三段计时（script / layout / paint）

- [ ] Chrome DevTools Performance 录制回放页面，**CPU 限频 6x** 模拟低端；对每个回合取 scripting / layout / paint 三段耗时。
- [ ] MAUI 真机：`setWebContentsDebuggingEnabled(true)` 后 `chrome://inspect` 远程连 WebView，同样录制（真机数据为准）。
- [ ] 产出：每个代表回合的三段耗时表 + 结论（瓶颈落在引擎 / 数据层 / 渲染 / GC 哪一段）。

### 2.3 前端分配统计

- [ ] Performance 录制中读取 GC 采样，统计 `opsApplier.applyDiff/applyOps` 的深拷贝分配占比（`{...s}` per segment、逐行重建 entries，`Emuera.Web/src/lib/opsApplier.ts`）。
- [ ] 产出：单回合前端分配量级（区分"数据层"与"渲染层"）。

### 2.4 引擎侧回合耗时基线

- [ ] 引擎侧已有慢回合扳机设计（第一轮 1.1-1.5）未实施；本轮至少先落一个**轻量 Stopwatch 基线**：`AgentJsonlProtocol.StepAsync` 的 `DispatchInput → BuildTurn` 耗时，日志级别记录（仅统计不打每回合日志）。
- [ ] 产出：典型回合引擎耗时分布（P50/P95/max）。

**阶段 0 验收**：产出《归因报告》：一段/一层式结论 + 数据表格，明确回答"低端机每回合的耗时大头是什么"。此报告决定阶段 1-5 的优先序（尤其是阶段 3 是否立项）。

---

## 3. 阶段 1：引擎 NativeAOT（本轮主攻）

> 目标：把 `Emuera.Headless.Core` 从 Mono 运行时中解放出来。官方数据（.NET 10 RC2，.NET Conf China 2025）：Android 启动 ~271ms vs Mono AOT ~1200ms；内存（无 Mono 运行时/无反射元数据表/无 JIT 堆）显著下降——直接命中低端机两个最痛点：冷启动与防杀进程。

### 3.1 可行性事实（已核实，写死避免重查）

- [x] **反射障碍不存在**：`PluginManager.LoadPlugins()` 在 Android/SAF 路径直接抛异常禁用（`Emuera.Headless.Core/Shared/Runtime/Utils/PluginSystem/PluginManager.cs:272-280`），`Assembly.LoadFrom`/`Activator.CreateInstance`（L292-305）在 Android 不走。
- [x] 引擎核心无 AspNetCore 依赖（MAUI 只引 `Emuera.Headless.Core`，不引 Headless.Server/Cli）。
- [x] `InternalsVisibleTo` 为编译期机制，不受 AOT 影响。
- [ ] **待核实**：全仓库扫描 `Expression.Compile`（AOT 下退化为解释模式、变慢）与 `Type.GetType(字符串)` 隐式反射。

### 3.2 引擎先行实验（低风险，先做）

- [ ] 新建 NativeAOT 验证分支（不并入主分支）：给 `Emuera.Headless.Cli` 或独立最小入口加 `PublishAot=true`，**先 `-r win-x64`**（零 Android 工具链依赖，快速闭环），跑通后出 `android-arm64`。
- [ ] 记录并清零 ILC 警告：IL3050（动态代码）、IL3053、IL2026（反射）、IL2067 等；**原则：清零或逐条注明豁免理由**，不许静默 suppress。
- [ ] 行为等价回归：304+ xUnit 用例（在 net10.0 桌面跑）+ `run_all.py` 14/14 + 前端 224 用例——**NativeAOT 产物跑同一回归矩阵，输出与 Mono AOT 逐字节一致**。
- [ ] 记录：编译耗时（CI 预算参考）、单文件体积、泛型结构体膨胀对体积的影响（`GlobalInt1dWrapper` 等值类型包装器是典型膨胀源）。
- [ ] **验收**：ILC 警告清零 + 回归矩阵全绿 + 体积记录在案。通过后进入 3.3；不通过则回退并记录阻塞点。

### 3.3 迁移清单（硬性改造，仅一处）

- [ ] **System.Text.Json 反射 → 源生成器**（唯一硬性改造，一两天量级）：
  - [ ] 新增 `JsonSerializerContext`（源生成）声明，覆盖：`TurnRecord`、`DisplayState` 快照类型、`JsonlCommand`、`JSONConfigData`。
  - [ ] 改造 8 处调用点：`AgentJsonlProtocol.cs:76/92/203/266`、`DisplayState.cs:443`（`GetSnapshotJson`）、`JSONConfig.cs:28/36/46`。
  - [ ] **重点**：`TurnRecord.cs:117/133` 两个 `JsonSerializer.Serialize(writer, (object)value, options)` 装箱 converter——AOT 下必炸，改为 `[JsonConverter]` 特性 + 源生成上下文。
  - [ ] 验收：单测（含序列化 roundtrip）+ 前端协议兼容性回归（wire 格式不得变化）。
- [ ] **`System.Data.DataTable` 子集验证**（`PluginManager.GetDataTable` / `GetIntVar` 等 API 用到）：AOT 下 `DataColumn.Expression` 等反射路径受限——用现有测试覆盖实际用到的子集（建表/读值/改值），记录结论。
- [ ] **`Emuera.Maui.csproj:36` 关联点**：`AndroidEnableMarshalMethods=false`（XAGNM7009 workaround）与 NativeAOT 的 JNI 通道（Marshal Methods）可能冲突——进入 3.4 前必须重新评估此开关。
- [ ] 验收：引擎 NativeAOT 产物在 CLI 模式下跑通 test_game 全流程 + 回归全绿。

### 3.4 MAUI 壳切换（推迟，独立门控）

> 微软文档 Android NativeAOT 仍标 experimental（no built-in Java interop）；壳层重度依赖 JNI：SAF `ContentResolver.OpenInputStream`（`SafGameDirAccessor`）、WebView（MAUI handler 过 JNI）。
> **2026.8.7 .NET 11 评估完成（`android-perf-2-net11-eval.md`）**：.NET 11 Preview 4-6 将 CoreCLR 定为 Android 默认运行时、Android interop trimmable type map 默认开启（Java interop 首次实质松动），但：① NativeAOT 仍 experimental；② 最低 API 21→24 与低端机覆盖冲突；③ Preview 6 起 Mono 回退属性移除（升 .NET 11 = 单程票）。**结论：壳层不即时迁移，门控推迟到 .NET 11 GA（2026-11 前后）后开独立实验分支。**

- [ ] **门控条件（已更新）**：引擎先行验证（3.2/3.3，.NET 10）通过 **且** .NET 11 GA 后壳层实验分支通过（CoreCLR 真机 A/B：冷/二次启动、包体、内存 P50/P95 vs Mono Full AOT 基线；NativeAOT 冒烟见下）。
- [ ] 冒烟测试清单（切换前）：SAF 选目录/读游戏文件/存读档全流程、WebView 渲染 + 手势、`TinputCountdown`/输入框、捏合缩放。
- [ ] 切换后回归：`Emuera.Maui.Tests` + 真机全流程 + 启动耗时 A/B（271ms vs 1200ms 目标验证——注意该数字为 NativeAOT 演示值，CoreCLR 官方口径仅 Mono ±10%）。
- [ ] 期间保持 Mono Full AOT 作为壳层基线（现状不动；**仅 .NET 10 内成立**——.NET 11 Preview 6 已移除 Mono 回退）。
- [ ] 附加（.NET 11 红利）：真机 dotnet-trace/dotnet-counters 归因取证（阶段 0 工具链统一）；`AndroidEnableMarshalMethods=false`（XAGNM7009）在 CoreCLR/NativeAOT 下重评。

**阶段 1 验收**：引擎侧 NativeAOT 验证分支达成"ILC 清零 + 全矩阵等价"；壳层切换有明确门控结论（做 / 等 / 不做）与冒烟结果。

---

## 4. 阶段 2：引擎热路径与 GC/内存调优

> 与第一轮衔接：第一轮已完成 CSV 索引化（3.1）、热路径减分配（3.2）；GC 配置（3.4）未做，本轮补齐并扩展。

### 4.1 GC 配置（低内存设备）

- [ ] 回合执行期间临时 `GCSettings.LatencyMode = SustainedLowLatency`，回合结束恢复（第一轮 3.4 未做项，本轮补）。
- [ ] Android 侧确认 GC 模式为 Workstation（默认），`ServerGarbageCollection` 不开。
- [ ] 观察 LOH：ERB CSV 数据表是大数组大户（角色/物品表），确认无 LOH 碎片化问题；必要时 `GCSettings.LargeObjectHeapCompactionMode` 回合间隙触发。
- [ ] 验收：真机长会话（连续 100+ 回合）GC 暂停毛刺（P95）对比基线。

### 4.2 前端数据层分配（归因显示 scripting 高才做）

- [ ] `opsApplier.ts` 深拷贝优化：`applyDiff` 逐行 `{...s}` 深拷贝 segments/button（`opsApplier.ts:117-127`）——评估改为**结构共享**（不可变行引用复用，变更才拷贝）或按需拷贝。
- [ ] 验收：阶段 0 的 2.3 分配统计前后对比（目标 -50%+），前端 224 用例全绿。

### 4.3 剩余热路径（数据驱动，只做归因点名的）

- [ ] 以阶段 0 火焰图为输入，对本轮新出现的 top 热点逐项立项（每项 = 一次独立实验，可回滚）。
- [ ] 验收：同一回放序列的引擎耗时 P95 对比。

---

## 5. 阶段 3：渲染层（门控，默认不做）

> 只有阶段 0 归因证明"渲染占帧预算主导"才立项。默认路线：**DOM 体系内低风险优化**，Canvas/字形图集仅作后手。

### 5.1 门控条件（不满足则整节关闭）

- [ ] 2.2 三段计时显示 layout+paint 合计 > 帧预算 50%（低端机限频下），且 2.1/2.4 证明引擎与数据层已无更大空间。
- [ ] 满足才继续 5.2，否则**整节关闭**，资源转投阶段 1/2。

### 5.2 DOM 低风险优化（保留选区/无障碍）

- [ ] **segment 合并**：前端 `opsApplier` 追加相邻同色同字体 segment 的 coalesce pass（零协议改动，`DisplayDiff`/快照格式不变；字符画行节点数预期 3-10x 下降）。
- [ ] **布局隔离**：`.term-line` 加 `contain: layout style paint`（`TerminalDisplay.vue`），行 40 变更不再触发行 1-39 布局失效。
- [ ] 验收：同回放序列 layout+paint 耗时对比 + 前端全绿 + 桌面/真机像素一致性截图对比。

### 5.3 字形图集（后手，仅当 5.2 后仍不达标）

- [ ] 评估立项条件：5.2 完成且渲染仍占主导。实现要点（结论已定）：按"唯一字形×颜色"烘焙 offscreen 位图（缓存维度=字形数，与行数无关），主 canvas 按格 blit；按钮保留 DOM 覆盖层（`ButtonRef.col/width` 定位）；`useVirtualScroll` 语义原样保留。
- [ ] 代价清单确认：自建字形烘焙、CJK/fallback 网格对齐（`EmueraBlock` 补字形）、丢原生选区/无障碍（aria 兜底）、DPR clamp（低端机 ≤2）。
- [ ] 验收：滚动帧率 + 每回合 blit 耗时 + 与 DOM 渲染像素一致性。

---

## 6. 阶段 4：启动与包体

- [ ] APK `libs` 体积监控（NativeAOT 泛型结构体膨胀、AOT 图像大小），超标时评估：泛型代码折叠、裁剪配置、按 ABI 分包（低端 32 位 ARM 保留 arm 包）。
- [ ] Vue 产物瘦身：`Emuera.Web` dist 检查（Tree-shaking/字体子集化——`EmueraMonoJP` 等内置字体是体积大户候选）。
- [ ] 首启流程：`GameResourceExtractor.EnsureGameDir()` 解压耗时（内置 test_game 打包）真机计时，必要时改按需解压。
- [ ] 验收：APK 体积 + 冷启动（首启/二次启动）真机 A/B。

---

## 7. 风险登记

| 风险 | 影响 | 缓解 |
|---|---|---|
| MAUI Android NativeAOT 的 JNI 实验性（SAF/WebView 全过 JNI） | 壳层切换受阻 | 引擎先行、壳后置；跟进 dotnet/android 发布说明；冒烟测试前置 |
| `AndroidEnableMarshalMethods=false` 与 NativeAOT 冲突（XAGNM7009 workaround） | 壳层切换时构建/运行异常 | 3.3 里显式立项重新评估该开关 |
| System.Text.Json 装箱 converter（TurnRecord.cs:117/133）在 AOT 下崩溃 | 协议序列化挂 | 3.3 已列为重点，先行改造 + roundtrip 单测 |
| `System.Data.DataTable` AOT 反射受限 | 插件 API 子集失效 | 3.3 子集验证，结论记录 |
| NativeAOT 构建时间显著变长 | CI 预算 | 3.2 记录编译耗时；验证分支独立，不阻塞主分支 |
| NativeAOT 产物托管调试器失效 | 调试成本 | 异常栈翻译流程；桌面 CLI 阶段先建立调试基线 |
| 渲染层"默认不做"的返工诱惑 | 资源错投 | 阶段 3 门控硬性条件，数据不达标即关闭 |

---

## 8. 进度表

| # | 条目 | 状态 | 结果摘要 |
|---|------|------|----------|
| 1.1-1.5 | 探索结论（WASM/Canvas/渲染推迟/纯 MAUI 否决） | ✅ 已定 | 见第 1 节决策记录；1.4 纯 MAUI 列为最后后手 |
| 2.1 | 回放 harness | ☐ | |
| 2.2 | 前端三段计时 | ☐ | |
| 2.3 | 前端分配统计 | ☐ | |
| 2.4 | 引擎回合耗时基线 | ☐ | |
| 3.1 | 可行性事实核实 | ✅ 部分 | 反射/插件/AspNetCore 已核实；`Expression.Compile`/`Type.GetType` 扫描待做 |
| 3.2 | NativeAOT 引擎先行（win-x64 → android-arm64） | ☐ | |
| 3.3 | JSON 源生成器迁移 + DataTable 验证 + MarshalMethods 评估 | ☐ | |
| 3.4 | MAUI 壳切换（门控） | ☐ | .NET 11 评估完成（2026.8.7）：等 GA 后实验分支，见 `android-perf-2-net11-eval.md` |
| 4.1 | GC 配置（SustainedLowLatency / LOH） | ☐ | |
| 4.2 | 前端数据层分配 | ☐ | |
| 4.3 | 剩余热路径（数据驱动） | ☐ | |
| 5.1-5.3 | 渲染层（门控） | ☐ | 默认关闭 |
| 6 | 启动与包体 | ☐ | |

---

## 9. 参考

- 上一轮：`docs/2026.8.3.安卓性能优化/android-perf.md`（结论与嫌疑清单）、`current_plan/2026.8.3.incremental-snapshot.md`、`current_plan/2026.8.3.saf-accel.md`
- 架构：`docs/adr/0018-button-generation-invalidation-v7.md`、`docs/adr/0019-android-saf-file-access.md`
- 前端渲染现状：`Emuera.Web/src/components/TerminalDisplay.vue`、`Emuera.Web/src/lib/opsApplier.ts`、`Emuera.Web/src/lib/hitTest.ts`
- NativeAOT：Microsoft 官方 Native AOT 文档（Android 标注 experimental、no built-in Java interop）；.NET 10 RC2 Android NativeAOT 启动 271ms vs Mono AOT 1200ms（.NET Conf China 2025）
- .NET 11 评估：`docs/2026.8.4.安卓性能优化2/android-perf-2-net11-eval.md`；官方 MAUI CoreCLR 博客（Preview 4 / Preview 6）、MS Learn .NET 11 runtime what's-new、Android 最低 API 24 breaking change
- 质量护栏：I-12（`Emuera.Headless/Shared/**` 警告抑制边界）；测试入口 `tests/run_all.py`
