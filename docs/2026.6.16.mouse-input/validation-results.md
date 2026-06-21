# 鼠标输入验证结果

## 2026-06-18 更新：Windows Terminal + PowerShell 7.6.2

本次更新重新验证了 SGR stdin 路径。结论是：在 Windows Terminal + PowerShell 7.6.2 下，`Console.ReadKey()` 和 raw stdin 都能读取 XTerm SGR mouse；但 raw stdin 必须进入 raw-ish console input mode，否则终端会回显原始 SGR 序列，导致误判。

### 经验总结

- 只写入 XTerm mouse `1000h + 1006h` 不足以让 SGR mouse 稳定进入 .NET Console 输入路径；Windows 下还需要开启 `ENABLE_VIRTUAL_TERMINAL_INPUT`。
- `Console.ReadKey(true)` 会把 SGR mouse 序列拆成多个字符输入，因此必须维护 pending buffer 重组 `ESC [ < Cb ; Cx ; Cy M/m`。
- raw stdin 验证必须配合 stderr 重定向；终端屏幕是否回显 `^[[<...` 是判断 echo / line input 是否关闭的重要信号。
- raw stdin 在 Windows 下需要 raw-ish console input mode：开启 `ENABLE_VIRTUAL_TERMINAL_INPUT`，关闭 `ENABLE_PROCESSED_INPUT` / `ENABLE_ECHO_INPUT` / `ENABLE_LINE_INPUT` / `ENABLE_QUICK_EDIT_MODE`。
- raw mode 下 `Ctrl+C` 应作为 stdin 字节 `0x03` 处理，而不是依赖 `Console.CancelKeyPress` 打断阻塞的 `Read()`。
- 退出路径和输入路径同等重要；必须验证 `finally` 中会关闭 mouse tracking 并恢复 console input mode。
- Windows 生产路径仍建议优先考虑 Win32 `ReadConsoleInput()`；SGR stdin parser 可作为非 Windows 路径、已验证终端路径或备选方案。

### 步骤 1 — `readkey-mouse` 重新验证

命令：

```powershell
dotnet run --project docs/2026.6.16.mouse-input/spikes/readkey-mouse/readkey-mouse.csproj -c Debug --no-build
```

观察到的行为：

- 程序启用了 `ENABLE_VIRTUAL_TERMINAL_INPUT`。
- 程序启用了 XTerm mouse `1000h + 1006h`。
- 点击窗口左上角后，`Console.ReadKey(true)` 按字符读到了完整 SGR mouse 按下序列。
- 程序通过 pending buffer 成功解析为 `left-down row=0 col=0`。
- 点击释放事件被识别为 release sequence，并按当前 MVP 策略忽略。
- 不再把 `Esc` 作为退出键，因为 `ESC` 是 SGR mouse 序列前缀的一部分。

关键输出：

```text
[readkey-mouse] enabled ENABLE_VIRTUAL_TERMINAL_INPUT.
[readkey-mouse] enabled XTerm mouse 1000h + 1006h
[readkey-mouse][key] Key=None, KeyChar=27, ch=<ESC>
[readkey-mouse][key] Key=None, KeyChar=91, ch='['
[readkey-mouse][key] Key=None, KeyChar=60, ch='<'
[readkey-mouse][key] Key=None, KeyChar=48, ch='0'
[readkey-mouse][key] Key=None, KeyChar=59, ch=';'
[readkey-mouse][key] Key=None, KeyChar=49, ch='1'
[readkey-mouse][key] Key=None, KeyChar=59, ch=';'
[readkey-mouse][key] Key=None, KeyChar=49, ch='1'
[readkey-mouse][key] Key=None, KeyChar=77, ch='M'
[readkey-mouse][mouse] left-down row=0 col=0
[readkey-mouse][mouse] ignored release sequence: <ESC>[<0;1;1m
```

结论：

- 步骤 1 的鼠标输入验证通过。
- `Console.ReadKey(true)` 可以读取 XTerm SGR mouse，但会把序列拆成多个字符输入。
- 该路径适合作为 spike / 验证手段；如果生产化，需要明确 pending buffer、退出键和输入 reader 架构，不能与 raw stdin / Win32 console input 混用。

### 步骤 2 — `raw-stdin-mouse` 重新验证

命令：

```powershell
dotnet run --project docs/2026.6.16.mouse-input/spikes/raw-stdin-mouse/raw-stdin-mouse.csproj -c Debug --no-build 2> raw.log
```

观察到的行为：

- 终端屏幕没有回显 `^[[<0;1;1M...`，说明 raw-ish console input mode 生效。
- stderr 日志文件中出现了完整原始 byte 日志。
- 程序成功解析左上角点击为 `left-down row=0 col=0`。
- 程序成功识别释放事件，并按当前 MVP 策略忽略。
- 按 `Ctrl+C` 后，程序读取到 `0x03` 字节并退出。
- `finally` 中成功关闭 XTerm mouse tracking，并恢复 console input mode。

`raw.log` 关键输出：

```text
[raw-stdin-mouse] enabled raw console input mode: VT input on, processed/echo/line input off.
[raw-stdin-mouse] enabled XTerm mouse 1000h + 1006h
[raw-stdin-mouse] click in the terminal. Press Ctrl+C to quit.
[raw-stdin-mouse] SGR mouse example: ESC [ < 0 ; X ; Y M
[raw-stdin-mouse][byte] 0x1B
[raw-stdin-mouse][byte] 0x5B
[raw-stdin-mouse][byte] 0x3C
[raw-stdin-mouse][byte] 0x30
[raw-stdin-mouse][byte] 0x3B
[raw-stdin-mouse][byte] 0x31
[raw-stdin-mouse][byte] 0x3B
[raw-stdin-mouse][byte] 0x31
[raw-stdin-mouse][byte] 0x4D
[raw-stdin-mouse][mouse] left-down row=0 col=0
[raw-stdin-mouse][byte] 0x1B
[raw-stdin-mouse][byte] 0x5B
[raw-stdin-mouse][byte] 0x3C
[raw-stdin-mouse][byte] 0x30
[raw-stdin-mouse][byte] 0x3B
[raw-stdin-mouse][byte] 0x31
[raw-stdin-mouse][byte] 0x3B
[raw-stdin-mouse][byte] 0x31
[raw-stdin-mouse][byte] 0x6D
[raw-stdin-mouse][mouse] ignored release sequence: <ESC>[<0;1;1m
[raw-stdin-mouse] Ctrl+C byte received; exiting.
[raw-stdin-mouse] disabled XTerm mouse 1000h + 1006h
[raw-stdin-mouse] restored console input mode
```

结论：

- 步骤 2 的 raw stdin 鼠标输入验证通过。
- Windows Terminal + PowerShell 7.6.2 下，raw stdin 可以读取完整 SGR mouse bytes。
- raw stdin 成功的关键不是单纯开启 mouse tracking，而是同时设置 raw-ish console input mode。
- 该路径可以作为 SGR stdin parser 的可行证明；但生产化时仍需避免与 `Console.ReadKey()` 或 Win32 `ReadConsoleInput()` 混用。

### 更新后的决策

- Windows Terminal + PowerShell 下，`Console.ReadKey()`、raw stdin 和 Win32 `ReadConsoleInput()` 都已验证可用。
- Windows 生产路径仍建议优先使用 Win32 `ReadConsoleInput()`，因为它不依赖 XTerm SGR stdin 解析，且与当前 `AgentCliProtocol` smoke / MVP 路径一致。
- SGR stdin parser 可作为非 Windows 路径、已验证终端路径或备选方案保留。
- 后续重点是生产化清理：debug 输出策略、输入 backend 选择、ANSI / 全角 / 自动换行坐标精度。

## 2026-06-16 历史初测 — Windows PowerShell / 类似 Windows Terminal 的环境

> 以下记录是早期未开启 `ENABLE_VIRTUAL_TERMINAL_INPUT` / 未进入 raw-ish console input mode 时的初测结果。2026-06-18 重新验证后，Step 1 和 Step 2 的结论已更新为通过。

报告环境：

```text
(base) PS D:\LaoBro\Emuera.MCP>
```

### 步骤 1 — `readkey-mouse`

命令：

```powershell
dotnet run --project docs/2026.6.16.mouse-input/spikes/readkey-mouse/readkey-mouse.csproj -c Debug --no-build
```

观察到的行为：

- 程序启用了 XTerm 鼠标追踪。
- 鼠标选区 / 快速选区行为发生了变化，说明终端接受了鼠标追踪模式。
- 鼠标点击没有产生任何输出。
- 按 `Esc` 能被正确读取并退出程序。
- 程序在 `finally` 中禁用了鼠标追踪。

输出：

```text
[readkey-mouse] enabled XTerm mouse 1000h + 1006h
[readkey-mouse] click in the terminal. Press Esc to quit.
[readkey-mouse] SGR mouse example: ESC [ < 0 ; X ; Y M
[readkey-mouse][key] Key=Escape, KeyChar=27, ch=<ESC>
[readkey-mouse] disabled XTerm mouse 1000h + 1006h
```

结论：

- `Console.ReadKey(true)` 可以读取键盘输入。
- 在此环境中，`Console.ReadKey(true)` 不会把 XTerm SGR 鼠标点击暴露为可读的字符输入。
- 步骤 1 的鼠标输入验证失败。

### 步骤 2 — `raw-stdin-mouse`

命令：

```powershell
dotnet run --project docs/2026.6.16.mouse-input/spikes/raw-stdin-mouse/raw-stdin-mouse.csproj -c Debug --no-build
```

观察到的行为：

- 程序启用了 XTerm 鼠标追踪。
- 鼠标点击没有产生输出。
- 键盘输入似乎没有传递给 raw stdin 读取器。
- 按 `Esc` 没有退出。
- `Ctrl+C` 退出了进程。
- 程序在 `finally` 中禁用了鼠标追踪。

输出：

```text
[raw-stdin-mouse] enabled XTerm mouse 1000h + 1006h
[raw-stdin-mouse] click in the terminal. Press q or Esc to quit.
[raw-stdin-mouse] SGR mouse example: ESC [ < 0 ; X ; Y M
[raw-stdin-mouse] disabled XTerm mouse 1000h + 1006h
```

结论：

- 在此环境中，原始的 `Console.OpenStandardInput().Read()` 没有收到鼠标事件。
- 启用鼠标追踪后，原始 stdin 也没有收到普通键盘输入。
- 步骤 2 在此早期配置中失败，或结论不明确。
- 后续 2026-06-18 重新验证表明，失败原因主要是未开启 `ENABLE_VIRTUAL_TERMINAL_INPUT`，且未进入 raw-ish console input mode。

### 当前决策

该决策已被 2026-06-18 的重新验证结果更新。早期结论不再代表最终状态。

当时的下一步建议验证：

- 增加一个 Win32 `ReadConsoleInput()` 验证程序。
- 确认是否可以通过原生 Windows 控制台输入 API 获得 `MOUSE_EVENT_RECORD` 形式的鼠标事件。
- 如果原生控制台输入可用，Windows 实现路径应该优先使用 Win32 控制台输入，而不是解析 XTerm SGR stdin。
- 如果原生控制台输入也失败，则除非选择其他终端/输入策略，否则 Windows 鼠标支持仍应仅保留键盘回退方案。

### 步骤 2B — Win32 `ReadConsoleInput()` 原生控制台输入

命令：

```powershell
dotnet run --project docs/2026.6.16.mouse-input/spikes/win32-console-input-mouse/win32-console-input-mouse.csproj -c Debug --no-build
```

```powershell
dotnet run --project docs/2026.6.16.mouse-input/spikes/win32-console-input-mouse/win32-console-input-mouse.csproj -c Debug --no-build -- --enable-xterm
```

第一条命令观察到的行为：

- 鼠标移动产生了输出。
- 鼠标按下产生了输出。
- 鼠标释放产生了输出。
- 键盘按键按下产生了输出。
- 键盘按键释放产生了输出。
- 这表明原生 Windows 控制台输入可以同时接收鼠标事件和键盘事件。

第二条带 `--enable-xterm` 的命令观察到的行为：

- 鼠标按下/释放和键盘事件的行为基本相同。
- 鼠标移动没有产生输出。
- 这对 XTerm `1000h` 来说是预期行为：它只报告按钮事件，不报告普通鼠标移动。移动报告需要额外的模式，例如 `1002h` 或 `1003h`，这超出了当前按钮点击 MVP 的范围。

结论：

- 步骤 2B 的原生 Win32 控制台输入验证通过。
- Windows 实现建议优先使用 Win32 `ReadConsoleInput()`，因为它不依赖 XTerm SGR stdin 解析。
- Windows MVP 不必须启用 XTerm SGR 模式；该模式会改变终端输入报告方式，因此生产化前需要明确 backend 选择。

### 2026-06-16 当时决策

基于当时的 Step 1 / Step 2 初测结果，只有当 Windows 路径基于 Win32 `ReadConsoleInput()`，而不是基于 SGR stdin 解析时，才继续推进 `AgentCliProtocol` smoke test。

该决策已被 2026-06-18 的重新验证结果更新：SGR stdin 路径已被证明可行，但 Windows 生产路径仍建议优先使用 Win32 `ReadConsoleInput()`。

Windows 方面：

- 使用 `ReadConsoleInput()` 读取 `MOUSE_EVENT_RECORD` 和 `KEY_EVENT_RECORD`。
- 将原生鼠标坐标转换为与 SGR 解析器相同的内部 0 基终端坐标。
- 将 XTerm SGR 解析保留为非 Windows 路径，或用于已单独验证过的终端。
- 鼠标按钮点击 MVP 可在 `EMUERA_ENABLE_MOUSE_CLICK=1` 后启用；调试输出应保持为显式开启。

### 步骤 3 — `AgentCliProtocol` Win32 smoke test

命令：

```powershell
$env:EMUERA_DEBUG_MOUSE='1'
dotnet exec D:/LaoBro/Emuera.MCP/Emuera.Headless/bin/Debug/net10.0/Emuera.Headless.dll --ExeDir D:/LaoBro/Emuera.MCP/test_game --protocol cli
```

```powershell
$env:EMUERA_DEBUG_MOUSE='1'
$env:EMUERA_DEBUG_MOUSE_VERBOSE='1'
dotnet exec D:/LaoBro/Emuera.MCP/Emuera.Headless/bin/Debug/net10.0/Emuera.Headless.dll --ExeDir D:/LaoBro/Emuera.MCP/test_game --protocol cli
```

观察到的行为：

- 鼠标按下和释放事件被传递到了 `AgentCliProtocol`。
- 鼠标事件以如下形式打印到 `Console.Error`：

```text
[AgentCliProtocol][mouse-smoke] left-down row=14 col=18 buttonState=0x00000001 eventFlags=0x00000000
[AgentCliProtocol][mouse-smoke] left-up row=14 col=18 buttonState=0x00000000 eventFlags=0x00000000
```

- 详细模式命令打印了 `eventFlags=0x00000001` 的鼠标移动事件。
- 部分 stdout/stderr 输出顺序出现了交错或延迟。
- 在这次 smoke run 中，键盘输入似乎没有被处理。

步骤 3 中发现的问题：

- 鼠标动作标签对右键和移动事件具有误导性。
- 键盘事件转换需要更多调试和回退映射。
- smoke 初始化消息写入了 `Console.Error`，在 PowerShell/捕获输出中可能与 stdout 交错。

后续已完成的修改：

- 增加 `EMUERA_DEBUG_MOUSE_KEYS=1` 用于记录按键事件。
- 为键盘事件增加虚拟键到字符的回退映射。
- 修正移动、右键和左键释放的鼠标动作标签。
- 在 smoke 初始化消息后增加控制台刷新。
- 使用 `dotnet build Emuera.Headless/Emuera.Headless.csproj -c Debug -v:q` 成功重新构建。

第一次后续运行后的观察到的行为：

- 鼠标滚轮事件被传递：

```text
[AgentCliProtocol][mouse-smoke] wheel row=12 col=96 buttonState=0x00800000 eventFlags=0x00000004
```

- 鼠标左键按下/释放事件被传递：

```text
[AgentCliProtocol][mouse-smoke] left-down row=15 col=65 buttonState=0x00000001 eventFlags=0x00000000
[AgentCliProtocol][mouse-smoke] left-up row=15 col=65 buttonState=0x00000000 eventFlags=0x00000000
```

- 详细模式命令传递了鼠标移动事件：

```text
[AgentCliProtocol][mouse-smoke] move row=21 col=45 buttonState=0x00000000 eventFlags=0x00000001
```

- 左键释放后观察到额外的右键/按钮释放事件。这些被视为非 MVP 噪声，应由按钮点击路径忽略。
- `EMUERA_DEBUG_MOUSE_KEYS=1` 确认按键事件会被传递，但 `wVirtualKeyCode` 被报告为 `0x0001`，而 `UnicodeChar` / `ConsoleKeyInfo.KeyChar` 对字母是正确的。

本次观察后的后续修改：

- 在默认 smoke 日志中屏蔽非左键噪声：
  - 滚轮
  - 移动
  - 右键
  - 没有前置左键按下记录的通用按钮释放
- 将 `KEY_EVENT_RECORD` 改为显式字段偏移，以修正 `wVirtualKeyCode`。
- 使用 `dotnet build Emuera.Headless/Emuera.Headless.csproj -c Debug -v:q /clp:ErrorsOnly` 成功重新构建。

最新后续运行后的观察到的行为：

- 默认鼠标 smoke 现在只显示左键按下/左键释放，并保留键盘行为。
- 上/下方向键仍然可以控制按钮提示。
- `Enter` 仍然可以确认选中的按钮。
- `Esc` 仍然可以清空输入缓冲区。
- `EMUERA_DEBUG_MOUSE_KEYS=1` 显示方向键、数字、Escape 和 Enter 的按键事件都正确。

本次观察后的后续修改：

- 在 `EMUERA_ENABLE_MOUSE_CLICK=1` 后增加最小按钮点击 MVP。
- 在 `EMUERA_DEBUG_MOUSE_REGIONS=1` 后增加按钮区域记录。
- 在已记录按钮区域内左键按下时，现在会派发按钮输入。
- 使用 `dotnet build Emuera.Headless/Emuera.Headless.csproj -c Debug -v:q /clp:ErrorsOnly` 成功重新构建。

下一步验证：

- 运行最小按钮点击 MVP：

```powershell
$env:EMUERA_DEBUG_MOUSE='1'
$env:EMUERA_ENABLE_MOUSE_CLICK='1'
dotnet exec D:/LaoBro/Emuera.MCP/Emuera.Headless/bin/Debug/net10.0/Emuera.Headless.dll --ExeDir D:/LaoBro/Emuera.MCP/test_game --protocol cli
```

- 可选：检查已记录的按钮区域：

```powershell
$env:EMUERA_DEBUG_MOUSE='1'
$env:EMUERA_ENABLE_MOUSE_CLICK='1'
$env:EMUERA_DEBUG_MOUSE_REGIONS='1'
dotnet exec D:/LaoBro/Emuera.MCP/Emuera.Headless/bin/Debug/net10.0/Emuera.Headless.dll --ExeDir D:/LaoBro/Emuera.MCP/test_game --protocol cli
```

预期 MVP 行为：

- 点击 `[0] Hello` → `You entered: 0`
- 点击 `[1] Quit` 或 `[1] Exit` → 提交 `1`
- 键盘回退方案仍然可用。

### 步骤 4 — 最小按钮点击 MVP

命令：

```powershell
$env:EMUERA_DEBUG_MOUSE='1'
$env:EMUERA_ENABLE_MOUSE_CLICK='1'
$env:EMUERA_DEBUG_MOUSE_REGIONS='1'
dotnet exec D:/LaoBro/Emuera.MCP/Emuera.Headless/bin/Debug/net10.0/Emuera.Headless.dll --ExeDir D:/LaoBro/Emuera.MCP/test_game --protocol cli
```

最终坐标修复后的观察到的行为：

- 初始菜单区域被记录为：

```text
[AgentCliProtocol][mouse-region] row=6 col=0-7 input=1 label=[1] Quit
[AgentCliProtocol][mouse-region] row=5 col=0-8 input=0 label=[0] Hello
```

- 在大约 `row=5 col=4` 处点击 `[0] Hello` 后提交了 `0`。
- 第二个菜单区域被记录为：

```text
[AgentCliProtocol][mouse-region] row=15 col=0-7 input=1 label=[1] Exit
[AgentCliProtocol][mouse-region] row=14 col=0-8 input=0 label=[0] World
```

- 在大约 `row=15 col=4` 处点击 `[1] Exit` 后提交了 `1`。
- 键盘 `↑/↓ + Enter` 仍然可用。
- 构建通过：

```text
已成功生成。
126 个警告
0 个错误
```

步骤 4 期间完成的坐标修复：

- 初始的从末尾映射假设最后一行显示行总是 `cursorTop - 1`。
- 该假设在按钮提示行渲染后失效，因为此时 `cursorTop` 位于提示行下方，而不是正好位于按钮列表下方。
- 使用 `cursorTop - 2` 的临时修复会让初始菜单过度修正，因为第一次区域捕获发生时提示还不存在。
- 最终修复是在 `RenderButtonPrompt()` 中记录实际提示行，并用该行定位按钮列表：
  - 如果存在提示行，则最后一个按钮行 = `promptRow - 1`
  - 否则，最后一个按钮行 = `cursorTop - 1`
- `FullRefresh()` 不再在 `SyncButtonState()` 渲染提示之前记录区域，因为这可能捕获到过期的提示行。

调试输出观察：

- 启用 `EMUERA_DEBUG_MOUSE=1` 时，鼠标 smoke 和区域日志会写入 `Console.Error`。
- PowerShell 终端显示可能会交错 `Console.Error` 和 `Console.Out`，产生视觉上混乱的行，例如提示或 PSReadLine 缓冲区的片段。
- 启用 stderr 调试日志时，这是终端显示中的预期现象。
- 底层游戏 stdout 仍然是干净的 CLI 输出；调试日志应重定向到文件，或后续改为生产验证用的显式日志文件。

结论：

- 步骤 4 在 Windows PowerShell 验证路径中通过。
- 剩余工作不是按钮命中逻辑，而是调试输出的生产环境清理。

### 步骤 5 — 坐标转换验证

坐标转换验证结果已拆分至独立文件：[`coordinate-validation-results.md`](coordinate-validation-results.md)。

验证结论：`viewportRow = mouseY - Console.WindowTop`、`viewportCol = mouseX - Console.WindowLeft` 在 Windows Terminal 和 conhost 下均已验证通过。
