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

**否决理由**：第一轮 S0 已证明瓶颈在**引擎指令语义 + 回合级快照/序列化/分配**（C# 侧），JS 运行时不在主矛盾；省 JS 外壳 ≠ 省渲染几何与数据层分配。**结论：无阶段 0 归因数据不立项**；若归因点名“内存/冷启动”主矛盾，优先引擎 NativeAOT（已在 3.2 主攻）与 WebView 进程/初始化优化，纯 MAUI 列为全部手段之后的最后后手。

### 1.5 Avalonia 迁移可行性与性价比（2026.8.7 调研）

#### TL;DR

> **技术上可行，经济上不适合以“Native AOT 解锁”为唯一迁移理由。** Avalonia 可以替换 WebView + Vue，使用 C# / XAML 与自定义绘制做 Android 原生 UI；但它仍然走 .NET Android workload，不能自动绕过当前 Android Native AOT 的工具链与 Java interop 限制。迁移的确定性成本是重写终端渲染器和交互层，性能收益仍需同机 A/B 证明。

**建议决策：暂不把 Avalonia 全量迁移列为主线；阶段 0 归因完成后，做一个有时限的 Avalonia vertical-slice PoC。** 由于现有 MAUI workload 已能生成 Android arm64 NativeAOT APK，Avalonia PoC 不再承担“解锁 AOT”的职责；只有在不依赖 Native AOT 的前提下显著降低低端机峰值内存/启动时间，或明确带来可接受的长期原生 UI 维护收益，才进入全量迁移。

#### 补充路线：Avalonia 外壳 + 保留 WebView

Avalonia 官方提供 `NativeControlHost`，可以在 Avalonia 视觉树中嵌入 Android 原生 `WebView`。因此存在一条比“立即重写 Vue/DOM”更低风险的 Android 壳验证路线：

```text
Avalonia Application
  └─ NativeControlHost
       └─ Android.Webkit.WebView
            └─ 现有 Vue dist + C#-JS bridge
```

这条路线的价值是验证 Avalonia Android 的 Activity 生命周期、输入法/返回键、SAF、发布链路和现有协议能否共存；`Emuera.Headless.Core`、Vue 产物和大部分行为测试仍可保留，适合作为 1.5-A 的短期实验。

但它**不是当前性能问题的解决方案**：WebView、V8/Blink、Vue reducer 和 JS↔C# bridge 仍然存在，低端机内存和前端 scripting 成本不会因外层换成 Avalonia 自动消失；Native AOT 仍需经过同样的 Android workload、JNI/Java interop 和真机验证。官方还列出原生 View 位于 Avalonia 渲染层之上、不能透明显示后方 Avalonia 内容、不受 Avalonia transform 影响、始终处于 Avalonia 内容上层、复杂裁剪受限等约束。故其定位应是“壳兼容性 PoC”，不能把结果外推为“原生 UI 性能已验证”。

如果 Android 与 Windows 必须继续共用一套 UI 壳，这条路线还会引入平台分叉：Android 可嵌入原生 WebView，Windows 仍需另选 WebView/第三方控件或保留现有 MAUI 壳。它降低单次改造风险，但会增加双壳维护成本。

#### 已核实事实与边界

| 结论 | 证据 | 对本项目的含义 |
|---|---|---|
| Avalonia 官方支持 Android；当前平台页要求 .NET 10：Android 16/API 36 的 ARM64、x64 为 Tier 1；Android 12-15/API 31-35 的 ARM64、ARM32、x64 为 Tier 2；Android 11/API 30 及以下为 Tier 3。 | Avalonia 官方平台矩阵与 Android 开发指南（见 §9） | 替换 MAUI 壳在产品平台层面可行，但当前 API 21-30 低端设备目标落在 Tier 3，不能按“官方支持 Android”直接推导兼容性和性能。 |
| Avalonia 官方 Native AOT 指南使用 `PublishAot=true`，要求编译 XAML/绑定、避免运行时动态 XAML，并明确提示动态控件、第三方控件和平台特性需要额外配置。 | Avalonia 官方 Native AOT 指南（见 §9） | Avalonia UI 层可以按 AOT 约束设计，但这不是 Android AOT 成功的证明。 |
| Microsoft .NET Native AOT 官方目标表仍将 Android 标记为 **Experimental, no built-in Java interop**；Android 的 x64/ARM64/ARM 支持取决于对应 .NET 版本。 | Microsoft Learn Native AOT deployment（见 §9） | Avalonia 仍需面对 Android 宿主、Activity、存储选择器和系统 Intent 的 interop 风险。 |
| 本仓库已实测：纯 `net10.0` CLI 项目使用 `.NET 10 + dotnet publish -r android-arm64` 没有通用 SDK Native AOT 路径（没有对应 KnownILCompilerPack，且 target ILC 包不存在）；但 `net10.0-android` MAUI 项目可以通过 Microsoft.Android workload 的 NativeAOT 管线。 | [`nativeaot-verify-report.md`](nativeaot-verify-report.md) §7；本次 MAUI 预检 | 换成 Avalonia 不会改变纯 CLI 工具链事实，也不是必要条件；现有 MAUI 壳应先直接验证 workload NativeAOT 的真机运行。 |
| Avalonia 的 StorageProvider 在 Android 支持文件/文件夹 picker、bookmark 和 `content:` URI 的流读写；但官方明确说明 Android 通常没有物理路径，不能依赖 `Path` 直接读写。 | Avalonia Storage Provider / Storage Item 文档（见 §9） | SAF 能迁移，但应实现基于 `IStorageFolder` / `IStorageFile` / bookmark 的适配器，不能把现有 URI 字符串替换后继续假设是文件系统路径。 |

**需要特别避免的误读：** 当前 `Emuera.Maui.csproj` 的 `RunAOTCompilation=true` 是 .NET Android 的 Mono Full AOT 配置，不等于 `PublishAot=true` 的 Native AOT。补充实测（2026.8.7）在不改项目文件的前提下执行 `dotnet publish Emuera.Maui/Emuera.Maui.csproj -f net10.0-android -r android-arm64 -c Release -p:PublishAot=true -p:RunAOTCompilation=false`，经 `Microsoft.Android.Runtime.NativeAOT.36.android-arm64` 产出 `com.emuera.maui-Signed.apk`（约 25.5 MiB）和 `lib/arm64-v8a/libEmuera.Maui.so`（约 40.4 MiB）；APK 签名及 16 KiB page alignment 校验通过，包内未发现托管程序集。故“MAUI Android NativeAOT 不能构建”已被本机实测推翻；当前仍未完成真机安装启动、WebView 和 SAF 全流程验证，且首次预检因 Core 的 IL2026/IL3050/IL2072 警告需使用 `TreatWarningsAsErrors=false` 才能继续，不能视为生产就绪。

#### 对当前架构的迁移映射

| 当前模块 | Avalonia 迁移后的处理 | 复用率判断 |
|---|---|---|
| `Emuera.Headless.Core`、ERB/CSV 引擎、`GamePaths`、`IGameDirAccessor`、JSON 源生成器和回归夹具 | 保留；把 UI 宿主依赖继续隔离在壳层 | 高，属于迁移的主要收益 |
| `BridgeHost` / `MauiBridgeIO` | 抽出与 UI 无关的 `GameSession` / `ITurnSink` / `IInputSink`；Avalonia 壳直接投递内存对象，保留 JSONL 作为 CLI/兼容测试协议 | 中高，但不是把当前类原样搬过去 |
| `MainPage`、`IJsBridge`、`AndroidJsBridge`、`WindowsJsBridge` | 改成 Avalonia `Application` / `TopLevel` / Android Activity 生命周期；删除 WebView 导航、JS 注入和 JS 消息桥 | 低，职责相似但 API 不兼容 |
| `SafGameDirAccessor` | 可复用 SAF 经验和 `IGameDirAccessor` 契约；实现改为 Avalonia StorageProvider + Android bookmark/Intent 适配，逐项验证持久读写 | 中，不能直接复用实现 |
| `Emuera.Web/src` | 不存在源码级迁移路径。13 个 Vue 组件、Pinia 状态、输入/手势/资源/协议 reducer 需要用 C# 控件和状态模型重建；现有测试主要作为行为规格 | 低 |
| `TerminalDisplay.vue` / `SegmentRenderer.vue` | 建议一个 `TerminalControl : Control`，在 `Render(DrawingContext)` 中绘制文本、图片、矩形和按钮热区；只保留视口行，不能为 5 万行 scrollback 建立控件树 | 低，但 Avalonia 的自绘能力匹配该方案 |

仓库当前 Web 前端共有 **34 个非测试源码文件 / 7,495 行**，另有 **21 个测试文件 / 6,777 行**；其中 `TerminalDisplay.vue` 为 574 行，`opsApplier.ts` 仍承担行级增量状态复制和深拷贝。这个规模说明迁移不是“把 Vue 模板翻译成 XAML”，而是重建一套原生终端渲染器。Avalonia 官方提供 `Control.Render`、`DrawingContext.DrawText/DrawGlyphRun/DrawImage`、裁剪/变换和指针事件，足以承载实现，但性能和 CJK 字形对齐必须做项目级验证（见 §9）。

#### 性能收益的可信度

**可能得到的收益：** 移除 WebView、V8/Blink 渲染进程、JS↔C# bridge 和 Vue 响应式对象，可以降低进程数量、首帧初始化和前端 reducer 分配；所有状态也可以在同一托管进程内传递。

**不能预先承诺的收益：** Avalonia 不是“免费获得 Canvas”。如果用 `TextBlock` / `Inline` 为每个 segment 或每行建立控件，布局树和对象分配会把当前 DOM 问题原样换名；正确方向仍是自定义单控件绘制 + 视口虚拟化 + 字形/图片缓存。引擎回合执行、SAF IPC、快照/增量模型和大文本数据本身也不会因为换 UI 框架而消失。

因此，Avalonia 的性能收益目前只能写成假设：

1. **冷启动/内存：** 预期移除 WebView 相关开销，但 Avalonia/Skia 自身初始化与 native 库体积会抵消一部分收益。
2. **每回合延迟：** 只有前端三段计时证明 scripting 或 WebView layout/paint 占主导时，原生渲染才可能成为主收益项；若瓶颈仍在引擎/SAF/数据分配，收益有限。
3. **滚动和大文本：** 自绘控件有机会优于 DOM，但要自行实现命中测试、按钮代数、图片映射图取色、选择/复制、无障碍和缩放语义，不能只比较一张首屏截图。

#### 粗略成本与性价比

以下是基于当前代码规模的工程估算，不是官方承诺：

| 阶段 | 工作量（1 名熟悉 C# UI 的工程师） | 产出 |
|---|---:|---|
| AOT/Android 可行性 spike | 2-5 人日 | 最小 Avalonia Android 项目，`android-arm64` 普通发布 + `PublishAot=true` 发布结果、ILC/interop 警告、真机启动日志 |
| vertical slice | 1-2 周 | 真实 `test_game` 首回合、终端文本/按钮/图片/滚动、输入、SAF 选择/读/写、返回键 |
| 全量迁移 | 约 3-6 人月 | 原生终端、设置/调试/游戏列表、双平台生命周期、行为回归、像素/低端机性能回归、发布流水线 |

| 目标 | 评价 |
|---|---|
| 只为获得 Android Native AOT | **低性价比**：Avalonia 不解除 .NET Android 的实验性 Java interop 和本地 ILC 门槛。 |
| 只为验证新原生壳、暂时保留 Vue/WebView | **短期可行**：改造面较小，但不减少 WebView/V8/JS 成本，且可能形成 Android/Windows 双壳。 |
| 为了去掉 WebView、降低进程/内存并接受 UI 重写 | **中等性价比**：值得做 PoC，但收益要用低端真机数据确认。 |
| 长期统一 C# UI、继续扩展复杂终端绘制 | **条件性中高性价比**：自绘控件和 C# 测试体系更统一，但前提是团队愿意长期维护自有渲染器。 |

#### 建议的 PoC 门槛

将下面任务作为独立实验分支，不改变当前 MAUI 基线：

1. 用当前 .NET/Avalonia 版本创建最小 Android 项目，分别执行普通 `dotnet publish` 和 `PublishAot=true` 的 `android-arm64` 发布；必须在真实 arm64 设备安装、启动并加载第一帧。只“编译成功”不算通过。
2. 复用 `Emuera.Headless.Core` 跑 `test_game`，先做单个 `TerminalControl`：40 行视口、CJK/彩色/粗斜体/下划线/删除线、按钮命中、图片/裁切/矩形、整屏清除、头部截断、缩放和 TINPUT 倒计时。
3. 用 Avalonia StorageProvider 完成 SAF 目录选择、bookmark 持久化、递归枚举、文件读取、存档写入和无写权限重选；对照现有 `SafGameDirAccessor` 的读写探针。
4. 在同一台低端设备、同一 `test_game` 回放序列上比较 MAUI+Vue 与 Avalonia：冷启动 P50/P95、首帧、峰值 RSS、连续 100 回合 P95、5 万行滚动帧时间、APK/AAB 体积和崩溃率。

建议把“继续全量迁移”设置为内部门槛，而不是凭体验决定：Native AOT 必须真机通过；若只走普通 Android 发布，则至少要求峰值 RSS 下降 20%、冷启动下降 15%、连续回合 P95 不回退超过 5%，并通过全部核心行为/SAF 回归。若 AOT 不通过且上述指标也不达标，立即关闭 Avalonia 路线，继续优化现有 MAUI/WebView 和引擎。

**路线调整建议：** 阶段 0、引擎 NativeAOT 和前端数据层优化继续按原计划推进；新增一个不阻塞主线的“1.5-A Avalonia spike”，分为两档：先做 Avalonia + WebView 混合壳验证，再决定是否做无 WebView 的 `TerminalControl`。如果原生渲染 PoC 通过，再先抽取 `GameSession` / `ITurnSink` 等 shell-neutral 边界，之后才迁移渲染器。这样即使最终不采用 Avalonia，边界抽取仍会降低当前 `BridgeHost` 对 MAUI/WebView 的耦合。

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

### 3.4 MAUI 壳 NativeAOT（现在可构建，独立门控）

> 微软文档 Android NativeAOT 仍标 experimental（no built-in Java interop）；壳层重度依赖 JNI：SAF `ContentResolver.OpenInputStream`（`SafGameDirAccessor`）、WebView（MAUI handler 过 JNI）。
> **2026.8.7 MAUI workload 预检已通过构建门槛**：当前 `.NET 10 + Microsoft.Android workload 36.1.69` 在 `PublishAot=true`、`android-arm64` 下能生成签名 APK；这条路径不需要 Avalonia，也不必等待 .NET 11 才能开始。未通过的是“生产门槛”：静态分析警告治理、真机启动和 JNI/SAF/WebView 全流程仍待完成。
> **2026.8.7 .NET 11 评估完成（`android-perf-2-net11-eval.md`）**：.NET 11 Preview 4-6 将 CoreCLR 定为 Android 默认运行时、Android interop trimmable type map 默认开启（Java interop 首次实质松动），但：① NativeAOT 仍 experimental；② 最低 API 21→24 与低端机覆盖冲突；③ Preview 6 起 Mono 回退属性移除（升 .NET 11 = 单程票）。**结论：.NET 11 GA（2026-11 前后）仍是生产切换复评窗口，不是当前开始 NativeAOT 构建的前置条件。**

- [ ] **构建门控**：保留独立 `NativeAOT` 发布配置，使用 `PublishAot=true`、`RunAOTCompilation=false`；`TreatWarningsAsErrors=false` 只允许用于预检，生产配置必须逐条处理/豁免 IL2026、IL3050、IL2072 等警告。
- [ ] **运行门控**：引擎先行验证（3.2/3.3，.NET 10）通过 **且** 当前 MAUI NativeAOT `android-arm64` APK 在真实设备启动并通过全流程；再做冷/二次启动、包体、内存 P50/P95 与现有 Mono Full AOT 基线 A/B。
- [ ] 冒烟测试清单（切换前）：SAF 选目录/读游戏文件/存读档全流程、WebView 渲染 + 手势、`TinputCountdown`/输入框、捏合缩放。
- [ ] 切换后回归：`Emuera.Maui.Tests` + 真机全流程 + 启动耗时 A/B（271ms vs 1200ms 目标验证——注意该数字为 NativeAOT 演示值，CoreCLR 官方口径仅 Mono ±10%）。
- [ ] 期间保持 Mono Full AOT 作为壳层基线（现状不动；**仅 .NET 10 内成立**——.NET 11 Preview 6 已移除 Mono 回退）。
- [ ] ABI/系统版本策略：先以 `android-arm64` 为 NativeAOT 主验证包，`android-x64` 仅用于模拟器/兼容性验证；API 21-23 的实际可用性必须用设备矩阵确认，NativeAOT 冒烟不通过时继续发布当前 Mono Full AOT 包。
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
| 1.1-1.4 | 探索结论（WASM/Canvas/渲染推迟/纯 MAUI 否决） | ✅ 已定 | 见第 1 节决策记录；1.4 纯 MAUI 列为最后后手 |
| 1.5 | Avalonia 迁移可行性与性价比调研 | ✅ 已完成 | 技术可行；不作为 Native AOT 解锁方案；阶段 0 后做有门槛的 vertical-slice PoC |
| 1.5-A | Avalonia 混合壳 PoC（`NativeControlHost` + Android WebView，可选） | ☐ | 只验证壳/生命周期/SAF/发布兼容性；不把结果当作性能或 Native AOT 结论 |
| 2.1 | 回放 harness | ☐ | |
| 2.2 | 前端三段计时 | ☐ | |
| 2.3 | 前端分配统计 | ☐ | |
| 2.4 | 引擎回合耗时基线 | ☐ | |
| 3.1 | 可行性事实核实 | ✅ 部分 | 反射/插件/AspNetCore 已核实；`Expression.Compile`/`Type.GetType` 扫描已补（全仓零匹配，动态反射仅 PluginManager 一处，Android 禁用） |
| 3.2 | NativeAOT 引擎先行（win-x64 → android-arm64） | ✅ win-x64 达成；⚠️ 纯 SDK CLI android-arm64 不通；MAUI workload 构建预检通过 | 验证分支 `nativeaot-verify`：win-x64 产物 26.2MB 单文件，xUnit 685/685 + run_all 14/14 全绿；纯 `net10.0` CLI 的 android-arm64 仍受 KnownILCompilerPack/Cross-OS/ILC 包限制；但当前 `Emuera.Maui` 用 `Microsoft.Android.Runtime.NativeAOT.36.android-arm64` 已生成签名 APK。仍待 IL 警告治理和真机运行，不把 `.NET 11 GA` 当作构建前置。已保留 android 条件编译基础（排除 Server/Kestrel）。详见 `nativeaot-verify-report.md` §7 |
| 3.3 | JSON 源生成器迁移 + DataTable 验证 + MarshalMethods 评估 | ✅ 完成 | JSON 源生成器迁移（`EmueraJsonContext`/`ServerJsonContext`，wire 不变——xUnit relaxed escaping 断言验证）；DataTable 子集验证通过（`tests/test_datatable_aot.py`，托管+AOT 双跑 13/13：DT_* 全指令链 + XML 往返实测可用；发现 DT_FROMXML 异 key 失败为 .NET Core 固有行为非 AOT 回归）；MarshalMethods 重评：P/Invoke 仅终端平台层，AOT 已验证无需迁移 LibraryImport |
| 3.4 | MAUI 壳 NativeAOT（运行门控） | ☐ | .NET 10 workload 已通过 `android-arm64` 构建预检；待 IL 警告治理、真机启动与 JNI/SAF/WebView 全流程，.NET 11 GA 后复评生产性 |
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
- Avalonia 官方平台矩阵：https://docs.avaloniaui.net/docs/supported-platforms（Android 支持、.NET 10 最低版本、API/架构等级；访问：2026.8.7）
- Avalonia 官方 Android 开发与发布：https://docs.avaloniaui.net/docs/platform-specific-guides/android、https://docs.avaloniaui.net/docs/deployment/android（Android workload、APK/AAB、发布流程；访问：2026.8.7）
- Avalonia 官方嵌入 Android 原生 View：https://docs.avaloniaui.net/docs/platform-specific-guides/android/embed-native-views（`NativeControlHost`、`WebView` 嵌入及透明/变换/Z-order/裁剪限制；访问：2026.8.7）
- Avalonia 官方 MAUI 迁移与性能指南：https://docs.avaloniaui.net/docs/migration/maui、https://docs.avaloniaui.net/docs/app-development/performance（手工迁移、虚拟化和性能分析；访问：2026.8.7）
- Avalonia 官方 Native AOT：https://docs.avaloniaui.net/docs/deployment/native-aot（`PublishAot`、编译绑定、动态控件/第三方控件限制；访问：2026.8.7）
- Avalonia 官方自定义绘制与输入：https://docs.avaloniaui.net/docs/custom-controls/drawing-custom-controls、https://docs.avaloniaui.net/docs/graphics-animation/custom-rendering、https://docs.avaloniaui.net/docs/input-interaction/pointer（`Control.Render`、`DrawingContext`、文本/字形/图片、指针事件；访问：2026.8.7）
- Avalonia 官方 StorageProvider：https://docs.avaloniaui.net/docs/services/storage/storage-provider、https://docs.avaloniaui.net/docs/services/storage/storage-item（Android picker/bookmark、`content:` URI、流读写和物理路径限制；访问：2026.8.7）
- Microsoft Learn Native AOT 平台限制：https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/#platformarchitecture-restrictions（Android experimental、no built-in Java interop；访问：2026.8.7）
- .NET 11 评估：`docs/2026.8.4.安卓性能优化2/android-perf-2-net11-eval.md`；官方 MAUI CoreCLR 博客（Preview 4 / Preview 6）、MS Learn .NET 11 runtime what's-new、Android 最低 API 24 breaking change
- NativeAOT 验证：`docs/2026.8.4.安卓性能优化2/nativeaot-verify-report.md`（工具链路径 / ILC 清单 / 回归矩阵 / 体积耗时）
- 质量护栏：I-12（`Emuera.Headless/Shared/**` 警告抑制边界）；测试入口 `tests/run_all.py`
