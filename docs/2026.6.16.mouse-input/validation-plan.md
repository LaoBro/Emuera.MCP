# CLI 鼠标输入可行性验证计划

## 版本信息

- 关联规格：`docs/2026.6.16.mouse-input/spec-v1.1.md` / `docs/2026.6.16.mouse-input/spec-v1.2.md`
- 目标：用最小成本验证 CLI 鼠标输入是否可行
- 推荐路线：分平台 / 分层验证
  1. 先写最小 CLI spike 验证 `Console.ReadKey()` 是否能读到 SGR mouse 序列；该路径只作为验证手段，不作为生产输入 reader 的最终形态。
  2. 再验证 raw stdin bytes；Windows 下必须进入 raw-ish console input mode：开启 `ENABLE_VIRTUAL_TERMINAL_INPUT`，关闭 echo / line / processed / quick-edit 输入模式，并把 `Ctrl+C` 当作 `0x03` 处理。
  3. Windows 生产路径优先使用 Win32 `ReadConsoleInput()`；SGR stdin parser 保留为非 Windows 路径、已验证终端路径或备选路径。
  4. 底层输入可行后，再在 `AgentCliProtocol` 做只打印坐标的 smoke test。
  5. smoke test 稳定后，最后才实现按钮区域记录和点击提交。
- 当前状态：
  - Step 1 `Console.ReadKey()`：通过；Windows Terminal + PowerShell 下能读到 SGR mouse，但序列会按字符拆分，必须维护 pending buffer；退出应使用 `Ctrl+C`，不能把 `Esc` 当退出键。
  - Step 2 raw stdin：通过；Windows Terminal + PowerShell 下能读到完整 SGR mouse bytes；需要 raw-ish console input mode；退出时能恢复 console input mode。
  - Step 2B Win32 `ReadConsoleInput()`：通过。
  - Step 3 `AgentCliProtocol` smoke：通过；`Console.Error` debug 输出在 PowerShell 终端显示中会与普通 stdout 交错，这是预期现象。
  - Step 4 最小按钮区域 MVP：通过；`test_game` 中两轮按钮点击均能提交正确输入。
  - Step 5 坐标系转换可行性验证：已通过 prompt-anchored 命中/miss 主要路径；左上角/右下角边界点击仍待补测。
  - 待处理：debug 输出策略仍需生产化；生产输入 backend 仍需在 Win32 console input 与 SGR stdin parser 之间按目标平台做最终取舍。

## 本次验证经验总结

- 只写入 XTerm mouse `1000h + 1006h` 不足以让 SGR mouse 进入 .NET Console 输入路径；Windows 下还需要开启 `ENABLE_VIRTUAL_TERMINAL_INPUT`。
- `Console.ReadKey(true)` 会把 SGR mouse 序列拆成多个字符输入，因此必须维护 pending buffer 重组 `ESC [ < Cb ; Cx ; Cy M/m`。
- raw stdin 测试必须用 stderr 重定向观察；终端屏幕是否回显 `^[[<...` 是判断 echo/line input 是否关闭的重要信号。
- raw stdin 在 Windows 下需要 raw-ish console input mode：开启 VT input，关闭 echo / line / processed / quick-edit，并把 `Ctrl+C` 作为 `0x03` 字节处理。
- 退出路径和输入路径同等重要；必须验证 `finally` 中会关闭 mouse tracking 并恢复 console input mode。
- Windows 生产路径仍建议优先考虑 Win32 `ReadConsoleInput()`；SGR stdin parser 适合作为非 Windows 路径、已验证终端路径或备选方案。

## 验证目标

本计划最初只验证鼠标输入可行性，不实现完整按钮点击功能；后续 Step 4 已扩展为最小按钮区域 MVP 验证。

必须回答以下问题：

1. 在现代终端中开启 XTerm mouse `1000h + 1006h` 后，点击是否能进入程序输入流？
2. `Console.ReadKey(true)` 是否能稳定读到完整 SGR mouse 序列？
3. 如果 `Console.ReadKey()` 不稳定，raw stdin bytes 是否能读到完整 SGR mouse 序列？
4. 鼠标事件进入 `AgentCliProtocol` 主循环后，是否能不破坏现有键盘 CLI 行为？
5. 退出程序后，mouse tracking 是否能正确关闭，终端鼠标选择是否恢复？

## 非目标

原始可行性阶段不做：

- 完整按钮坐标映射
- 生产级 ANSI / 全角 / 自动换行处理
- 悬浮高亮、拖拽、滚轮、右键
- pipe 模式鼠标支持
- 生产级 input reader 重构

后续 Step 4 已覆盖最小按钮区域 MVP：只记录当前 generation、只处理左键按下、只提交按钮值。完整坐标精度、ANSI / 全角 / 自动换行等仍属于后续生产化范围。

---

## 环境矩阵

建议至少在以下环境中记录结果：

| 环境 | 是否验证 | 结果 | 备注 |
|---|---:|---|---|
| Windows Terminal + PowerShell | 是 | 通过 | 主要目标环境；Step 1 / Step 2 / Step 2B / Step 3 / Step 4 已通过 |
| Windows Terminal + VS Code Integrated Terminal | 待填 | 待填 | 常见开发环境 |
| Windows cmd.exe / conhost | 可选 | 待填 | 老式控制台行为可能不同 |
| Git Bash / mintty | 可选 | 待填 | 可能不是标准 Windows Console |
| Linux/macOS terminal | 可选 | 待填 | 非 Windows 下 `_ansiEnabled` 通常为 true；当前 Windows MVP 不依赖 SGR |
| redirected stdin/stdout | 是 | 通过 | pipe 模式未启用 mouse；现有 CLI 文本输入保持不变 |

---

## Step 0：准备验证记录方式

建议在 `docs/2026.6.16.mouse-input/` 下后续新增：

```text
validation-results.md
```

每次验证后记录：

- 日期
- OS / terminal / shell
- 使用方案：`Console.ReadKey()` / raw stdin / Win32 `ReadConsoleInput()` / `AgentCliProtocol smoke`
- 点击坐标示例
- 是否稳定
- 是否影响键盘输入
- 退出后终端是否恢复
- 结论：通过 / 失败 / 需进一步验证
- 若启用 `EMUERA_DEBUG_MOUSE`，记录 debug 输出是否只出现在 stderr / 日志文件；PowerShell 终端显示中 stdout 与 stderr 可能交错，不代表 stdout 流被业务输出污染

建议在需要观察区域坐标或 raw stdin bytes 时使用 stderr 重定向，避免终端显示交错，也避免把终端回显误判为程序输出：

```powershell
2> $env:TEMP\emuera-mouse-debug.log
```

raw stdin 验证时，终端屏幕不应再出现 `^[[<...`；如果终端屏幕出现原始 SGR 序列，说明 echo / line input 等输入模式仍未关闭，不能仅凭屏幕输出判断读取成功。

---

## Step 1：最小 CLI 验证 `Console.ReadKey()`

### 目的

验证最简单路径：

> 开启 mouse tracking 和 `ENABLE_VIRTUAL_TERMINAL_INPUT` 后，`Console.ReadKey(true)` 能否把 SGR mouse 序列作为字符输入暴露给 .NET Console。

注意：`Console.ReadKey(true)` 可能把一条 SGR mouse 序列拆成多个字符输入，因此该步骤重点不是“一次 ReadKey 得到完整事件”，而是验证能否通过 pending buffer 稳定重组事件。

### 建议位置

可先创建临时 spike 项目，例如：

```text
docs/2026.6.16.mouse-input/spikes/readkey-mouse/
```

不要直接修改 `Emuera.Headless`。

### 运行方式示例

```bash
dotnet new console -o docs/2026.6.16.mouse-input/spikes/readkey-mouse --framework net10.0
dotnet run --project docs/2026.6.16.mouse-input/spikes/readkey-mouse
```

### 程序行为

程序启动后：

1. 如果 stdin/stdout 不是 redirected，写入：

   ```text
   \x1b[?1000h\x1b[?1006h
   ```

2. 打印提示：

   ```text
   Click anywhere. Press Ctrl+C to quit.
   ```

3. 循环读取 `Console.ReadKey(true)`。
4. 每次读到字符，向 `Console.Error` 打印：

   ```text
   [key] Key=... KeyChar=... ch='...'
   ```

5. 维护一个 pending buffer。
6. 如果 buffer 形成 SGR mouse 序列，打印：

   ```text
   [mouse] left-down row=<Y> col=<X>
   ```

7. 按 `Ctrl+C` 退出；不能把 `Esc` 作为退出键，因为 `ESC` 也是 SGR mouse 序列的一部分。
8. `finally` 中写入：

   ```text
   \x1b[?1006l\x1b[?1000l
   ```

### 期望输出示例

```text
[key] Key=Unknown KeyChar=27 ch=''
[key] Key=Unknown KeyChar=91 ch='['
[key] Key=Unknown KeyChar=60 ch='<'
[key] Key=D0 KeyChar=48 ch='0'
[key] Key=OemSemicolon KeyChar=59 ch=';'
[key] Key=D1 KeyChar=53 ch='5'
[key] Key=OemSemicolon KeyChar=59 ch=';'
[key] Key=D8 KeyChar=56 ch='8'
[key] Key=Unknown KeyChar=77 ch='M'
[mouse] left-down row=8 col=15
```

也可能某些终端/运行时直接把完整序列作为一个 key 或分段方式不同，因此本步骤重点是观察是否能稳定重组出 mouse event。

### 通过标准

满足以下条件即可认为 Step 1 通过：

- 点击终端能产生 mouse event
- `row` / `col` 随点击位置变化
- 连续点击多次都稳定
- 键盘输入仍可读取
- 按 `Ctrl+C` 能退出
- 不把 `Esc` 当退出键，避免误吞 SGR 前缀
- 退出后终端鼠标选择恢复正常

### 失败标准

出现以下任一情况，进入 Step 2：

- 点击完全无反应
- 只读到 `\x1b`，后续字符读不到
- `ReadKey()` 在 ESC 后阻塞
- SGR 序列被拆得无法可靠重组
- 把 `Esc` 当退出键导致鼠标序列前缀被提前终止
- Windows Terminal 下无法读取鼠标事件
- 退出后终端鼠标行为异常

---

## Step 2：最小 CLI 验证 raw stdin bytes

### 目的

验证鼠标事件是否能作为原始 bytes 进入 stdin。Windows 下需要额外进入 raw-ish console input mode，否则终端可能回显 SGR 序列，导致误判为程序读取成功。

### 建议位置

```text
docs/2026.6.16.mouse-input/spikes/raw-stdin-mouse/
```

### 程序行为

程序启动后：

1. 检查 stdin/stdout 是否 redirected。
2. 如果可用，获取 `STD_INPUT_HANDLE`，读取原始 console input mode。
3. 临时设置 raw-ish input mode：
   - 开启 `ENABLE_VIRTUAL_TERMINAL_INPUT`
   - 开启 `ENABLE_EXTENDED_FLAGS`
   - 关闭 `ENABLE_PROCESSED_INPUT`
   - 关闭 `ENABLE_ECHO_INPUT`
   - 关闭 `ENABLE_LINE_INPUT`
   - 关闭 `ENABLE_QUICK_EDIT_MODE`
4. 写入 XTerm mouse 开启序列：

   ```text
   \x1b[?1000h\x1b[?1006h
   ```

5. 阻塞读取 `Console.OpenStandardInput()`。
6. 将 bytes 累积到 buffer。
7. 尝试解析：

   ```text
   ESC [ < Cb ; Cx ; Cy M
   ESC [ < Cb ; Cx ; Cy m
   ```

8. 向 `Console.Error` 打印原始 byte 和 mouse event，例如：

   ```text
   [mouse-bytes][byte] 0x1B
   [mouse-bytes] left-down row=<Y> col=<X>
   ```

9. 可选：打印原始 hex，方便排查：

   ```text
   [bytes] 1b 5b 3c 30 3b 31 35 3b 38 4d
   ```

10. 按 `Ctrl+C` 时读取到 `0x03` 字节后退出。
11. `finally` 中写入关闭序列，并恢复原始 console input mode。

### 期望输出示例

```text
[mouse-bytes][byte] 0x1B
[mouse-bytes][byte] 0x5B
[mouse-bytes][byte] 0x3C
[mouse-bytes][byte] 0x30
[mouse-bytes][byte] 0x3B
[mouse-bytes][byte] 0x31
[mouse-bytes][byte] 0x3B
[mouse-bytes][byte] 0x31
[mouse-bytes][byte] 0x4D
[mouse-bytes] left-down row=8 col=15
```

stderr 重定向后，终端屏幕不应再出现 `^[[<0;1;1M...`；原始 SGR bytes 应只出现在日志文件中。

### 通过标准

- raw stdin 能读到完整 SGR mouse bytes
- 终端不回显原始 `^[[<...` SGR 序列
- 点击坐标正确
- 多次点击稳定
- `Ctrl+C` 能作为 `0x03` 字节退出
- 退出后 mouse tracking 关闭，console input mode 恢复

### 失败标准

- raw stdin 也读不到鼠标事件
- 终端屏幕直接回显 `^[[<...`，说明 raw mode 未生效
- `Ctrl+C` 不能退出或退出后未恢复 console input mode
- Windows 下所有交互终端都失败
- raw stdin 读取导致普通键盘输入无法解析

### 失败后的结论

如果 raw stdin 也失败，应记录为：

> 当前终端/.NET Console 组合下无法可靠读取 XTerm mouse。  
> CLI 鼠标点击功能应仅在已验证通过的环境中启用，其他环境继续键盘 fallback。

必要时再评估：

- Windows native console input API
- 仅非 Windows 启用
- 完全放弃 Windows mouse tracking

---

## Step 2B：Windows Win32 `ReadConsoleInput()` 验证

### 触发条件

当项目需要确认 Windows 原生控制台输入能力，或需要决定 Windows 生产 backend 是否应优先使用 Win32 console input 时执行。

即使 Step 1 / Step 2 的 SGR stdin 路径已通过，也建议保留 Step 2B，因为它用于区分两类能力：

1. 终端是否能把鼠标事件作为 XTerm SGR bytes 写入 stdin。
2. Windows console input API 本身是否能以 `MOUSE_EVENT_RECORD` 形式提供鼠标事件。

### 建议位置

```text
docs/2026.6.16.mouse-input/spikes/win32-console-input-mouse/
```

### 程序行为

程序启动后：

1. 获取 `STD_INPUT_HANDLE`。
2. 读取原始 console input mode。
3. 临时启用：
   - `ENABLE_MOUSE_INPUT`
   - `ENABLE_WINDOW_INPUT`
   - `ENABLE_EXTENDED_FLAGS`
4. 临时禁用 `ENABLE_QUICK_EDIT_MODE`，避免点击优先进入终端选择模式。
5. 循环调用 `ReadConsoleInput()`。
6. 向 `Console.Error` 打印事件：
   - `KEY_EVENT`
   - `MOUSE_EVENT`
   - `WINDOW_BUFFER_SIZE_EVENT`
   - `FOCUS_EVENT`
7. 对 `MOUSE_EVENT_RECORD` 打印：
   - `X`
   - `Y`
   - `ButtonState`
   - `ControlKeyState`
   - `EventFlags`
8. 按 `Esc` 退出。
9. `finally` 中恢复原始 console input mode。

### 可选扩展

可以加一个 `--enable-xterm` 参数，先写入：

```text
\x1b[?1000h\x1b[?1006h
```

再观察 `ReadConsoleInput()` 收到的是：

- `MOUSE_EVENT_RECORD`
- 还是包含 ESC 序列的 `KEY_EVENT_RECORD`
- 或仍然没有事件

### 通过标准

- 点击终端能产生 `MOUSE_EVENT_RECORD`
- 点击坐标随位置变化
- 键盘 `Esc` 能产生 `KEY_EVENT_RECORD`
- 退出后原始 input mode 被恢复
- 终端鼠标选择行为恢复正常

### 失败标准

- 点击没有 `MOUSE_EVENT_RECORD`
- 键盘事件也读不到
- 禁用 Quick Edit 后仍然无法收到鼠标事件
- 退出后 input mode 未恢复

### 决策含义

| 结果 | 后续决策 |
|---|---|
| Win32 console input 能读到鼠标事件 | Windows 路径应考虑使用 `ReadConsoleInput()`，而不是 XTerm SGR stdin parser |
| Win32 console input 也读不到鼠标事件 | 当前 Windows 终端/.NET/ConPTY 组合不支持该鼠标输入路径；Windows 继续 keyboard fallback |
| 只有禁用 Quick Edit 后才能读到 | 生产实现必须临时禁用 Quick Edit，并在退出时恢复 |

---

## Step 3：`AgentCliProtocol` smoke test

### 前提

Step 1、Step 2 或 Step 2B 至少有一个通过。

### 目的

验证鼠标事件能否进入现有 `AgentCliProtocol` 主循环，并且不破坏现有 CLI 行为。

此步骤仍不做按钮点击提交，只打印坐标。

### 建议实现形态

Windows 生产路径建议使用 Win32 `ReadConsoleInput()`；如果选择 SGR stdin 路径，则必须复用 Step 2 已验证的 raw-ish console input mode，并明确其只适用于已验证终端。

在 `Emuera.Headless/Agent/AgentCliProtocol.cs` 中加入 debug-gated smoke backend：

- `EMUERA_DEBUG_MOUSE=1` 时启用
- Windows 下临时启用 `ENABLE_MOUSE_INPUT`
- 临时禁用 `ENABLE_QUICK_EDIT_MODE`
- 用 `WaitForSingleObject(handle, 0)` 非阻塞检查输入
- 用 `ReadConsoleInput()` 读取 `MOUSE_EVENT_RECORD` / `KEY_EVENT_RECORD`
- 鼠标事件只向 `Console.Error` 打印 row/col，不提交按钮
- 键盘事件继续走 `ProcessKey()`
- `finally` 中恢复原始 console input mode
- 若需要观察日志，建议使用 stderr 重定向或后续改为可选日志文件，避免 PowerShell 终端显示交错

只有 debug 开关开启时才向 `Console.Error` 打印鼠标事件，避免污染正常 CLI 输出。

> 上面只是 smoke test 结构，不是最终生产实现。  
> Windows 路径应使用 Win32 console input；非 Windows 路径可另行使用 XTerm SGR parser。

### 运行方式

使用真实 Emuera CLI，并启用 debug mouse smoke：

```bash
EMUERA_DEBUG_MOUSE=1 dotnet exec Emuera.Headless/bin/Debug/net10.0/Emuera.Headless.dll --ExeDir test_game --protocol cli
```

Windows PowerShell 下建议使用绝对路径：

```powershell
$env:EMUERA_DEBUG_MOUSE='1'
dotnet exec D:/LaoBro/Emuera.MCP/Emuera.Headless/bin/Debug/net10.0/Emuera.Headless.dll --ExeDir D:/LaoBro/Emuera.MCP/test_game --protocol cli
```

如果只想看鼠标移动事件，可同时启用 verbose：

```powershell
$env:EMUERA_DEBUG_MOUSE='1'
$env:EMUERA_DEBUG_MOUSE_VERBOSE='1'
dotnet exec D:/LaoBro/Emuera.MCP/Emuera.Headless/bin/Debug/net10.0/Emuera.Headless.dll --ExeDir D:/LaoBro/Emuera.MCP/test_game --protocol cli
```

如果要排查键盘事件为什么没有反应，可同时启用 key logging：

```powershell
$env:EMUERA_DEBUG_MOUSE='1'
$env:EMUERA_DEBUG_MOUSE_KEYS='1'
dotnet exec D:/LaoBro/Emuera.MCP/Emuera.Headless/bin/Debug/net10.0/Emuera.Headless.dll --ExeDir D:/LaoBro/Emuera.MCP/test_game --protocol cli
```

如果要进入最小按钮点击 MVP 测试，可同时启用鼠标点击：

```powershell
$env:EMUERA_DEBUG_MOUSE='1'
$env:EMUERA_ENABLE_MOUSE_CLICK='1'
dotnet exec D:/LaoBro/Emuera.MCP/Emuera.Headless/bin/Debug/net10.0/Emuera.Headless.dll --ExeDir D:/LaoBro/Emuera.MCP/test_game --protocol cli
```

如果要查看按钮区域记录，可同时启用 region debug：

```powershell
$env:EMUERA_DEBUG_MOUSE='1'
$env:EMUERA_ENABLE_MOUSE_CLICK='1'
$env:EMUERA_DEBUG_MOUSE_REGIONS='1'
dotnet exec D:/LaoBro/Emuera.MCP/Emuera.Headless/bin/Debug/net10.0/Emuera.Headless.dll --ExeDir D:/LaoBro/Emuera.MCP/test_game --protocol cli
```

### 验证内容

- 启动后普通游戏输出正常
- 点击按钮文字附近，`Console.Error` 能打印 row/col
- 点击空白也能打印 row/col，或至少不崩溃
- 键盘输入仍正常
- `↑/↓ + Enter` 仍能选择按钮
- 程序退出后 mouse tracking 关闭
- 正常 CLI 业务输出不被鼠标功能额外提示污染；debug 输出若写入 `Console.Error`，在 PowerShell 终端显示中可能与 stdout 交错，应通过 stderr 重定向或日志文件观察

### 通过标准

- 鼠标事件能进入 `AgentCliProtocol`
- 现有键盘 CLI 行为未被破坏
- 业务 stdout 不被鼠标功能额外提示污染；debug 输出在终端显示中可能交错，需要重定向或日志文件观察
- cleanup 正常

### 失败标准

- 鼠标事件进入后导致键盘输入异常
- 鼠标功能在非 debug 路径额外输出业务提示
- 退出后 mouse tracking 未关闭
- 事件解析阻塞主循环
- debug 输出策略未处理时，不应把 PowerShell 终端显示交错误判为游戏 stdout 损坏

---

## Step 4：最小按钮区域 MVP

### 前提

Step 3 通过。

### 目的

验证真实按钮点击链路：

> 点击按钮文字区域 → 找到对应 `ConsoleButtonString` → 提交按钮值

### MVP 范围

只做最简单情况：

- 只处理左键按下
- 只记录当前 generation 按钮
- 只记录完整落在单个终端行内的按钮
- 点击空白 no-op
- 点击按钮提交：

```csharp
string input = btn.IsInteger ? btn.Input.ToString() : btn.Inputs;
DispatchInput(input);
```

- 点击成功后清空按钮 prompt 和旧区域
- 不支持跨行按钮，或明确忽略跨行按钮
- 不支持 hover、滚轮、右键、拖拽

### 验证游戏

使用 `test_game` fixture。

期望：

1. 初始菜单显示 `[0] Hello`、`[1] Quit`
2. 点击 `[0] Hello` 后输出：

   ```text
   You entered: 0
   ```

3. 第二菜单显示 `[0] World`、`[1] Exit`
4. 点击 `[0] World` 后输出：

   ```text
   You entered: 0
   ```

5. 点击 `[1] Exit` 后输出：

   ```text
   You entered: 1
   ```

6. 游戏到达结束状态

同时确认键盘路径仍然可用。

已观察到的区域坐标示例：

```text
[AgentCliProtocol][mouse-region] row=5 col=0-8 input=0 label=[0] Hello
[AgentCliProtocol][mouse-region] row=6 col=0-7 input=1 label=[1] Quit
[AgentCliProtocol][mouse-region] row=14 col=0-8 input=0 label=[0] World
[AgentCliProtocol][mouse-region] row=15 col=0-7 input=1 label=[1] Exit
```

注意：PowerShell 终端显示中 `Console.Error` debug 日志可能与 stdout 交错；验证坐标建议重定向 stderr 到日志文件。

---

## Step 5：坐标系转换可行性验证

### 目的

验证 v1.2 的坐标模型是否足够支撑最小按钮点击 MVP：

> Win32 鼠标事件坐标 → 内部 0-based visible viewport 坐标 → 按钮区域命中

详细验证用例见：

```text
coordinate-validation-plan.md
```

对应 spike：

```text
docs/2026.6.16.mouse-input/spikes/coordinate-validation/
```

### 验证方式

第一版使用独立 `coordinate-validation` spike，不修改 `AgentCliProtocol`。

该 spike 使用 Win32 `ReadConsoleInput()`，但不启用 XTerm mouse tracking，也不依赖 `Console.WindowTop` 或 `Console.SetCursorPosition()` 来定位按钮。程序通过自然输出滚动生成按钮，并把最后一行显式作为 prompt 行：

```text
输出多行预填充
→ 输出按钮行
→ 输出若干间隔行
→ 输出 prompt 行
→ 按钮预期 viewport row = promptRow - distanceFromPrompt
```

点击后日志同时输出：

- `dwMousePosition.X/Y`
- `Console.WindowTop/Left/Height/Width`
- expected viewport row / col
- `hitRawViewport`
- `hitWindowTop`
- `inferredWindowTop`
- `hitInferredTop`
- `promptRow`
- `distanceFromPrompt`

### 重点问题

- `MOUSE_EVENT_RECORD.dwMousePosition` 是否可作为 screen buffer 坐标使用，并通过 `WindowTop/Left` 归一化为 viewport 坐标。
- 当前终端下 `Console.WindowTop` 是否可用。
- 如果 `Console.WindowTop` 不可用，是否可以通过已知按钮点击推断 `rawY - expectedViewportRow`。
- 等待输入时，按钮选择提示行是否可稳定视为当前终端最后一行。
- `lastPromptRow = Console.WindowHeight - 1` 与 `buttonRow = lastPromptRow - rowsBelowPrompt` 是否能命中当前按钮。
- 输出滚动、手动 scrollback、resize、提示行换行是否会影响 v1.2 坐标假设。

### 通过标准

- 不依赖 `Console.SetCursorPosition()` 也能稳定生成目标位置的按钮。
- prompt 行稳定位于最后一行。
- `--rows-above-prompt N` 时，最后一个按钮距离 prompt 行 N 行。
- 多按钮时，按钮从上到下依次位于 prompt 上方递增距离处。
- 左上角点击得到 `row=0 col=0`。
- 右下角点击得到 `row=WindowHeight-1 col=WindowWidth-1`。
- 点击按钮文字区域时，归一化后能稳定命中。
- 点击按钮同一行空白处 no-op / miss。
- 点击按钮和 prompt 之间的空白行 no-op / miss。
- 点击 prompt 行 no-op / miss。
- pipe 模式不启用鼠标，键盘 fallback 保持原行为。
- 提示行换行、scrollback 等非 v1.2 范围问题被明确记录为限制，而不是误判为 MVP 成功。

---

## 决策矩阵

| 验证结果 | 后续决策 |
|---|---|
| Step 1 `Console.ReadKey()` 通过 | 可作为 spike / 非生产验证路径；若采用，必须维护 ESC buffer，且不能把 `Esc` 当退出键 |
| Step 1 不稳定，但 Step 2 raw stdin 通过 | 可实现 raw stdin input reader；必须进入 raw-ish console input mode，且后续不能混用 `Console.ReadKey()` |
| Step 1/2 通过，但生产稳定性优先 | Windows 仍优先使用 Win32 `ReadConsoleInput()`；SGR stdin 作为非 Windows 或已验证终端路径 |
| Step 1/2 失败，但 Step 2B Win32 `ReadConsoleInput()` 通过 | Windows 路径使用 Win32 console input；SGR stdin parser 仅用于非 Windows 或已验证终端 |
| Step 1/2 在非 Windows 通过，Windows 失败 | Windows 默认 keyboard fallback 或 Win32 console input；非 Windows 可启用 mouse |
| Step 2B 也失败 | 当前 Windows 终端/.NET/ConPTY 组合不支持鼠标输入；Windows 继续 keyboard fallback |
| Windows Terminal 通过，老 conhost 失败 | 仅在实际支持的环境中启用；老 conhost 继续 keyboard fallback 或单独验证 |
| redirected stdin/stdout 下未启用 mouse，pipe 测试通过 | 继续保留 pipe 模式原行为 |
| Step 3 smoke test 破坏键盘输入 | 不进入 Step 4，先重构 input reader |
| Step 3 稳定 | 进入 Step 4 最小按钮区域 MVP |
| Step 4 按钮点击通过但 debug 输出交错 | 功能链路通过；后续清理 debug 输出策略，不把显示交错视为坐标/提交失败 |

---

## 验收清单

### 底层输入

- [x] Windows Win32 `ReadConsoleInput()` 能产生 `MOUSE_EVENT_RECORD`
- [x] 鼠标点击坐标随点击位置变化
- [x] 多次点击稳定
- [x] 鼠标释放事件可被忽略
- [x] 退出后 Win32 console input mode 恢复
- [x] Windows 下 XTerm SGR `Console.ReadKey()` 可用：需要 `ENABLE_VIRTUAL_TERMINAL_INPUT` 和 pending buffer
- [x] Windows 下 raw stdin SGR 可用：需要 raw-ish console input mode，并把 `Ctrl+C` 作为 `0x03`
- [ ] 非 Windows SGR 路径验证

### `AgentCliProtocol` smoke

- [x] 鼠标事件进入主循环
- [x] 键盘输入仍正常
- [x] `↑/↓ + Enter` 仍正常
- [x] pipe 模式不受影响
- [x] 退出时恢复 Win32 console input mode
- [x] raw stdin 退出后恢复 console input mode
- [ ] debug 输出改为文件日志或默认关闭，避免终端显示交错

### 最小按钮 MVP

- [x] 点击 `[0] Hello` 提交 `0`
- [x] 点击 `[1] Quit` / `[1] Exit` 提交 `1`
- [ ] 点击空白 no-op（待补测）
- [x] 旧菜单区域不会在新菜单出现后误命中
- [ ] ANSI、全角字符、终端替换字符不明显偏移（待补测）
- [x] 键盘 fallback 仍可用

### 坐标系转换 spike

- [x] 不依赖 `Console.SetCursorPosition()` 生成目标位置按钮
- [x] prompt 行稳定位于最后一行
- [x] 默认 `rowsAbovePrompt=1` 命中
- [x] `--rows-above-prompt 5 --left 8` 命中
- [x] `--button` 可覆盖默认 `CLICK`
- [x] 多按钮 `[1] Quit` 命中，`distanceFromPrompt=3`
- [x] 点击按钮和 prompt 之间的空白行 miss
- [x] 点击 prompt 行 miss
- [ ] 左上角点击得到 `rawY=0 rawX=0`（待补测）
- [ ] 右下角点击得到 `rawY=WindowHeight-1 rawX=WindowWidth-1`（待补测）

---

## 建议结论写入方式

完成验证后，在 `validation-results.md` 中写入类似结论：

```markdown
## 2026-06-18 Windows Terminal + PowerShell 7.6.2

- Step 1 Console.ReadKey: 通过；需要 `ENABLE_VIRTUAL_TERMINAL_INPUT` 和 pending buffer
- Step 2 raw stdin: 通过；需要 raw-ish console input mode，并把 `Ctrl+C` 作为 `0x03`
- Step 2B Win32 ReadConsoleInput: 通过
- Step 3 AgentCliProtocol smoke: 通过
- Step 4 最小按钮区域 MVP: 通过
- Step 5 坐标系转换 spike: prompt-anchored 命中/miss 主要路径通过；左上/右下角边界点击待补测
- 结论: Windows 生产路径建议优先使用 Win32 `ReadConsoleInput()`；SGR stdin parser 可作为非 Windows / 已验证终端路径或备选方案

备注:
- 点击坐标示例: row=0 col=0
- 问题: PowerShell 终端显示中 `Console.Error` 与 stdout 可能交错；验证时应重定向 stderr
- 问题: `Console.ReadKey()` 会把 ESC 序列拆开，不能把 `Esc` 当退出键
```

---

## 推荐执行顺序

1. 写 Step 1 最小 `Console.ReadKey()` CLI，并开启 `ENABLE_VIRTUAL_TERMINAL_INPUT`。
2. 在 Windows Terminal + PowerShell 验证 SGR mouse 是否能进入 .NET Console；记录序列拆分方式。
3. 写 Step 2 raw stdin CLI；Windows 下必须进入 raw-ish console input mode，并用 stderr 重定向确认终端不回显原始 SGR bytes。
4. Step 1、Step 2 或 Step 2B 通过后，再改 `AgentCliProtocol` 做 smoke test。
5. smoke test 通过后，再执行 `coordinate-validation-plan.md` 验证坐标系转换。
6. 坐标验证通过后，再进入最小按钮区域 MVP。
7. MVP 通过后，再清理 debug 输出策略，使生产路径不向终端输出鼠标调试信息。
8. 生产化前明确 Windows backend：优先 Win32 `ReadConsoleInput()`，或明确选择已验证的 SGR stdin 路径。

不要在 Step 1 之前直接实现完整按钮点击功能。
