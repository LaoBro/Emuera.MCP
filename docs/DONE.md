# DONE.md

## 已完成目标

### T-005：CLI 模式 DisplayTime 动态倒计时

- 状态：已实现
- 说明：TINPUT 带 `DisplayTime` 时，CLI 终端通过 `SetCursorPosition` 原地覆盖倒计时行，超时后替换为 `TimeUpMes`。

### T-006：CLI 模式 CLEARLINE 删行后终端同步

- 状态：已实现
- 说明：`deleteLine()` 中添加 HEADLESS 分支，先尝试从 `_agentBuffer` 移除（行仍在缓冲区时），缓冲区空则累加 `_pendingEraseRows`。`AgentCliProtocol.FlushBuffer()` 中调用 `EraseTerminalRows()` 用光标上移 + 空格覆盖擦除终端行。`REUSELASTLINE`（`PrintTemporaryLine`）依赖的 `deleteLine(1)` 同样受益。

### T-009：CLI 模式 changeLastLine 终端同步

- 状态：已实现
- 说明：`changeLastLine()` = `deleteLine(1)` + `PrintSingleLine()`。T-006 的 `EraseTerminalRows()` 在 `FlushBuffer()` 中先擦除终端旧行（光标回到旧行位置），再输出新行，实现原地替换。`WriteAlignedLine` 根据 `IsLineEnd` 控制换行，`RemoveLastLineFromAgentBuffer()` 能正确处理不换行行的移除。DisplayTime 倒计时场景由 T-005 单独处理。

### T-010：CLI 模式文字样式与颜色提示

- 状态：已实现
- 范围：`SETCOLOR`、`RESETCOLOR`、`FONTBOLD`、`FONTITALIC`、`FONTREGULAR`、`FONTSTYLE`、`SETFONT`
- 说明：WinForms 下这些指令改变文字颜色、粗体、斜体、字体。终端不支持富文本样式，但可以用 ANSI 转义序列模拟部分效果：
  - `SETCOLOR` → ANSI 256色/真彩色转义 `\x1b[38;5;Nm` 或 `\x1b[38;2;R;G;Bm`
  - `FONTBOLD` → ANSI `\x1b[1m`
  - `FONTITALIC` → ANSI `\x1b[3m`
  - `RESETCOLOR` / `FONTREGULAR` → ANSI `\x1b[0m`
  - `SETBGCOLOR` → ANSI `\x1b[48;2;R;G;Bm`
- 纳入范围：
  - 在 `WriteAlignedLine()` 中根据当前 `StringStyle` 的颜色/字体信息输出 ANSI 转义。
  - 每行结束后重置样式，避免影响后续行。
- 不纳入范围：
  - `SETFONT` 改变字体族（终端不支持）。
  - `SETBGIMAGE` / `CLEARBGIMAGE`（终端不支持背景图）。
  - `SETBGCOLOR` 背景色（会覆盖用户终端配色方案，且为背景色全局状态追踪复杂度高）。
- 实现说明：
  - 新增 `FormatLineWithAnsi()` 方法，逐段遍历 `ConsoleDisplayLine` 中的 `ConsoleStyledString`，提取 `StringStyle.Color`（真彩色 `\x1b[38;2;R;G;Bm`）和 `StringStyle.FontStyle`（粗体 `\x1b[1m`、斜体 `\x1b[3m`），仅在样式变化时输出转义码。
  - `WriteAlignedLine()` 和 `FormatLineForTerminal()` 均调用 `FormatLineWithAnsi()`，保证增量输出与全量刷新一致。
  - 宽度计算与对齐仍基于纯文本（ANSI 转义不占显示宽度）。
  - 受 `IsAnsiEnabled()`（`Program.AnsiEnabled || !OperatingSystem.IsWindows()`）控制，终端不支持时回退到纯文本。
- 验收：
  - `SETCOLOR` 后的文字在终端上显示对应颜色。
  - `FONTBOLD` 后的文字在终端上显示粗体。
  - 样式重置后恢复正常显示。

### T-011：CLI 模式 PRINTBUTTON 按钮选择模式

- 状态：已实现
- 说明：CLI 终端在 `WaitInput` 状态下按 `↑` 键进入按钮选择模式，提示行显示 `> [按钮]  ↑↓切换 Enter确认`，`↑↓` 循环切换按钮，`Enter` 确认提交按钮值，`Esc` 退出选择模式。`CollectCurrentButtons()` 采集 `Generation == LastButtonGeneration` 的有效按钮，排除过期按钮。

### T-013：CLI 按钮选择模式跨轮次状态同步

- 状态：已实现
- 说明：提取 `SyncButtonState()` 统一管理按钮模式的进入/退出/刷新，在 `RunConsoleKeyLoop` 的三个关键点（FullRefresh 后、超时后、每轮 `FlushBuffer()` 后）调用。`ConfirmButton()` 和 Timeout 路径不再手动清除按钮状态，全部由 `SyncButtonState()` 统一处理。新增 `ButtonListEquals()` 辅助方法检测按钮列表是否变化（按 Generation + Input 值比较），新增 `ClearInputBuffer()` 提取重复的输入缓冲区清除逻辑。

### T-012：CLI 模式 HTML_PRINT 纯文本降级

- 状态：已实现
- 范围：`HTML_PRINT`、`HtmlManager`、`EmueraConsole.AgentBridge`
- 说明：`HTML_PRINT` 解析 HTML 标签生成富文本行（含按钮、图片、对齐等）。CLI 下 `WriteAlignedLine()` 原先调用 `line.ToString()` 获取纯文本，非文本节点（`ConsoleImagePart`、`ConsoleSpacePart`、`ConsoleRectangleShapePart`、`ConsoleDivPart`）的 `ToString()` 返回完整 HTML 标签，导致文本过长、排版错位、自动换行后按钮失灵。
- 实现方案：新增 `BuildTerminalLine()` 方法逐节点构建终端友好文本，降级规则：`ConsoleStyledString`→原样文本；`ConsoleSpacePart`→像素宽度转空格数；`ConsoleImagePart`/`ConsoleRectangleShapePart`→跳过；`ConsoleDivPart`→递归子行。
- 验收：`HTML_PRINT` 文字内容正确显示，对齐/粗体/斜体样式降级但不丢失语义，非文本节点不再输出 HTML 标签文本。

### T-018：HEADLESS AgentBuffer 删除最后一行容错

- 状态：已实现
- 范围：`EmueraHeadless/UI/Game/EmueraConsole.AgentBuffer.cs`
- 说明：`deleteLine()` 调用 `RemoveLastLineFromAgentBuffer()` 时，若 `_agentBuffer` 内容为空或只有 1 个字符，`LastIndexOf` 可能因负数参数抛异常。
- 实现方案：在 `LastIndexOf` 调用前增加 `content.Length <= 1` 的前置判断，直接清空缓冲区并递减计数。
- 验收：缓冲区只有不换行单行时调用 `deleteLine()` 不抛异常，多行缓冲区删除最后一行后前序内容保留正确。

### T-014：CLI 模式游戏结束后主动退出

- 状态：已实现
- 范围：`Emuera.Headless/Agent/AgentCliProtocol.cs`
- 说明：`AgentCliProtocol.RunAgentLoop` 的 `while` 条件只看 `token.IsCancellationRequested`，从不看 `console.State`。游戏脚本同步执行完毕后 `console.State` 已被置为 `Quit` 或 `Error`，但主循环仍会继续 `Poll` 等待永远不会到来的输入：ConsoleKey 路径每轮 `WaitOne(50ms)` 忙等，VT 路径同样忙等，pipe 路径靠 stdin EOF 退出所以测试不暴露此 bug。
- 实现方案：在 `RunAgentLoop` 的 `while` 条件追加 `&& !IsGameExited()`，新增私有 helper `IsGameExited()` 判定 `console.State is ConsoleState.Quit or ConsoleState.Error`。检查放在循环顶部而非末尾 `FlushBuffer` 后，避免游戏结束后再做一次无意义的 `Poll`，并顺带覆盖 `Initialize` 失败（state 已是 Error）的首轮退出场景。未调用 `Stop()`，让循环条件自然失败，保留 `Stop()` 给 VT Ctrl+C 和 pipe EOF 路径专用。
- 验收：
  - 交互 CLI 下选择退出按钮后进程自然结束。
  - 游戏错误状态下 CLI 不继续等待输入。
  - pipe CLI 现有行为保持不变（`run_all.py` 4 套件全绿：JSONL+buttons 30/30、CLI 8/8、server single-session 24/24、TINPUT timeout 14/14）。

### T-021：RunEmueraProgram 脚本死循环保护

- 状态：已实现
- 范围：`Emuera/Runtime/Script/Process.cs`、`Emuera/Runtime/Script/Process.ScriptProc.cs`
- 说明：`Process.DoScript()` 内部指令循环遇到 ERB 死循环（如 `WHILE 1 \n WEND`）永不返回，所有调用 `RunEmueraProgram` 的路径卡死。原 `checkInfiniteLoop()` 依赖 `state.lineCount % 10000 == 0` 触发，但 `JumpTo()` 额外递增 `lineCount` 导致该条件永不成立；且 Headless 模式下 `HeadlessDialog.ShowPrompt()` 始终返回 false（auto-select: No），脚本无限继续。
- 实现方案：
  - `runScriptProc()` 中新增独立迭代计数器 `loopIterCount` 替代 `state.lineCount % 10000`，每 10000 次迭代调用 `checkInfiniteLoop()`
  - `checkInfiniteLoop()` 增加 `#if HEADLESS` 分支：超时后输出 `[script-timeout]` 日志到 stderr，抛出 `GameExitException` 终止脚本
  - `GameExitException` 沿已有传播链穿透至 `Session.GameLoopAsync`/`HeadlessRunner.RunAsync` 的 catch + finally，触发正常清理（BuildFinalTurn / IO.Close / GlobalStatic.Reset）
  - WinForms 路径保持原有 `Dialog.ShowPrompt()` 交互行为不变
- 超时阈值：`Config.InfiniteLoopAlertTime`（默认 5000ms），可通过 emuera.config 配置，设为 0 禁用
- 验收：
  - `WHILE 1 \n WEND` ERB 脚本在 Server 模式 ~5.6s 内终止，stderr 输出 `[script-timeout]` 日志
  - 全部 94 项回归测试通过（JSONL/CLI/Server/TINPUT/I-11）
  - WinForms 构建不受影响（`#if HEADLESS` 条件编译隔离）

### T-025：T-023 前置 — Emuera.Headless 文件结构整理

- 状态：已实现（2026-07-02）
- 范围：`Emuera.Headless/` 自有源码目录结构整理（13 个文件移动 + csproj 注释更新）
- 说明：T-023 物理迁移共享源码前的前置整理 PR，理顺 `Emuera.Headless/` 自有源码目录结构，为 T-023 的 `Shared/` 隔离方案预留边界。详见 [T-023 前置-文件结构整理方案](2026.6.30.架构健壮性重构/T-023前置-文件结构整理方案.md)。
- 实现方案：
  - 新建 `Emuera.Headless/Headless/` 目录，从根目录移入 `HeadlessClipboard.cs`/`HeadlessDialog.cs`/`HeadlessSound.cs`/`HeadlessStringMeasure.cs`，从 `UI/Game/` 移入 `HeadlessConsole.cs`（5 个 Headless 替换实现）
  - 新建 `Emuera.Headless/Terminal/` 目录，从 `Agent/` 移入 8 个终端渲染文件：`AgentCliVtInput.cs`、`AgentCliVtScreen.cs`、`CountdownRenderer.cs`、`TerminalCursor.cs`、`TerminalDisplayWidth.cs`、`TerminalRenderer.cs`、`VtParser.cs`、`Win32ConsoleInterop.cs`
  - 全部用 `git mv` 保留文件历史；namespace 声明与 using 引用保持不变（物理目录与 namespace 不完全一致，长期一致性留待 T-023 后统一评估）
  - `Emuera.Headless.csproj` 第 42 行注释更新：`UI/Game/` → `Headless/`，并去掉 `（I-11）` 标记
  - 13 个文件原本都由 SDK 默认 glob 包含，移动后仍由 SDK 默认 glob 包含，无 csproj 结构性变更
  - `CLAUDE.md` 已预先描述目标态目录结构（line 96-106），无需修改；`docs/TODO.md` 中 T-025 状态字段更新为已实现
- 不纳入范围：
  - `Server/`（T-024 处理 `ConsoleOutIO.cs`）
  - `UI/Game/Console/`、`UI/Game/EmueraConsole.cs`（留原处）
  - `Shared/` 目录创建（属 T-023）
  - `.editorconfig` 调整（属 T-023）
- 验收：
  - `dotnet build Emuera.Headless/Emuera.Headless.csproj -c Debug` 0 error，132 warnings（基线范围，警告源为已存在代码，路径变为新位置但无新增警告）
  - `run_all.py` 全部回归测试通过（JSONL+buttons、CLI、server single-session、TINPUT timeout、I-11 exit survival 5 个 suite 全 PASS）
  - `git diff --stat -M` 显示 13 个文件 100% rename 检测、0 行逻辑改动；仅 csproj 注释 1 处 + TODO.md 状态字段
  - build 警告输出已包含新路径（`Emuera.Headless/Headless/HeadlessClipboard.cs`、`Emuera.Headless/Terminal/AgentCliVtScreen.cs` 等），证明 13 个文件在新位置被正确编译
- 参考：
  - [T-023 前置-文件结构整理方案](2026.6.30.架构健壮性重构/T-023前置-文件结构整理方案.md)
  - 解锁 T-023

### T-023：I-01 简化 — 物理迁移共享源码到 Emuera.Headless/Shared

- 状态：已实现（2026-07-02）
- 范围：`Emuera/Runtime/`、`Emuera/UI/Game/`、`Emuera/UI/FontFactory.cs`、`Emuera/Properties/lang/`、`Emuera.Headless/Shared/`、`Emuera.Headless/Emuera.Headless.csproj`、`Emuera/Emuera.csproj`、根 `Emuera.sln`、根 `.editorconfig`、`CLAUDE.md`、`docs/TODO.md`
- 说明：替代原 I-01"抽 `Emuera.Core` 类库"方案。鉴于 WinForms 项目不再维护，双向兼容约束消失，改为物理迁移共享源码到 `Emuera.Headless/Shared/` 下，与 Headless 自有源码目录隔离，便于 T-022 按路径分级。110 个文件用 `git mv` 保留历史，namespace 声明与 using 引用保持不变。
- 实现方案：
  - 新建 `Emuera.Headless/Shared/` 子树
  - `git mv Emuera/Runtime/ Emuera.Headless/Shared/Runtime/`（整目录迁移），再把 5 个 WinForms/音频专用文件移回 `Emuera/` 原处：`Utils/Sound.WMP.cs`、`Utils/Sound.NAudio.cs`、`Utils/NAudio_LoopStream.cs`、`Utils/WinInput.cs`、`Script/Statements/Clipboard.cs`
  - `git mv` 19 个 UI/Game 共享文件到 `Shared/UI/Game/`（12 个根文件 + 7 个 Image/ 文件）；`EmueraConsole.cs`/`EmueraConsole.Print.cs`/`WinFormsConsole.cs`/`StringMeasure.cs`/`HotkeyState.cs`/`Rikaichan*.cs` 留在 `Emuera/` 原处
  - `git mv Emuera/UI/FontFactory.cs Emuera.Headless/Shared/UI/FontFactory.cs`
  - `git mv Emuera/Properties/lang/ Emuera.Headless/Shared/Properties/lang/`（2 个 xml）
  - 简化 `Emuera.Headless.csproj`：删除所有 `<Compile Include="..\Emuera\...">` glob 与 `<Compile Remove="...">` 排除项，改由 SDK 默认 glob（`**\*.cs`）包含；`<EmbeddedResource>` 路径改为 `Shared\Properties\lang\*.xml`（保留 `LinkBase="Properties\lang"` 维持资源名稳定，SDK 默认 glob 不含 `.xml` 必须显式包含）
  - 根 `.editorconfig`：`[Emuera/**]` 段迁移为 `[Emuera.Headless/Shared/**]`，22 个 CA 规则与 CS0472/CS0649/CS0162/CS0164/SYSLIB0014 抑制规则不变；头部注释同步更新
  - `git rm Emuera/Emuera.csproj`（`Emuera/Emuera.sln` 此前已在 I-17 commit `e4e6e87` 删除）
  - 根 `Emuera.sln` 移除 Emuera 与 EmueraPluginExample 项目声明及配置段，简化为仅 `Emuera.Headless` + `Debug|Any CPU` / `Release|Any CPU`（移除 x64/x86/NAudio 等 Emuera 专属配置）
  - `CLAUDE.md` 更新：`.editorconfig` 路径说明、删除 WinForms 构建命令（csproj 已删）、项目架构段、`Shared/` 目录描述、warning 基线 140→132
- 不纳入范围：
  - `Emuera/` 目录下 WinForms 专用文件（MainWindow/Forms/WinFormsConsole/Sound.WMP/Libs/Interop.WMPLib.dll 等）保留原处作只读参考
  - 不启用 `Nullable enable`/`TreatWarningsAsErrors`（属 T-022，本任务为其解锁前置）
  - namespace 与物理目录一致性留待后续 PR 评估
- 验收：
  - `dotnet build Emuera.Headless/Emuera.Headless.csproj -c Debug` 0 error，132 warnings（与迁移前基线完全一致，证明 `.editorconfig` 抑制规则正确迁移）
  - `Emuera.Headless.csproj` 中不再出现 `..\Emuera\` 路径引用
  - `Emuera.Headless/Shared/` 目录存在且包含迁移的历史源码（Runtime/UI/Game/FontFactory/lang）
  - 根 `.editorconfig` 中 `[Emuera/**]` 段已迁移为 `[Emuera.Headless/Shared/**]`
  - `run_all.py` 全部回归测试通过（JSONL+buttons、CLI、server single-session、TINPUT timeout、I-11 exit survival 5 个 suite 全 PASS，exit 0）
  - `Emuera/` 目录下不再有 `.csproj`/`.sln`
- 参考：
  - [Emuera.Headless 剩余问题与行动方案 — T-023](2026.6.30.架构健壮性重构/Emuera.Headless%20剩余问题与行动方案.md#t023)
  - [T-023 前置-文件结构整理方案](2026.6.30.架构健壮性重构/T-023前置-文件结构整理方案.md)
  - 解决 I-01、I-17；解锁 T-022（I-12 阶段 2）、I-11、I-14

### T-024：废弃 CLI/JSONL 管道模式入口

- 状态：已实现（2026-07-02）
- 范围：`Emuera.Headless/Server/ConsoleOutIO.cs`、`Emuera.Headless/Agent/AgentJsonlProtocol.cs`、`Emuera.Headless/Agent/AgentCliProtocol.cs`、`Emuera.Headless/HeadlessRunner.cs`、`Emuera.Headless/HeadlessOptions.cs`、`tests/test_jsonl.py`、`tests/test_cli.py`、`tests/emuera_agent.py`、`tests/emuera_server.py`、`tests/run_all.py`、`tests/README.md`、`CLAUDE.md`、根 `README.md`、`docs/TODO.md`
- 说明：CLI 管道模式（stdin pipe）与 JSONL 管道模式（stdin/stdout）在实际使用中无用途。移除管道入口与 `ConsoleOutIO`，收窄维护与测试矩阵。`AgentJsonlProtocol` 保留（Server 模式通过 `HttpSessionIO` 仍依赖它），仅删除依赖 `ConsoleOutIO` 的默认构造函数。
- 实现方案：
  - 删除 `Emuera.Headless/Server/ConsoleOutIO.cs`（stdin/stdout 封装，仅管道模式使用）
  - `AgentJsonlProtocol.cs`：删除依赖 `ConsoleOutIO.Instance` 的默认构造函数 `(console, ui)`，仅保留 `(console, ui, SessionIO io)` 三参构造函数（Server 模式 `Session.cs` 使用）
  - `HeadlessRunner.cs`：
    - `SelectProtocol` 移除 `"jsonl"` 分支（改为抛 `ArgumentException` 提示已废弃）
    - `DetectProtocol` 移除 `Console.IsInputRedirected` 的 stdin 管道检测分支，改为检测到 stdin 重定向时输出废弃提示并返回 null
    - `RunAsync` 移除 `AgentJsonlProtocol.RunLoopAsync` 调用分支，仅保留 `AgentCliProtocol.RunCliLoop`
    - 错误提示更新为指向 `--server` 或 `--protocol cli`
  - `HeadlessOptions.cs`：`ProtocolOption` 描述更新为 `auto(默认,检测终端), cli`，提示 stdin 管道已废弃
  - `AgentCliProtocol.cs`：
    - `RunCliLoop` 移除 `Console.IsInputRedirected` 分支与 `PipeLoopStrategy` 调用，仅保留 `TryRunVtLoop()` → `ConsoleKeyLoopStrategy` 降级路径
    - 删除 `PipeLoopStrategy` 内部类（stdin 逐行读取策略）
  - 测试矩阵收窄：
    - `tests/test_jsonl.py` 改为通过 `emuera_server.start_server` 驱动，断言逻辑（buttons schema、stale 按钮排除、状态流转）保持不变
    - 删除 `tests/test_cli.py`（stdin 管道 CLI 模式已移除；交互式 CLI 无法在无 TTY 的 CI 环境自动化）
    - 删除 `tests/emuera_agent.py`（`EmueraAgent` 类依赖 stdin 管道 JSONL），`find_binary` 函数迁移到 `tests/emuera_server.py`
    - `tests/run_all.py` 移除 CLI protocol suite 与 `--skip-cli-protocol` 选项，更新 `find_binary` 导入源
  - 文档更新：`tests/README.md`、`CLAUDE.md`、根 `README.md` 同步反映 stdin 管道废弃、测试矩阵变化、运行命令更新
- 不纳入范围：
  - `AgentJsonlProtocol` 核心协议逻辑（`RunLoopAsync`/`StepAsync`/`BuildTurn` 等，Server 模式依赖，不改动）
  - `HttpSessionIO`（Server 模式专用，不改动）
  - `enableTimeout: false` 调用路径虽已无调用方（原由 HeadlessRunner stdin 管道使用），但为保持 `RunLoopAsync` 签名稳定未删除（死代码，后续可清理）
- 验收：
  - `dotnet build Emuera.Headless/Emuera.Headless.csproj -c Debug` 0 error，131 warnings（基线范围）
  - `ConsoleOutIO.cs` 已删除，`Emuera.Headless/` 中无 `ConsoleOutIO`/`PipeLoopStrategy` 引用
  - `run_all.py` 回归测试全绿（JSONL+buttons、server single-session、TINPUT timeout、I-11 exit survival 4 个 suite 全 PASS）
- 参考：
  - [Emuera.Headless 剩余问题与行动方案 — T-024](2026.6.30.架构健壮性重构/Emuera.Headless%20剩余问题与行动方案.md)
  - 收窄测试矩阵，为 I-09 单测项目减负

### T-026：CLI 装饰线 ANSI 增强 + 图片/矩形空格占位

- 状态：已实现（2026-08-07）
- 范围：`Emuera.Headless.Core/Terminal/TerminalLineFormatter.cs`、新增 `Emuera.Headless.Tests/TerminalLineFormatterTests.cs`
- 说明：两个 CLI 渲染待议问题的落地（TDD 红-绿，先写 10 个单测再改实现）：
  - **① 下划线/删除线**：`FormatLineWithAnsi` 原只输出粗体 `\x1b[1m`/斜体 `\x1b[3m`（T-010 有意范围），补 `EmuFontStyle.Underline` → `\x1b[4m`、`Strikeout` → `\x1b[9m`。PRINT_SLIDER 滑条（ERB FONTSTYLE 位4 = 内部 Strikeout）在 CLI 由「完全消失」变为「删除线可见」，终端删除线位置 ≈60% 字符高度，与 winforms TextRenderer 一致。`\x1b[0m` 重置已覆盖，老终端仅静默不显示、不破坏布局。
  - **② 图片/矩形占位**：`BuildTerminalLine` 与 `FormatLineWithAnsi` 对 `ConsoleImagePart`/`ConsoleRectangleShapePart` 原直接 break 丢弃（DisplayState.cs 注释的有意跳过语义），现复用 `ConsoleSpacePart` 同公式 `Math.Max(node.Width / charWidth, 0)` 补空格占位。`charWidth = Config.FontSize / 2`。占位计入 textWidth，顺带修正：居中/右对齐 padding、`ComputeAlignOffset`（按钮鼠标命中区列偏移）、`RebuildButtonPositions` 按钮列位置——均以 `BuildTerminalLine` 为单一宽度源头，天然一致。
- 决策记录：
  - 占位宽度用 `node.Width`（流推进列数）而非矩形绝对坐标 `Rect.X + Rect.Width`——与 space 先例自洽，避免把绝对定位语义引入 CLI 流式模型。
  - 超宽（图片 > 行宽）不 clamp，终端自然换行，与 winforms 横向超宽语义一致。
- 验收：
  - 新增 `TerminalLineFormatterTests` 13/13 绿（4m/9m 单独与组合、段间/行尾 reset、bold/italic 不回归、图片/矩形 build 与 ansi 双路径占位、0 宽不占位、占位流入居中/右对齐 padding、**ComputeAlignOffset 与 FormatLineForTerminal 前导空格一致**、**ANSI 中段占位空格 reset 装饰线**——后两项为 code-review 后补的联动锁定测试）。
  - 完整套件 667/667 全绿（含新增 13 个）。
- 后续修订（2026-08-07 code-review 三修复）：
  - **联动测试补缺**：RIGHT 对齐、ComputeAlignOffset（经 BuildSnapshot，锁"与 FormatLineForTerminal 一致"契约）、中段样式 reset 三个用例，把"单一宽度源"从结构保证升级为测试保证。
  - **中段样式继承修复**：`FormatLineWithAnsi` 图片/矩形分支在有活动样式时先 `\x1b[0m` 并清空 last 镜像（保留选中反显 `\x1b[7m`），占位空格不再继承前段 4m/9m，且后续样式段能正确重开样式码。
  - **过时注释同步**：`TerminalRenderer.cs`（"CLI 仍跳过图片/矩形"）与 `DisplayState.cs`（"保留跳过语义"）更新为占位语义。
