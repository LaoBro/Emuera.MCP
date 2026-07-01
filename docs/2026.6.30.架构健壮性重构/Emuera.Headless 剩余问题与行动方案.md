# Emuera.Headless 剩余问题与行动方案

> 基准：[Emuera.Headless 架构健壮性复评报告.md](file:///d:/LaoBro/Emuera.MCP/docs/2026.6.30.架构健壮性重构/Emuera.Headless%20架构健壮性复评报告.md)（2026-07-01）
> 复核日期：2026-07-02
> 定位：本文档汇总**尚未解决或仅部分解决**的问题与对应行动方案，作为后续重构的执行清单。已解决问题不在此列。

## 一、现实约束

经与维护者确认，项目维护范围收窄：

1. **只维护 Emuera.Headless**：Emuera（WinForms）项目功能数年前已稳定，无维护必要，仅留作功能参考。
2. **CLI 交互模式与 Server 模式**是实际使用入口；**CLI 管道模式（stdin pipe）与 JSONL 管道模式（stdin/stdout）无实际用途**。

### 已解决项概览（仅记录，不在下文展开）

I-03、I-04、I-05、I-06、I-07、I-08、I-13、I-15（JSONL + HTTP 500）、I-16、I-18；新增问题 N-01（Reset try-catch）、N-02（HeadlessRunner async 化）、N-03（ServerRunner 优雅关闭）、N-04（脚本死循环，由 T-021 `#if HEADLESS` + `GameExitException` 解决）均已落地。

***

## 二、剩余问题清单

### 🟡 部分解决

#### I-02 — GlobalStatic god-object

**状态**：部分解决（重大进展，仍是单会话约束）

**已做**：[Emuera.Headless/GlobalStatic.cs](file:///d:/LaoBro/Emuera.MCP/Emuera.Headless/GlobalStatic.cs) 是 Headless 专用实现，核心字段改为 `property + internal set`，setter 加锁 + 严格断言；新增幂等 `Reset()` 方法，由 `Session.GameLoopAsync` finally 块调用。

**未做**：字段仍是进程级 `static`，**无法支持并发多会话**。原报告建议的"`IGameRuntime` 上下文接口"未实施——**单会话契约已确认**（server 模式只支持单会话，见 [CLAUDE.md](file:///d:/LaoBro/Emuera.MCP/CLAUDE.md) 与 [SPEC_SINGLE_THREAD.md §5.2](file:///d:/LaoBro/Emuera.MCP/docs/old/3.single-thread/SPEC_SINGLE_THREAD.md)），现有硬约束（`HttpGameServer` 单 `_session` + 409、`GlobalStatic` 锁 + 断言 + 幂等 `Reset`）足以防误用，无需再引入上下文接口。

#### I-10 — AgentLog 单例

**状态**：部分解决

**已做**：[AgentLog.cs](file:///d:/LaoBro/Emuera.MCP/Emuera.Headless/Agent/AgentLog.cs) 默认开启，加 `lock` 保护写入，注册 `ProcessExit` 钩子确保 flush。

**未做**：仍是 `Lazy<>` 单例 + 文件路径依赖 `Program.ExeDir` 静态。未引入 `Microsoft.Extensions.Logging` 抽象。Server 多会话共享同一文件句柄（与单会话约束叠加，目前不构成实际问题）。

#### I-17（部分）— EmueraConsole.AgentBridge 合并

**状态**：部分解决

**已做**：`EmueraConsole.AgentBridge.cs` 已合并入 [EmueraConsole.cs](file:///d:/LaoBro/Emuera.MCP/Emuera.Headless/UI/Game/EmueraConsole.cs)。

**未做**：`partial` 修饰符仍保留（[EmueraConsole.cs:20](file:///d:/LaoBro/Emuera.MCP/Emuera.Headless/UI/Game/EmueraConsole.cs#L20)），且 `Emuera/Emuera.sln` 仍存在。详见下文 I-17 未解决部分。

***

### ❌ 未解决

#### I-01 — 共享源码 glob（结构性风险）

**状态**：未解决（已调整为物理迁移方案，见 [T-023](#t023)）

[Emuera.Headless.csproj:22-51](file:///d:/LaoBro/Emuera.MCP/Emuera.Headless/Emuera.Headless.csproj#L22-L51) 仍用 `<Compile Include="..\Emuera\...">` glob 共享 Runtime/UI/Game 源码。鉴于 WinForms 不再维护，原"抽 `Emuera.Core` 类库"方案改为**物理迁移源码**到 `Emuera.Headless/` 下，工作量降低一个量级。

#### I-09 — 零 C# 单元测试

**状态**：未解决

`tests/` 目录全是 Python 端到端测试，**没有任何 xUnit/NUnit 项目**。`AgentJsonlProtocol.BuildTurn`、`HttpSessionIO` 队列语义、`ButtonRegionTracker` 坐标计算等纯逻辑无法隔离测试，每次 C# 改动仍需 `dotnet build` + Python 才能验证。

#### I-11 — HeadlessConsole.Environment.Exit 绕过 Dispose

**状态**：未解决

[HeadlessConsole.cs](file:///d:/LaoBro/Emuera.MCP/Emuera/UI/Game/HeadlessConsole.cs) 的 `Close()` / `ExitApplication()` 仍 `Environment.Exit(0)`。该文件由 Emuera 主项目共享，改动会同时影响 WinForms——这是未修复的合理顾虑。**T-023 物理迁移后**该文件归 Headless 独占，可直接改为抛 `GameExitException`，无 WinForms 顾虑。

#### I-12（阶段 2）— csproj 质量护栏

**状态**：阶段 1 已落地，阶段 2 未实现

[Emuera.Headless.csproj](file:///d:/LaoBro/Emuera.MCP/Emuera.Headless/Emuera.Headless.csproj) 已启用 `EnableNETAnalyzers` + `AnalysisMode>Minimum` + `EnforceCodeStyleInBuild`，按路径分级的 `.editorconfig` 抑制 `Emuera/**` 历史警告。

**阶段 2 未做**：`<Nullable>` 仍 `disable`，未启用 `<TreatWarningsAsErrors>`。原因：会因 `Emuera/` 共享源码警告导致 Headless 构建失败。**T-023 物理迁移后**可撤销 `.editorconfig` 中 `[Emuera/**]` 段的 CA 抑制，统一启用 nullable + warnings as errors。详见 [docs/TODO.md](file:///d:/LaoBro/Emuera.MCP/docs/TODO.md) T-022。

#### I-14 — IConsoleUI 抽象泄漏 System.Drawing

**状态**：未解决

[IConsoleUI.cs:86](file:///d:/LaoBro/Emuera.MCP/Emuera/UI/Game/IConsoleUI.cs#L86) `ToolTipDrawEventArgs.Graphics` 仍暴露 `System.Drawing.Graphics`。`IConsoleUI` 也仍用 `System.Drawing.Point/Rectangle/Color`。HeadlessConsole 实现仍需引用 `System.Drawing.Common`（虽然 Headless 路径不触发 ToolTip Draw 事件）。**T-023 物理迁移后**可逐步替换为不依赖 System.Drawing 的抽象类型。

#### I-17 — 两份 .sln + partial 修饰符多余

**状态**：未解决

- [Emuera/Emuera.sln](file:///d:/LaoBro/Emuera.MCP/Emuera/Emuera.sln) 仍存在（仅含 Emuera 单项目），与根 [Emuera.sln](file:///d:/LaoBro/Emuera.MCP/Emuera.sln) 易混淆。
- [EmueraConsole.cs:20](file:///d:/LaoBro/Emuera.MCP/Emuera.Headless/UI/Game/EmueraConsole.cs#L20) `internal sealed partial class` 但全项目只有这一个文件，`partial` 多余。

**T-023 物理迁移**会删除 `Emuera/Emuera.sln` 与 `Emuera/Emuera.csproj`，顺带解决此问题。

***

## 三、行动方案

### <a id="t023"></a>T-023：I-01 简化 — 物理迁移共享源码到 Emuera.Headless

**核心思路**：用 `git mv` 把 `Emuera/` 下被共享的源码物理迁移到 `Emuera.Headless/` 下，删除 csproj glob，WinForms 专用文件留在 `Emuera/` 原处作只读参考。

| 步骤 | 操作 |
|---|---|
| 1 | `git mv Emuera/Runtime/ Emuera.Headless/Runtime/`（排除 `Sound.WMP.cs`/`Sound.NAudio.cs`/`NAudio_LoopStream.cs`/`WinInput.cs`/`Clipboard.cs`，留在 `Emuera/` 原处） |
| 2 | `git mv` `Emuera/UI/Game/` 下被共享的文件到 `Emuera.Headless/UI/Game/`（**不可覆盖**已有的 [EmueraConsole.cs](file:///d:/LaoBro/Emuera.MCP/Emuera.Headless/UI/Game/EmueraConsole.cs)；`EmueraConsole.Print.cs` 不迁移） |
| 3 | `git mv Emuera/UI/FontFactory.cs Emuera.Headless/UI/FontFactory.cs` |
| 4 | `git mv Emuera/Properties/lang/ Emuera.Headless/Properties/lang/` |
| 5 | 简化 [Emuera.Headless.csproj](file:///d:/LaoBro/Emuera.MCP/Emuera.Headless/Emuera.Headless.csproj)：删除所有 `<Compile Include="..\Emuera\...">` glob 与 `<Compile Remove="...">` 排除项，改由 SDK 默认 glob 包含；`<EmbeddedResource>` 路径同步更新 |
| 6 | 删除 `Emuera/Emuera.csproj`、`Emuera/Emuera.sln`；仓库根 `Emuera.sln` 移除 Emuera 项目引用，只保留 `Emuera.Headless` |

**解锁**：T-022（I-12 阶段 2）、I-11、I-14、I-17

**验收**：

- `dotnet build Emuera.Headless/Emuera.Headless.csproj -c Debug` 0 error（warning 基线不变）
- `Emuera.Headless.csproj` 中不再出现 `..\Emuera\` 路径引用
- `run_all.py` 全部回归测试通过
- `Emuera/` 目录下不再有 `.csproj`/`.sln`，残留文件不被任何项目引用

### T-024：废弃 CLI/JSONL 管道模式入口

**核心思路**：移除 stdin 管道入口与 `ConsoleOutIO`，收窄维护与测试矩阵。

| 步骤 | 操作 |
|---|---|
| 1 | 删除 [ConsoleOutIO.cs](file:///d:/LaoBro/Emuera.MCP/Emuera.Headless/Server/ConsoleOutIO.cs)（stdin/stdout 封装，仅管道模式使用） |
| 2 | [HeadlessOptions.cs](file:///d:/LaoBro/Emuera.MCP/Emuera.Headless/HeadlessOptions.cs) / [HeadlessRunner.cs](file:///d:/LaoBro/Emuera.MCP/Emuera.Headless/HeadlessRunner.cs) 移除 `--protocol cli`/`--protocol jsonl` 的 stdin 管道分支；`--protocol` 参数保留以兼容交互 CLI |
| 3 | [AgentCliProtocol](file:///d:/LaoBro/Emuera.MCP/Emuera.Headless/Agent/AgentCliProtocol.cs) 改为只支持交互式终端（移除 stdin pipe 读路径） |
| 4 | [AgentJsonlProtocol](file:///d:/LaoBro/Emuera.MCP/Emuera.Headless/Agent/AgentJsonlProtocol.cs) **保留**（Server 模式通过 `HttpSessionIO` 仍依赖它，不改动） |
| 5 | 精简 `tests/test_cli.py`、`tests/test_jsonl.py` 中针对 stdin 管道的用例；更新 [tests/README.md](file:///d:/LaoBro/Emuera.MCP/tests/README.md) 测试矩阵与 [CLAUDE.md](file:///d:/LaoBro/Emuera.MCP/CLAUDE.md) 运行命令 |

**验收**：

- `dotnet build Emuera.Headless/Emuera.Headless.csproj -c Debug` 0 error
- 交互 CLI 模式与 Server 模式回归测试全绿
- `ConsoleOutIO.cs` 已删除，代码中无 stdin 管道引用

***

## 四、优先级路线图

### P1 — 新功能开发期间应完成

1. **T-023**：物理迁移共享源码到 `Emuera.Headless/` —— 替代原"抽 `Emuera.Core`"，是后续所有质量护栏的前置，顺带解决 I-01、I-17
2. **T-024**：废弃 CLI/JSONL 管道模式入口 —— 收窄维护面
3. **I-09**：建立 C# xUnit 测试项目 —— 保护 Channel/Reset/WaitForTurnAsync 等纯逻辑
4. **T-022（I-12 阶段 2）**：`Nullable enable` + `TreatWarningsAsErrors` —— 前置 T-023 满足后可推进
5. **I-02 收尾**：~~评估 `IGameRuntime` 上下文接口~~ —— **已关闭**：单会话契约已确认（CLAUDE.md + SPEC_SINGLE_THREAD §5.2），现有硬约束（409 + 锁 + 幂等 Reset）已足够防误用，无需引入上下文接口

### P2 — 持续改进

1. **I-11**：HeadlessConsole 改抛 `GameExitException`（T-023 后可直接改，无 WinForms 顾虑）
2. **I-14**：`IConsoleUI` 抽象去 `System.Drawing`（与 T-023 协同推进）
3. **I-10**：可注入 `ILogger`（Server 多会话前置）
4. **I-17 收尾**：清理 `EmueraConsole.cs` 多余 `partial` 修饰符

### 关键路径依赖

```
T-023 (物理迁移) ──┬─→ T-022 (Nullable + WarningsAsErrors)
                   ├─→ I-11 (HeadlessConsole Exit)
                   ├─→ I-14 (IConsoleUI 去 System.Drawing)
                   └─→ I-17 (sln/partial 清理)

T-024 (管道废弃) ──→ 收窄测试矩阵，为 I-09 单测项目减负
```

**建议下一步**：以 T-023 作为独立 PR 启动（纯文件移动 + csproj 简化，diff 体积大但逻辑改动小）。落地后即可并行推进 T-024 与 I-09。
