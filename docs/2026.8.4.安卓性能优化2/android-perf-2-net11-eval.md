# .NET 11 升级评估：NativeAOT 实现路径

> 日期：2026.8.7
> 状态：评估完成，结论落地（决策记录，避免重复调研）
> 关联：`android-perf-2.md` 阶段 1（引擎 NativeAOT 主攻 / 壳层门控 3.4）
> 依据：.NET 11 Preview 4-6 官方博客与 MS Learn（2026-05 至 2026-08 发布）；项目现状（TFM / ABI 矩阵 / 构建配置实测）

---

## TL;DR（三句话结论）

1. **引擎 NativeAOT（3.2/3.3）：.NET 10 即可实现，无需等 .NET 11**——现在就用 .NET 10 开验证分支；.NET 11 的 NativeAOT runtime 改进（接口分发 / Runtime Async / R2R）登记为验证分支稳定后的升级评估项，不阻塞当前。
2. **壳层 NativeAOT（3.4）：.NET 11 是方向性解锁（CoreCLR 默认化 + Android interop type map 落地），但不是即时可用**——Java interop 仍 experimental；**最低 API 21→24 与"低端机覆盖"直接冲突**；**Preview 6 起 Mono 回退选项已移除**（升 .NET 11 = 单程票）。结论：门控到 GA（2026-11 前后）后开独立实验分支做 A/B 与冒烟，期间保持 .NET 10 Mono Full AOT 壳层基线不动。
3. **顺带红利**：CoreCLR 移动端诊断统一（dotnet-trace / dotnet-counters / dotnet-gcdump 直接对 Android 进程可用），对阶段 0 归因取证（2.1/2.4）是真机侧高价值工具，应纳入实验分支验收清单。

---

## 0. 项目现状（实测，写死避免重查）

| 项目 | 现状 | 对 .NET 11 的含义 |
|---|---|---|
| `Emuera.Headless.Core` | `net10.0` 类库，无 MAUI/AspNet 依赖 | 引擎 NativeAOT 与 .NET 11 解耦，.NET 10 即可 |
| `Emuera.Headless.Cli` | `net10.0` Exe，`PublishSingleFile=true`，`ServerGarbageCollection=true` | 3.2 验证分支入口（win-x64 先行） |
| `Emuera.Maui` | `net10.0-android`，`SupportedOSPlatformVersion=21.0`，`AndroidEnableMarshalMethods=false`（XAGNM7009 workaround），Release `RunAOTCompilation=true`（Mono Full AOT） | 升 .NET 11 后 API 21 不再受 CoreCLR 支持；MarshalMethods 开关需重评 |
| ABI 矩阵 | obj 目录实测仅 `android-arm64` + `android-x64`，**无 armv7 产物** | 32 位 ARM 已不在构建矩阵；.NET 11 CoreCLR 对 armv7 的支持不确定性影响有限 |

---

## 1. .NET 11 关键事实核对（官方来源，写死避免重查）

| # | 事实 | 来源 | 对项目影响 |
|---|---|---|---|
| 1 | CoreCLR 成为 Android/iOS/Mac Catalyst 的 **Release+Debug 默认运行时**（Preview 4，2026-05-12） | devblogs: dotnet-maui-moves-to-coreclr-in-dotnet-11 | 壳层运行时栈将整体替换，非 NativeAOT 也生效 |
| 2 | **Preview 6 起 CoreCLR 是唯一运行时，`UseMonoRuntime` 回退属性已移除** | devblogs: coreclr-progress-and-mono-timeline-dotnet-maui | 升 .NET 11 = 单程票；3.4"保持 Mono Full AOT 基线"只在 .NET 10 内成立 |
| 3 | 官方性能口径：**Android 启动与包体积在 Mono ±10% 内**（iOS/Mac 一般更快）；已收到大应用回归报告（dotnet/android#10588、#10914，"measure your app"） | 同上 | CoreCLR ≠ NativeAOT；文档 3.4 里"271ms vs 1200ms"是 **NativeAOT** 演示数字（.NET Conf China 2025），不可外推给 CoreCLR |
| 4 | Android **NativeAOT 仍标 experimental**；Preview 6 起 **Android interop 的 trimmable type map 默认开启**——Java interop 障碍首次出现实质松动信号 | devblogs: coreclr-progress；MS Learn: native-aot 部署概览（no built-in Java interop 仍未移除） | 3.4 门控条件（Java interop 成熟）**未满足但方向明确** |
| 5 | **最低 Android API 21 → 24**（Preview 3 起）；API 21-23 仅 Mono 支持；CoreCLR 要求 API 24+ | MS Learn: MAUI what's new / breaking change android-minimum-api-level | 当前 `SupportedOSPlatformVersion=21.0` 与 1-2GB 低端机（Android 5/6）覆盖目标冲突——**产品决策点** |
| 6 | CoreCLR 模式 APK 内含 `libcoreclr.so` + `libclrjit.so`（**JIT 引擎仍在**）+ R2R assemblies | MS Learn: MAUI runtimes-compilation | CoreCLR 模式内存/防杀进程表现优于 Mono 有限；NativeAOT（单 .so）才是"无运行时"形态 |
| 7 | .NET 11 NativeAOT runtime 改进：shared dispatch helper 接口分发（减体积+密集接口调用提速）、**Runtime Async 支持 NativeAOT/R2R**、协变 Task 覆写修复、R2R 去虚拟化、crossgen2 内联限制解除 | MS Learn: dotnet/11/runtime | 引擎是解释器+协议 async 栈，**分发密集**——接口分发与 Runtime Async 是实质收益项 |
| 8 | 硬件基线升级：x86/x64 最低 x86-64-v2（新增 CX16/POPCNT/SSE3-4.2），R2R 目标 x86-64-v3；Arm64 R2R 目标 armv8.2-a+RCPC；旧 CPU 无法运行 | MS Learn: dotnet/11/runtime | Android 侧影响需在真机验证（低端 SoC 指令集子集）；x86 模拟器场景注意 |
| 9 | .NET 11 内置崩溃报告（进程内捕获托管栈/运行时状态，**专为移动端设计**） | MS Learn: dotnet/11/runtime | 对低端机崩溃取证有用，列入实验分支验收 |

---

## 2. 两条路径分别评估

### 2.1 路径 A：引擎先行 NativeAOT（3.2/3.3）——**.NET 11 非解锁项，用 .NET 10 立即执行**

NativeAOT 自 .NET 8 起即可发布 win-x64 / linux-bionic-arm64 产物；引擎（net10.0 类库 + CLI）不依赖 MAUI、不依赖 Java interop，**.NET 10 当前就能完整实现 3.2/3.3**。

.NET 11 对这条路径仅带来**增量改善**（#7：接口分发 / Runtime Async / R2R 去虚拟化），代价却是：

- **Preview 6 非 RTM**（GA 在 2026-11），工具链与 ILC 警告集合在预览期 churn——直接威胁 3.2"**ILC 警告清零**"验收目标（警告编号/行为可能随预览变化，清零工作反复返工）；
- TFM 迁移矩阵：Core/Server/Cli/Tests 全升 net11.0，xUnit 479+ 用例与 run_all.py 回归基线同步挪动，与 3.2 验证分支目标正交；
- System.CommandLine 2.0.9 等依赖在预览期无验证背书。

**决策**：3.2/3.3 在 **.NET 10** 上执行（工具链稳定、验证分支独立、不触碰 MAUI API 24 问题）；.NET 11 的 NativeAOT 改进登记为"验证分支稳定后升级评估项"（收益量化后再迁移，迁移本身可推迟到 .NET 11 GA）。

### 2.2 路径 B：MAUI 壳 NativeAOT（3.4）——**.NET 11 方向性解锁，但门控仍不能放行**

对照 3.4 门控条件（".NET 10 后续 Android NativeAOT 的 Java interop 支持成熟"）逐项核对：

**解锁了什么：**
- CoreCLR 默认化（#1）——壳层不再需要手动 opt-in，运行时栈向主流统一；
- Android interop **trimmable type map 默认开启**（#4）——Java interop 从"无内置"走向"有基础构件"，SAF/WebView 通道首次看到可行路径；
- 移动端诊断统一（#9 + 2.4 归因）——dotnet-trace 可直接抓 Android 进程，阶段 0 真机取证工具链大幅简化。

**没解锁/新增阻碍：**
- **Java interop 仍标 experimental（no built-in Java interop 未从文档移除）**——3.4 门控条件**未满足**；
- CoreCLR 模式仍带 JIT 引擎（#6）——"防杀进程"内存目标拿不到 NativeAOT 级别的收益，甚至可能不如当前 Mono Full AOT 的内存画像（官方口径仅 ±10% 且有大应用回归报告 #3）；
- **API 24 门槛（#5）**——低端机（1-2GB RAM）恰是 Android 5/6（API 21-23）重灾区；升 .NET 11 且用 CoreCLR 将失去这批设备；
- **Mono 回退已移除（#2）**——升 .NET 11 后无法再回 Mono Full AOT 基线，3.4"期间保持 Mono Full AOT 壳层基线"只在 .NET 10 内成立。

**决策**：**不升，门控推迟到 GA**。3.4 门控条件改写为：**.NET 11 GA（2026-11 前后）后开独立壳层实验分支**，实验内容见 §5 行动 4。在此期间：
- 壳层保持 .NET 10 Mono Full AOT（现状不动）；
- 引擎验证分支（.NET 10）先跑通，为壳层提供"引擎侧已达最优"的先验。

### 2.3 升级 .NET 11 的整体净收益判断

| 维度 | 引擎路径（3.2/3.3） | 壳层路径（3.4） |
|---|---|---|
| .NET 11 是否解锁 | 否（.NET 10 已能做） | 部分（interop type map，仍 experimental） |
| .NET 11 是否改善 | 是（接口分发/Runtime Async/R2R） | 是（CoreCLR 默认化、诊断统一） |
| .NET 11 新增阻碍 | 预览期 ILC churn、TFM 迁移矩阵 | API 24 门槛、Mono 单程票、回归报告 |
| 建议 | **.NET 10 立即执行** | **等 GA 后实验分支** |

---

## 3. 对 `android-perf-2.md` 的修订

1. **3.4 门控条件改写**（已在主文档落地）：从".NET 10 后续 Java interop 成熟"改为"**引擎先行验证通过 + .NET 11 GA 后壳层实验分支（CoreCLR A/B + NativeAOT 冒烟）通过**"。
2. **进度表 3.4** 备注 .NET 11 评估结论（等 GA 后实验分支）。
3. **参考节**补充本评估文档与官方来源（见 §6）。

---

## 4. 风险登记（新增，并入主文档 §7 视角）

| 风险 | 影响 | 缓解 |
|---|---|---|
| .NET 11 最低 API 24（Android 7.0+），API 21-23 仅 Mono | 低端机（Android 5/6）用户无法安装新版本 | 属产品决策：确认目标设备分布；若必须覆盖 API 21-23，则 .NET 11 前停留 .NET 10 Mono 基线 |
| Preview 6 起 Mono 回退属性移除 | 升 .NET 11 后无法回退 Mono Full AOT 基线 | 壳层迁移前先跑完整回归矩阵（真机全流程 + Emuera.Maui.Tests）；实验分支独立，不并主分支 |
| CoreCLR Android 官方口径仅 ±10% 且有回归报告（#10588/#10914） | 壳层 CoreCLR 迁移后启动/包体反而劣化 | 实验分支做真机冷启动/二次启动/包体 A/B（P50/P95），不达标即关闭，维持 .NET 10 基线 |
| Android NativeAOT Java interop 仍 experimental | 壳层 SAF/WebView 冒烟可能失败 | 冒烟清单前置（SAF 选目录/读文件/存读档、WebView 渲染/手势/输入/缩放）；不通过即保持 CoreCLR 或 Mono |
| `AndroidEnableMarshalMethods=false`（XAGNM7009）与 CoreCLR/NativeAOT 通道冲突 | 壳层实验分支构建/运行异常 | 实验分支内重评该开关（.NET 11 的 dotnet/android 是否修复原生 marshal 生成 bug）；3.3 已列此项 |
| 引擎 .NET 11 升级的 NativeAOT 收益（接口分发/Runtime Async）未经量化 | 迁移后收益不明 | 登记为验证分支稳定后评估项；用 2.1 回放 harness 做方法级对比后再决定 |

---

## 5. 结论与行动清单

| # | 行动 | 时机 | 状态 |
|---|---|---|---|
| 1 | 3.2 引擎 NativeAOT 验证分支，**.NET 10**，win-x64 → android-arm64 | 现在 | ☐ |
| 2 | 3.3 JSON 源生成器迁移 + DataTable 子集验证 + MarshalMethods 重评（.NET 10 内完成） | 与 1 并行 | ☐ |
| 3 | 登记 .NET 11 评估窗口：**GA（2026-11 前后）后**开壳层实验分支 | GA 后触发 | ☐（登记） |
| 4 | 壳层实验分支清单：① CoreCLR A/B（冷/二次启动、包体、内存 P50/P95 vs Mono Full AOT 基线）② NativeAOT 冒烟（SAF 全流程 / WebView 渲染+手势+输入+缩放）③ API 24 覆盖决策 ④ dotnet-trace 真机归因取证（阶段 0 工具链红利） | GA 后 | ☐ |
| 5 | 引擎升级 .NET 11 的收益量化（接口分发/Runtime Async）作为 3.2 稳定后的独立评估项 | 验证分支稳定后 | ☐ |

**一句话结论**：**升级 .NET 11 对 NativeAOT"有帮助但不到解锁点"——引擎路径现在就用 .NET 10 做（.NET 11 只是锦上添花），壳层路径 .NET 11 值得等（GA 后实验），但需先过 API 24 覆盖决策与 Java interop 冒烟两关。**

---

## 6. 参考

- 官方博客：.NET MAUI Moves to CoreCLR in .NET 11（Preview 4）：https://devblogs.microsoft.com/dotnet/dotnet-maui-moves-to-coreclr-in-dotnet-11
- 官方博客：CoreCLR Progress and the Mono Timeline（Preview 6，Mono 回退移除）：https://devblogs.microsoft.com/dotnet/coreclr-progress-and-mono-timeline-dotnet-maui/
- MS Learn：What's new in .NET MAUI for .NET 11（最低 API 24）：https://learn.microsoft.com/dotnet/maui/whats-new/dotnet-11
- MS Learn：Breaking change: Minimum Android API level raised to 24：https://learn.microsoft.com/dotnet/core/compatibility/maui/11/android-minimum-api-level
- MS Learn：Runtimes and compilation（.NET MAUI，APK 构成）：https://learn.microsoft.com/dotnet/maui/deployment/runtimes-compilation
- MS Learn：What's new in .NET 11 runtime（NativeAOT 接口分发 / Runtime Async / 崩溃报告）：https://learn.microsoft.com/dotnet/core/whats-new/dotnet-11/runtime
- 中文综述（用户提供）：.NET 11 Preview 4 MAUI CoreCLR/NativeAOT：https://www.cnblogs.com/shenchuanchao/p/20043743/dotnet-11-preview-4-maui-coreclr-nativeaot
