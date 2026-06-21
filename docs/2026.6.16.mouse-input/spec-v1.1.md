# CLI 鼠标点击按钮 — v1.1 修订规格

## 版本信息

- 版本：v1.1
- 基线：`docs/2026.6.16.mouse-input/spec.md` v1.0
- 目标文件：`Emuera.Headless/Agent/AgentCliProtocol.cs`
- 相关辅助：`Emuera.Headless/UI/Game/EmueraConsole.AgentBridge.cs`、`Emuera.Headless/Agent/TerminalDisplayWidth.cs`、CLI/PTY 测试
- 当前验证状态：Windows PowerShell 下 `Console.ReadKey()` / raw stdin SGR 路径失败；Win32 `ReadConsoleInput()` 路径通过；最小按钮点击 MVP 已通过

## 一句话总结

让 CLI 终端用户直接用鼠标点击按钮文字来选择选项；键盘的 `↑/↓ + Enter` 模式继续作为 fallback，两者互不阻塞。

## 现状与问题

当前 CLI 模式下选择按钮需要两步：

1. 游戏输出按钮文本，例如 `[0] Hello`、`[1] Quit`
2. 用户按 `↑` 进入按钮选择模式
3. 底部出现提示 `> [1/2] [0] Hello | [Up/Dn] Switch  [Enter] OK`
4. 用 `↑/↓` 循环切换，`Enter` 确认

这要求用户理解“先进入模式再切换”的流程。v1.1 的目标是让鼠标点击成为一等操作：用户看到按钮文字后，直接点击按钮文字即可提交。

## 目标

- 用户在支持 XTerm SGR mouse 的非 Windows 终端，或支持 Win32 console input 的 Windows 终端中，点击按钮文字即可直接提交该按钮值。
- 键盘 `↑/↓ + Enter` 模式保持可用，作为不支持鼠标终端或用户偏好键盘时的 fallback。
- pipe / redirected stdin 模式不受影响，继续走现有 CLI 文本输入逻辑。
- 不改变 JSONL、server、WinForms 行为。

## 不做

| 不做的事 | 原因 |
|---|---|
| 悬浮高亮 | 需要追踪鼠标移动事件，复杂度高，后续再做 |
| 点击动画/视觉反馈 | 游戏画面刷新本身就是反馈；MVP 不输出额外提示 |
| 拖拽、滚轮、右键、中键 | 游戏按钮选择只需要左键按下 |
| 鼠标模式动态开关 | 交互 CLI 中始终开启最简单；pipe 模式完全跳过 |
| pipe 模式鼠标支持 | stdin 重定向时没有真实终端 |
| 模拟 `PrimitiveMouseKey` | 原始鼠标/键盘事件语义无法由终端按钮点击可靠表达；继续拒绝 |

## 输入读取策略

Windows 验证结果已经证明：在当前 Windows PowerShell / Windows Terminal-like 环境中，XTerm SGR mouse 不能作为可靠入口：

- `Console.ReadKey(true)` 不能稳定暴露完整 SGR mouse 序列。
- raw stdin bytes 在开启 mouse tracking 后也不能可靠接收鼠标和普通键盘输入。
- Win32 `ReadConsoleInput()` 可以稳定收到 `MOUSE_EVENT_RECORD` 和 `KEY_EVENT_RECORD`。

因此 Windows MVP 使用 Win32 console input：

- `ReadConsoleInput()` 读取 `MOUSE_EVENT_RECORD`
- 将 `dwMousePosition.X/Y` 转换为内部 0-based `col/row`
- 只处理左键按下事件
- 忽略 move、wheel、right button、非当前按钮按下等噪声事件
- 退出时恢复原始 console input mode

非 Windows 终端仍可保留 XTerm SGR mouse parser，但必须在目标终端中独立验证。

### XTerm SGR mouse 协议说明

XTerm SGR mouse 仅作为非 Windows 或已验证终端路径参考：

- `1000`：基本鼠标追踪，报告鼠标按下事件
- `1006`：SGR 扩展格式，坐标以明文数字发送，没有旧格式的 223 列/行限制

开启序列：

```text
\x1b[?1000h\x1b[?1006h
```

关闭序列：

```text
\x1b[?1006l\x1b[?1000l
```

> v1.1 建议关闭时先关闭 `1006`，再关闭 `1000`，避免终端在退出追踪前继续发送 SGR 格式事件。

SGR 鼠标事件格式：

```text
\x1b[<Cb;Cx;CyM
\x1b[<Cb;Cx;Cy m
```

含义：

- `Cb`：button code。低 2 位表示按钮类型；`0` 表示左键
- `Cx`：列号，终端协议为 1-based
- `Cy`：行号，终端协议为 1-based
- `M`：按下事件
- `m`：释放事件

v1.1 只处理左键按下：

- 接受：`Cb & 3 == 0` 且结尾为 `M`
- 忽略：释放事件 `m`、右键、中键、滚轮、带 modifier 的非左键事件

## 启用条件

鼠标读取只在真正的交互 CLI 中启用：

```csharp
if (OperatingSystem.IsWindows())
{
    // Windows MVP 使用 Win32 console input，不依赖 ANSI / SGR。
    !Console.IsInputRedirected
}
else
{
    // 非 Windows 路径可保留 XTerm SGR mouse parser。
    !Console.IsInputRedirected &&
    !Console.IsOutputRedirected &&
    _ansiEnabled
}
```

其中 `_ansiEnabled` 继续复用现有判断：

```csharp
Program.AnsiEnabled || !OperatingSystem.IsWindows()
```

pipe 模式：

- `RunPipeCliLoop()` 完全不启用 mouse tracking
- 不解析鼠标 ESC 序列
- 现有 redirected stdin/stdout 测试必须保持通过

## 关键实现风险与 v1.1 决策

### 风险：`Console.ReadKey()` 不一定可靠

Windows 上 `.NET Console.ReadKey()` 底层不是简单读取 stdin bytes。开启 XTerm mouse tracking 后，终端可能把鼠标事件写成 ESC 序列，但 `Console.ReadKey()` 可能：

- 只返回 `\x1b`
- 把 `[`、`<`、数字、`M` 拆成多次按键
- 在等待后续字符时阻塞
- 在 Windows Console / ConPTY 下表现与 Linux/macOS 不同

v1.1 决策：

1. 实现前必须先做 spike，验证目标终端下 `Console.ReadKey(true)` 是否能稳定读到完整 SGR mouse 序列。
2. Windows PowerShell 验证已确认 `Console.ReadKey()` 和 raw stdin 都不可靠，因此 Windows MVP 必须改用 Win32 `ReadConsoleInput()`。
3. 非 Windows SGR parser 不能依赖“一次 `ReadKey()` 返回完整 ESC 序列”这个假设。
4. Windows 下应临时启用 `ENABLE_MOUSE_INPUT` / `ENABLE_WINDOW_INPUT` / `ENABLE_EXTENDED_FLAGS`，临时禁用 `ENABLE_QUICK_EDIT_MODE`，退出时恢复原始 console input mode。

### 风险：按钮坐标必须基于终端渲染结果

`ConsoleButtonString.PointX` / `Width` 是游戏内部像素坐标，不是终端列坐标。终端坐标必须从实际输出文本计算，并考虑：

- `ConsoleDisplayLine.Align` 产生的左侧 padding
- ANSI 样式序列不占显示宽度
- 全角字符占 2 列
- `TerminalDisplayWidth.ReplaceForTerminal()` 对部分符号的替换/补空格
- 终端自动换行
- 按钮跨行或区域重叠

## 数据模型

v1.1 建议在 `AgentCliProtocol` 中新增内部结构：

```csharp
private sealed class ButtonTerminalRegion
{
    public required int Row;       // 0-based terminal row
    public required int Left;      // 0-based terminal column, inclusive
    public required int Right;     // 0-based terminal column, inclusive
    public required ConsoleButtonString Button;
    public required long Generation;
}
```

约束：

- `Row`、`Left`、`Right` 使用 0-based 内部坐标。
- SGR mouse 输入进入后转换为 0-based：`row = cy - 1`，`col = cx - 1`。
- `Generation` 使用按钮 generation，用于避免旧菜单区域误命中。
- 区域只记录当前可见终端行；不可见 scrollback 中的按钮不记录。
- 区域只在当前请求仍然有效时可用；点击提交后应清空或标记失效。

## 按钮区域记录规则

### 记录时机

按钮区域必须在“实际输出到终端的行”上记录，而不是只在 `FlushBuffer()` 前后粗略采样。

需要记录/重建区域的输出路径：

1. 初始 `FlushBuffer()`
2. 普通 `FlushBuffer()` 中实际写出的按钮行
3. `FullRefresh()` 重绘的可见行

需要清空区域的场景：

- `Console.Clear()`
- `FullRefresh()` 开始前
- `console._needFullRefresh`
- `EraseTerminalRows()`
- 按钮 generation 变化
- 终端窗口宽度/高度变化
- 点击按钮成功提交后
- 当前请求不再是可点击按钮请求时

### 记录对象

只记录满足以下条件的按钮：

```csharp
console.State == ConsoleState.WaitInput
req.InputType != InputType.EnterKey
req.InputType != InputType.AnyKey
btn.IsButton
btn.Generation == console.LastButtonGeneration
```

如果当前请求是 `InputType.PrimitiveMouseKey`，终端鼠标点击不能替代原始鼠标事件，应继续使用现有拒绝逻辑。

### 坐标计算

对每个实际输出的 display line：

1. 计算左侧 padding：
   - `LEFT`：`0`
   - `CENTER`：`max((gameWidth - textWidth) / 2, 0)`
   - `RIGHT`：`max(gameWidth - textWidth, 0)`
2. 使用与输出一致的格式化结果：
   - `FormatLineForTerminal(line)`
   - 该结果已经包含 alignment、ANSI 样式、`ReplaceForTerminal()` 补偿
3. 遍历 `line.Buttons`，按渲染顺序累加列宽：
   - ANSI 序列不增加列宽
   - 全角字符增加 2
   - `ReplaceForTerminal()` 后新增的补偿空格计入显示宽度
4. 对 `btn.IsButton && btn.Generation == LastButtonGeneration` 的按钮记录：
   - `Row = 当前终端行`
   - `Left = 当前列`
   - `Right = 当前列 + 按钮渲染宽度 - 1`
5. 如果按钮区域跨越终端自动换行：
   - MVP 可记录多个 segment，每个 segment 指向同一个按钮
   - 或明确忽略跨行按钮；但必须在 spec 中固定行为
   - v1.1 推荐记录 segment，因为这样更符合“点击看到的文字即提交”

### 与现有格式化函数的关系

`EmueraConsole.FormatLineForTerminal()` 已经负责输出格式化，见 `Emuera.Headless/UI/Game/EmueraConsole.AgentBridge.cs`。

v1.1 不要求改变终端视觉输出；但可能需要新增辅助方法，用于同时返回：

- 最终终端文本
- 每个 button 的起始/结束列
- 自动换行后的 segment 信息

目标是避免 parser 重新猜测 ANSI、全角、alignment 和终端替换规则。

## 鼠标事件解析

### 推荐输入读取策略

v1.1 不强制只能使用 `Console.ReadKey()`。实现应选择一种能在目标平台稳定读取鼠标事件的方式：

#### Windows：Win32 `ReadConsoleInput()`

- Windows MVP 推荐方案
- 使用 `ReadConsoleInput()` 读取 `MOUSE_EVENT_RECORD`
- 只处理 `FROM_LEFT_1ST_BUTTON_PRESSED` 且 `dwEventFlags == 0` 的左键按下事件
- 忽略 move、wheel、right button、generic button-up 等非 MVP 噪声
- 键盘事件继续转换为现有 `ConsoleKeyInfo` 路径

#### 非 Windows：`Console.ReadKey()` + ESC buffer

- 仅在 spike 证明目标终端稳定可用时使用
- 需要维护 pending ESC buffer
- 读到 `\x1b` 后继续收集后续字符，直到：
  - 形成完整 SGR mouse 序列
  - 或确认只是普通 Escape 键

#### 非 Windows：原始 stdin bytes reader

- 当 `Console.ReadKey()` 不稳定时的备选方案
- 从 `Console.OpenStandardInput()` 非阻塞读取 bytes
- 普通按键仍按 UTF-8/控制台编码解析为字符
- ESC 序列由状态机解析

v1.1 要求：无论采用哪种方案，鼠标 parser 都必须能处理事件被拆分或批量到达的情况。

### Parser 规则

#### Windows Win32 路径

从 `MOUSE_EVENT_RECORD` 提取：

- `row = dwMousePosition.Y`
- `col = dwMousePosition.X`
- `leftButtonDown = (dwButtonState & FROM_LEFT_1ST_BUTTON_PRESSED) != 0`
- `buttonEvent = dwEventFlags == 0`

处理：

- 只接受 `leftButtonDown && buttonEvent` 的左键按下事件
- 忽略 `MOUSE_MOVED`、`MOUSE_WHEELED`、`MOUSE_HWHEELED`、`DOUBLE_CLICK` 等非 MVP 事件
- 忽略右键、中键、滚轮和没有前序左键按下的释放事件
- 键盘事件继续转换为现有 `ConsoleKeyInfo` 路径

#### 非 Windows SGR 路径

输入 buffer 中识别：

```text
ESC [ < digits ; digits ; digits M
ESC [ < digits ; digits ; digits m
```

处理：

- `M`：按下事件，继续匹配按钮
- `m`：释放事件，忽略
- 坐标越界：忽略
- 非 SGR ESC 序列：按现有 Escape/普通按键逻辑处理，避免破坏键盘 fallback

## 点击命中规则

点击坐标 `(row, col)` 进入后：

1. 如果当前没有有效按钮区域，忽略。
2. 在 `_buttonRegions` 中查找：
   - `region.Row == row`
   - `region.Left <= col <= region.Right`
3. 如果多个区域重叠，选择“最后绘制/后出现”的区域。
4. 找到按钮后：
   - 复用现有按钮提交逻辑
   - 即 `btn.IsInteger ? btn.Input.ToString() : btn.Inputs`
   - 调用 `DispatchInput(input)`
   - 清除按钮 prompt 和旧区域
5. 点击空白：
   - no-op
   - 不 echo
   - 不清空输入 buffer，除非当前按钮提交路径要求

## 与键盘模式的交互

- 鼠标点击按钮：直接提交，不需要 `Enter`
- `↑/↓`：继续切换当前选中按钮
- `Enter`：确认当前选中按钮
- 鼠标和键盘可以同时启用
- 当前底部 prompt 继续作为键盘 fallback 提示，但鼠标点击不要求先进入 prompt 模式
- prompt 行本身不记录为按钮区域

## 清理与生命周期

`RunConsoleKeyLoop()` 应采用类似结构：

```csharp
private void RunConsoleKeyLoop()
{
    EnableMouseTrackingIfPossible();

    try
    {
        while (!IsStopped)
        {
            // existing loop
        }
    }
    finally
    {
        DisableMouseTracking();
        ClearButtonRegions();
    }
}
```

要求：

- 开启失败不抛出到主流程；静默 fallback 到键盘
- 退出、异常、停止时都发送关闭序列
- pipe 模式不调用 mouse enable/disable
- 不在 stdout 输出额外鼠标提示，避免污染 CLI 文本

## 边界情况

### 终端 resize

- 每次轮询可比较 `Console.WindowWidth` / `Console.WindowHeight`
- 变化时清空旧区域
- 下一次输出按钮行时重新记录

### 自动换行

- 记录的是终端渲染行，不是游戏逻辑行
- 按钮跨行时，v1.1 推荐记录多个 segment
- 如果实现选择忽略跨行按钮，必须在 UI 或文档中明确

### ANSI 样式

- ANSI 序列不占列
- 区域匹配必须基于渲染宽度，而不是字符串长度

### 全角字符

- 全角字符占 2 列
- 使用现有 `TerminalDisplayWidth.GetDisplayWidth()` 和 `ReplaceForTerminal()` 规则

### 重叠按钮

- 选择最后绘制/后出现的按钮
- 与 WinForms “后绘制优先”的命中直觉一致

### 旧区域

- 旧区域必须在新菜单输出前清空
- 避免点击新画面位置却提交旧菜单按钮

### 不支持鼠标的终端

- 不报错
- 不改变输出
- 用户仍可用键盘 fallback

## 验收标准

### 功能验收

- 在 Windows Terminal 或等价现代终端中，点击 `[0] Hello` 会提交 `0`
- 点击 `[1] Quit` / `[1] Exit` 会提交 `1`
- 点击按钮区域外不产生输入、不 echo、不改变游戏状态
- 点击后游戏刷新，不额外输出鼠标提示
- 键盘 `↑/↓ + Enter` 行为保持不变
- pipe 模式 `tests/test_cli.py` 行为保持不变

### 稳定性验收

- 旧菜单按钮区域不会在新菜单出现后继续命中
- `Console.Clear()` / full refresh 后旧区域全部失效
- 终端 resize 后不会保留旧坐标
- ANSI 样式、全角字符、终端替换字符不会导致明显偏移
- 鼠标释放事件不会重复提交

### 兼容性验收

- `Console.IsInputRedirected` 时不启用 mouse tracking
- 非 Windows SGR 路径在 `Console.IsOutputRedirected` 时不启用 mouse tracking
- Windows Win32 路径退出或异常时恢复原始 console input mode
- 非 Windows SGR 路径退出或异常时发送关闭序列
- 不支持 SGR mouse 的非 Windows 终端中，键盘 fallback 可用

## 测试计划

### 单元测试

建议新增 parser 和 region matcher 单元测试：

- Windows：Win32 `MOUSE_EVENT_RECORD` 到内部鼠标事件
- Windows：左键按下接受，move / wheel / right button / release 忽略
- 非 Windows：SGR 左键按下解析
- 非 Windows：SGR 释放事件忽略
- 非 Windows：非 SGR ESC 序列回退
- 1-based 到 0-based 坐标转换
- ANSI 不占列
- 全角字符占 2 列
- 居右/居中 padding 计算
- 重叠区域选择后绘制按钮
- 点击空白 no-op
- 点击成功复用按钮输入值

### 集成测试

现有 pipe 测试必须继续通过：

```bash
python tests/test_cli.py --binary D:/LaoBro/Emuera.MCP/Emuera.Headless/bin/Debug/net10.0/Emuera.Headless.exe --game-dir test_game
```

鼠标点击需要 PTY 或真实终端验证，因为 redirected stdin 无法产生鼠标事件。

建议手动验证：

```bash
dotnet exec Emuera.Headless/bin/Debug/net10.0/Emuera.Headless.dll --ExeDir test_game --protocol cli
```

在真实终端中：

1. 点击 `[0] Hello`，确认输出 `You entered: 0`
2. 点击 `[1] Exit`，确认提交 `1`
3. 使用键盘 `↑/↓ + Enter`，确认行为不变
4. 点击空白，确认无输入
5. 触发 full refresh / resize 后，确认旧按钮不会误命中

## 实现顺序建议

1. 先做 mouse input spike：
   - Windows：验证 Win32 `ReadConsoleInput()` 是否能稳定读取鼠标和键盘事件
   - 非 Windows：验证 `Console.ReadKey(true)` 或 raw stdin 是否能稳定读取完整 SGR mouse 序列
2. 增加鼠标事件解析单元测试：
   - Windows：Win32 `MOUSE_EVENT_RECORD` 到内部坐标/按钮事件
   - 非 Windows：SGR 左键按下解析
3. 增加按钮区域记录/匹配单元测试
4. 在 `AgentCliProtocol` 中接入平台输入读取
5. 在输出按钮行时记录 region
6. 在主循环中解析鼠标事件并命中提交
7. 跑 pipe CLI 回归测试
8. 做真实终端手动验收
9. 清理 debug 输出策略，生产路径不向终端输出鼠标调试信息

## v1.1 与 v1.0 的主要差异

- 明确 `Console.ReadKey()` 风险，并要求先做 spike
- 明确 Windows PowerShell 下 `Console.ReadKey()` / raw stdin SGR 路径不可靠，Windows MVP 改用 Win32 `ReadConsoleInput()`
- 明确非 Windows 可保留 SGR mouse parser，但必须独立验证
- 明确鼠标开启条件区分 Windows Win32 路径和非 Windows SGR 路径
- 明确鼠标坐标内部统一为 0-based
- 明确按钮区域必须基于终端渲染结果，而不是游戏像素坐标
- 增加 ANSI、全角、终端替换字符、自动换行的处理要求
- 增加旧区域失效规则
- 增加重叠按钮、点击空白、释放事件、resize 等边界行为
- 增加验收标准和测试计划
- 增加 debug 输出清理要求，避免验证日志污染正常 CLI 显示
