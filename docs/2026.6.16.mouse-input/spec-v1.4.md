# CLI 鼠标点击按钮 — v1.4 conhost buffer 坐标规格

## 版本信息

- 版本：v1.4
- 基线：`docs/2026.6.16.mouse-input/spec-v1.3.md`
- 关联验证：`docs/2026.6.16.mouse-input/validation-results.md`、`docs/2026.6.16.mouse-input/coordinate-validation-results.md`
- 目标文件：`Emuera.Headless/Agent/AgentCliProtocol.cs`、`Emuera.Headless/Agent/AgentCliMouseInput.cs`（新建）
- 目标平台：Windows `conhost.exe` / classic console host
- 输入 backend：Win32 `ReadConsoleInput()`
- 输入读取策略：方案 D — `GetNumberOfConsoleInputEvents` 非阻塞轮询 + `ReadConsoleInput` 统一分派

## 相对 v1.3 的变更

- 目标平台从泛化的 Windows Console 收窄为 `conhost.exe`。
- 坐标模型从 viewport 坐标改为 conhost console screen buffer 坐标。
- `MOUSE_EVENT_RECORD.dwMousePosition` 的 raw buffer 坐标直接用于命中，不再减去 `Console.WindowTop` / `Console.WindowLeft`。
- 按钮区域记录从依赖 prompt 行改为直接记录按钮所在行的 buffer 坐标。
- `RenderButtonPrompt()` 只负责显示，不再参与命中计算，不再捕获 prompt 行。
- 删除 v1.3 的 `cursorTop - 1`、`cursorTop - 2`、`promptRow - distanceFromPrompt` 等反推行号逻辑。
- 不再承诺 Windows Terminal / conpty 坐标兼容。
- **输入读取改为方案 D**：主循环用 `GetNumberOfConsoleInputEvents` 查询队列长度，>0 时逐条 `ReadConsoleInput` 读取并按 `EventType` 分派；键盘与鼠标事件统一从 `ReadConsoleInput` 消费，不再调用 `Console.KeyAvailable` / `Console.ReadKey()`，避免 .NET Console 内部缓冲吞掉鼠标事件。
- **键盘事件映射规则**：`KEY_EVENT_RECORD` 显式转换为 `ConsoleKeyInfo` 复用现有 `ProcessKey` 路径，映射规则见「键盘事件映射」一节。
- **鼠标输入拆分到新文件** `AgentCliMouseInput.cs`：负责 P/Invoke、input mode 生命周期、`INPUT_RECORD` 分派、按钮区域记录与命中；`AgentCliProtocol.cs` 只保留主循环、渲染、文本输入逻辑。
- **边界点击偏差根因已确认**：conhost 中 `WINDOW_BUFFER_SIZE_EVENT` 的 stderr 日志会推动可见内容下移一行，导致 rawY 偏 +1；生产路径不在绘图阶段向 stderr 输出，该偏差不再出现。
- 保留 pipe / redirected stdin 禁用鼠标、Win32 input mode 设置、Quick Edit 禁用与恢复、文件日志调试策略。

## 一句话总结

让 `conhost.exe` 下的 CLI 用户直接用鼠标点击当前按钮提示中的按钮文字；鼠标和按钮区域统一使用 conhost buffer 坐标，键盘 `↑/↓ + Enter` 仍然作为 fallback。

## 范围

v1.4 只做 `conhost.exe` 下的原生控制台鼠标输入：

- 使用 Win32 `ReadConsoleInput()` 读取鼠标事件；
- 只处理左键按下；
- 只处理当前按钮提示中可见的按钮；
- 鼠标坐标使用 `MOUSE_EVENT_RECORD.dwMousePosition` 给出的 raw buffer 坐标；
- 按钮区域使用同一套 conhost buffer 坐标；
- 不实现 SGR mouse parser；
- 不实现 raw stdin mouse parser；
- 不实现非 Windows 鼠标路径；
- 不实现 Windows Terminal / conpty 坐标兼容。

SGR / raw stdin 的验证经验只作为历史背景，不进入 v1.4 实现范围。

## 目标终端约束

v1.4 明确只保证以下环境：

- Windows `conhost.exe`；
- 经典 Windows Console Host，例如 `cmd.exe` 或 PowerShell 5.1 的普通 console 窗口；
- `Console.IsInputRedirected == false` 的交互模式。

以下环境不属于 v1.4 验收范围：

- Windows Terminal；
- conpty；
- VS Code Integrated Terminal；
- 其他伪终端或终端复用器；
- pipe / redirected stdin 模式。

原因：

- 重新验证发现，`conhost.exe` 中 `MOUSE_EVENT_RECORD.dwMousePosition` 直接给出 console screen buffer 坐标；
- Windows Terminal / conpty 中该坐标语义可能不同；
- 若同时兼容两类终端，需要保留复杂 viewport 归一化与终端分支；
- v1.4 的目标是降低本次实现复杂度，优先保证 conhost 可用。

## 基本假设

v1.4 基于以下前提设计：

1. 等待输入时，鼠标只用于点击当前按钮提示中可见的按钮。
2. v1.4 MVP 假设按钮行不会因自动换行产生复杂多行区域；若某按钮因渲染宽度异常产生多行，仍按实际可见行记录 segment。
3. 用户不会手动操作滚动条；鼠标只用于点击当前可见按钮。
4. 游戏已有按钮过期逻辑，当前按钮提示只展示可点击按钮。
5. 按钮区域只记录当前按钮提示，不维护复杂 scrollback 坐标。
6. pipe / redirected stdin 模式不启用鼠标。
7. prompt 行只负责显示，不参与命中计算。

## 坐标系统

所有鼠标坐标和按钮区域坐标统一使用：

```text
0-based conhost console screen buffer coordinate
```

含义：

- 原点在当前 conhost console screen buffer 的左上角；
- row 向下增加；
- col 向右增加；
- row 包含当前 viewport 上方的 buffer offset；
- row 不等同于 viewport row；
- col 在 v1.4 MVP 中按实际渲染列计算，默认以 ASCII/半角按钮列为主要验证对象。

### Win32 鼠标坐标

v1.4 使用 `MOUSE_EVENT_RECORD.dwMousePosition`。

在目标平台 `conhost.exe` 下，该字段直接给出 console screen buffer 坐标：

```csharp
mouseRow = mouseRecord.dwMousePosition.Y;
mouseCol = mouseRecord.dwMousePosition.X;
```

v1.4 不做 viewport 归一化：

```csharp
// 不在 v1.4 命中路径中使用
row = mouseY - Console.WindowTop;
col = mouseX - Console.WindowLeft;
```

原因：

- conhost 下 raw `dwMousePosition` 已经是 buffer 坐标；
- 按钮区域也使用 buffer 坐标；
- 直接比较 raw buffer 坐标即可命中；
- 减去 `WindowTop` / `WindowLeft` 会把两类坐标混在一起，反而导致 conhost 下 miss。

### 坐标一致性原则

v1.4 禁止混用以下两类坐标：

```text
viewport row/col
buffer row/col
```

命中路径中只允许出现一种坐标：

```text
buffer row/col
```

因此：

- `Console.WindowTop` 可用于从 viewport 行推导 buffer 行；
- 鼠标命中时不转换鼠标坐标；
- 按钮区域记录时必须记录 buffer 行。

## 按钮区域记录

按钮区域使用 conhost buffer 坐标记录。

```csharp
private sealed class ButtonTerminalRegion
{
    public required int Row;       // 0-based conhost buffer row
    public required int Left;      // 0-based conhost buffer column, inclusive
    public required int Right;     // 0-based conhost buffer column, inclusive
    public required ConsoleButtonString Button;
    public required long Generation;
}
```

### 记录对象

只记录当前按钮提示中可点击的按钮：

```csharp
console.State == ConsoleState.WaitInput
req.InputType != InputType.EnterKey
req.InputType != InputType.AnyKey
btn.IsButton
btn.Generation == console.LastButtonGeneration
```

不记录：

- 已经过期的按钮；
- 当前请求不可点击的按钮；
- prompt 本身；
- 空白区域；
- scrollback 中的历史按钮；
- 非当前按钮提示中的按钮。

### 行号计算

v1.4 不使用 prompt 行作为命中锚点。

按钮区域行号直接来自按钮所在可见行的 buffer row：

```text
bufferRow = Console.WindowTop + visibleRowIndex
```

其中：

- `Console.WindowTop` 是当前 viewport 起始行在 console screen buffer 中的 row；
- `visibleRowIndex` 是该按钮行在当前可见 viewport 内的 0-based 行号；
- 结果 `bufferRow` 与 `dwMousePosition.Y` 使用同一坐标空间。

推荐区域刷新策略：

1. 读取当前 `Console.WindowTop`、`Console.WindowHeight`、`Console.WindowWidth`；
2. 确定当前可见行数，例如 `Math.Min(Console.WindowHeight - 1, lines.Count)`；
3. 从 `displayLineList` 末尾取对应数量的显示行；
4. 对每条显示行计算：

```csharp
bufferRow = Console.WindowTop + visibleRowIndex;
```

5. 将该行中的按钮区域记录为 buffer 坐标。

示例伪代码：

```csharp
int windowTop = Console.WindowTop;
int visibleLines = Math.Min(Console.WindowHeight - 1, lines.Count);
int startLine = Math.Max(0, lines.Count - visibleLines);

for (int i = 0; i < visibleLines; i++)
{
    int lineIndex = startLine + i;
    int visibleRowIndex = i;
    int bufferRow = windowTop + visibleRowIndex;

    // 必须用 FormatLineForTerminal 后的串计算列范围，与屏幕实际位置一致
    string formatted = console.FormatLineForTerminal(lines[lineIndex]);
    RecordButtonRegionsForLine(formatted, bufferRow, consoleWidth);
}
```

### 刷新时机

区域刷新必须发生在 `SyncButtonState()` 渲染完成**之后**，且在 `FullRefresh()` 重绘**之后**：

- `FullRefresh()` 会清屏并重写所有可见行，触发滚动，`WindowTop` 在其返回后才稳定；
- `SyncButtonState()` 可能调用 `RenderButtonPrompt()` 改变最后一行内容；
- 只有两者都完成后，`WindowTop` 与可见行布局才反映用户即将点击的真实屏幕。

主循环中的顺序：

```text
FullRefresh()          // 重绘
SyncButtonState()      // 渲染按钮 prompt
RefreshButtonRegions() // 此时 WindowTop 已稳定，记录区域
```

### 列宽计算基准

`RecordButtonRegionsForLine` 接收的字符串必须是 `console.FormatLineForTerminal(lines[lineIndex])` 的返回值，**不是原始 `displayLine`**：

- `FormatLineForTerminal` 会处理 ANSI 转义、宽字符截断等，改变实际显示宽度；
- 按原始 `displayLine` 计算的 `Left` / `Right` 会与屏幕实际位置错位；
- 列宽计算使用 `TerminalDisplayWidth.GetDisplayWidth`，与 `RenderButtonPrompt` / `OverwriteCountdownLine` 保持一致。

注意：

- 按终端可见行计算，不按游戏逻辑行计算；
- 如果按钮因自动换行占多行，每一行都记录一个 segment；
- prompt 行不参与区域计算；
- `RenderButtonPrompt()` 不需要捕获 `_buttonPromptRow`；
- 不再需要 `_lastRecordedButtonPromptRow` 之类的 prompt 行缓存。
- `RecordButtonRegionsForLine` 中若 `buttonWidth <= 0`，直接 `continue` 跳过该按钮，**不要累加 `column`**，避免后续按钮列范围偏移（见「已知限制」历史 bug 修复）。

## 鼠标事件处理

### 启用条件

满足以下条件时，鼠标始终启用（不使用 env-var 门控）：

```csharp
OperatingSystem.IsWindows()
&& !Console.IsInputRedirected
```

pipe 模式不启用鼠标，不解析鼠标事件，不改变现有文本输入逻辑。

### Console input mode 设置

读取鼠标事件前，必须调整 STD_INPUT_HANDLE 的 console input mode。

1. 用 `GetConsoleMode()` 读取并保存原始 input mode，退出时恢复。
2. 启用：

```text
ENABLE_MOUSE_INPUT
ENABLE_WINDOW_INPUT
ENABLE_EXTENDED_FLAGS
```

3. 禁用：

```text
ENABLE_QUICK_EDIT_MODE
```

注意事项：

- `ENABLE_QUICK_EDIT_MODE` 必须禁用，否则鼠标点击会先进入终端选择模式，事件到不了 `ReadConsoleInput()`；
- `ENABLE_EXTENDED_FLAGS` 必须与 `ENABLE_MOUSE_INPUT` / `ENABLE_WINDOW_INPUT` 同时设置，否则 Quick Edit 的改动不生效（Quick Edit 属于 extended flags）；
- 退出鼠标输入时用保存的原始 mode 调用 `SetConsoleMode()` 恢复，不手工拼回标志位，避免漏掉环境中其他扩展标志。

### 读取方式

采用**方案 D**：`GetNumberOfConsoleInputEvents` 非阻塞轮询 + `ReadConsoleInput` 统一分派。

主循环每次轮询时：

1. 调用 `GetNumberOfConsoleInputEvents(stdinHandle, out int count)`；
2. 若 `count == 0`，跳过输入处理，进入 `Thread.Sleep(PollIntervalMs)`；
3. 若 `count > 0`，循环 `count` 次调用 `ReadConsoleInput(stdinHandle, out INPUT_RECORD rec, ...)`，逐条按 `EventType` 分派。

```csharp
GetNumberOfConsoleInputEvents(stdin, out int count);
for (int i = 0; i < count; i++)
{
    ReadConsoleInput(stdin, out INPUT_RECORD rec, 1, out _);
    switch (rec.EventType)
    {
        case INPUT_RECORD.MOUSE_EVENT: HandleMouse(rec.MouseEvent); break;
        case INPUT_RECORD.KEY_EVENT:   HandleKey(rec.KeyEvent);   break;
        default: /* 忽略 */ break;
    }
}
```

**禁止在鼠标启用期间调用 `Console.KeyAvailable` / `Console.ReadKey()`**：.NET Console 在 Windows 内部会主动消费并丢弃非 key 事件（含 `MOUSE_EVENT_RECORD`），会吞掉鼠标事件。键盘事件必须从 `ReadConsoleInput` 的 `KEY_EVENT_RECORD` 分派。

pipe / redirected stdin 模式不进入此路径，仍走 `RunPipeCliLoop` 的 `TextReader.ReadLine`。

### 键盘事件映射

`KEY_EVENT_RECORD` 必须显式转换为 `ConsoleKeyInfo`，复用 `AgentCliProtocol.ProcessKey` 路径，避免重复实现按键逻辑。

#### 字段映射规则

```csharp
// KEY_EVENT_RECORD → ConsoleKeyInfo
ConsoleKey key = (ConsoleKey)keyEvent.wVirtualKeyCode;       // 直接映射虚拟键码
char keyChar = keyEvent.uChar.UnicodeChar;                   // 取 Unicode 字符
ConsoleModifiers mods = MapControlKeyState(keyEvent.dwControlKeyState);
var keyInfo = new ConsoleKeyInfo(keyChar, key, mods.HasFlag(ConsoleModifiers.Shift),
                                 mods.HasFlag(ConsoleModifiers.Alt),
                                 mods.HasFlag(ConsoleModifiers.Control));
```

`dwControlKeyState` → `ConsoleModifiers` 映射：

| `dwControlKeyState` 标志 | `ConsoleModifiers` |
|---|---|
| `SHIFT_PRESSED` | `Shift` |
| `LEFT_ALT_PRESSED` / `RIGHT_ALT_PRESSED` | `Alt` |
| `LEFT_CTRL_PRESSED` / `RIGHT_CTRL_PRESSED` | `Control` |

#### 释放事件过滤

`KEY_EVENT_RECORD.bKeyDown == false` 的释放事件一律忽略，否则每个按键会触发两次 `ProcessKey`。

```csharp
if (!keyEvent.bKeyDown) return;
```

#### Ctrl+C 处理

鼠标启用期间 `Console.CancelKeyPress` 不再可靠触发（因为不再走 `Console.ReadKey`）。必须在 `KEY_EVENT_RECORD` 分派路径手动检测：

```csharp
if (keyEvent.wVirtualKeyCode == (ushort)ConsoleKey.C
    && (keyEvent.dwControlKeyState & (LEFT_CTRL_PRESSED | RIGHT_CTRL_PRESSED)) != 0)
{
    // 派发 0x03 或触发退出
}
```

退出路径仍需恢复 console input mode（见「清理要求」）。

#### IME 组合输入

IME 组合过程中 `KEY_EVENT_RECORD.uChar.UnicodeChar` 可能为 `0`。处理策略：

- `UnicodeChar == 0` 且非修饰键（Ctrl/Alt/Shift 单独按下）时，跳过本次 `ProcessKey` 派发；
- IME 提交时会产生 `UnicodeChar != 0` 的最终字符事件，正常派发；
- 不实现 IME 组合中间态的可见回显，保持与现有 `ProcessChar` 行为一致。

#### 与 `ProcessKey` 的对接

转换后的 `ConsoleKeyInfo` 直接传入 `AgentCliProtocol.ProcessKey(ConsoleKeyInfo)`，复用现有 `Enter` / `Backspace` / `Escape` / 字符输入 / 按钮模式 `↑/↓/Enter` 分支，不重复实现按键语义。

### 接受的事件

只接受左键按下事件：

```text
dwButtonState 包含 FROM_LEFT_1ST_BUTTON_PRESSED
dwEventFlags == 0
```

### 忽略的事件

忽略：

- 鼠标移动；
- 鼠标释放；
- 滚轮；
- 右键；
- 中键；
- double-click 噪声；
- 坐标越界事件；
- `WINDOW_BUFFER_SIZE_EVENT`（conhost 在绘图阶段会触发，生产路径不处理，避免引入坐标偏移）；
- `MENU_EVENT` / `FOCUS_EVENT` 等其他事件类型。

释放事件不会重复提交。

## 调试输出

- 调试输出默认关闭。
- 可通过 env var `EMUERA_MOUSE_LOG=1` 开启，仅用于开发 / 验证，不属于生产用户接口。
- 开启时写入文件（默认 `<ExeDir>/debug/mouse.log`，追加模式），**不写 `Console.Error`，不写 `stdout`**。
- 单一开关统一控制：原 spike 阶段的 `EMUERA_DEBUG_MOUSE` / `_VERBOSE` / `_KEYS` / `_REGIONS` / `EMUERA_ENABLE_MOUSE_CLICK` 五个细分开关已合并为 `EMUERA_MOUSE_LOG` 一个，开启后所有鼠标 / 按键 / 区域日志都写入同一文件。
- 原因：Step 3 / Step 4 验证中发现，`Console.Error` 在 PowerShell 终端显示中会与 `stdout` 视觉交错，既干扰判断游戏 `stdout` 是否被污染，也容易把终端回显误判为程序输出。
- 关闭调试时，生产路径不向终端输出任何鼠标相关信息。

## 点击命中规则

鼠标坐标使用 raw conhost buffer 坐标。

1. 如果当前没有有效按钮区域，忽略。
2. 在当前 `_buttonRegions` 中查找：

```text
region.Row == mouseRow
region.Left <= mouseCol <= region.Right
```

3. 如果多个区域重叠，选择最后记录的按钮。
4. 找到按钮后提交：

```csharp
string input = btn.IsInteger ? btn.Input.ToString() : btn.Inputs;
DispatchInput(input);
```

5. 点击空白不产生输入、不 echo、不改变游戏状态。

鼠标点击直接提交，不需要 `Enter`。

### 命中日志建议

调试日志中可以同时记录 raw buffer 坐标和命中的按钮区域，便于验证：

```text
hit bufferRow=127 bufferCol=9 regionRow=127 regionCol=8-29 input=0
miss bufferRow=128 bufferCol=9 regionRows=126,127
```

## 与键盘输入的关系

- 鼠标和键盘可以同时启用，统一从 `ReadConsoleInput` 读取（方案 D）。
- 鼠标点击按钮直接提交。
- `↑/↓` 继续切换当前按钮。
- `Enter` 继续确认当前按钮。
- 输入事件按实际到达顺序处理。
- 鼠标提交后，后续按钮状态交给游戏现有按钮过期逻辑处理。
- 鼠标启用期间禁止调用 `Console.KeyAvailable` / `Console.ReadKey()`，键盘事件通过 `KEY_EVENT_RECORD → ConsoleKeyInfo → ProcessKey` 派发（见「键盘事件映射」）。

## 不清理复杂区域的原因

v1.4 不单独设计复杂的按钮区域过期策略。

原因：

- 游戏已有按钮过期逻辑；
- 当前按钮提示只展示可点击按钮；
- v1.4 只记录当前按钮提示；
- 不维护 scrollback 中的历史按钮区域；
- 区域刷新时直接覆盖当前可见按钮行。

因此按钮区域的生命周期跟随当前按钮提示，而不是跟随完整终端历史。

## 不做

v1.4 不做：

- SGR mouse parser；
- raw stdin mouse parser；
- 非 Windows 鼠标支持；
- Windows Terminal / conpty 坐标兼容；
- prompt 换行后的完整区域重建；
- 用户手动滚动 scrollback 后的命中修正；
- hover 高亮；
- 拖拽；
- 滚轮；
- 右键；
- 中键；
- 模拟 `PrimitiveMouseKey`；
- pipe 模式鼠标支持；
- env-var 门控鼠标启用（满足启用条件即默认启用）；
- 生产路径 stdout / stderr 鼠标调试输出。

## 清理要求

退出鼠标输入时：

- 停止读取 Win32 console input；
- 用保存的原始 input mode 调用 `SetConsoleMode()` 恢复，包括恢复 `ENABLE_QUICK_EDIT_MODE`；
- 清空当前按钮区域；
- 不向 stdout / `Console.Error` 输出鼠标调试信息。

pipe 模式不调用鼠标启用 / 禁用逻辑。

## 已知限制 / 待验证

以下属于 v1.4 范围内但尚未完全覆盖，应记录为限制：

- **边界点击**：已在 conhost 实测（见 `coordinate-validation-results.md` 用例 6a/6b）。左上角 `(0,0)` 命中正常；右下角存在 `normalizedRow = WindowHeight` 的 +1 偏差（点击窗口最底部边缘像素映射到 viewport 下方一行），属极端边界情况，不影响生产路径。早期 spike 中观察到的 rawY +1 偏差根因已确认：conhost 中 `WINDOW_BUFFER_SIZE_EVENT` 的 stderr 日志会推动可见内容下移一行，生产路径不在绘图阶段向 stderr 输出，该偏差不再出现。
- **全角字符 / ANSI 按钮列宽**：当前主要验证 ASCII 按钮（`[0] Hello` 等）的列范围。含全角字符（占 2 列）或 ANSI 转义的按钮，列范围计算需按实际渲染宽度处理。v1.4 已要求基于 `FormatLineForTerminal` 后的串计算列宽，但全角场景未系统实测。
- **prompt 换行**：v1.4 不依赖 prompt 行命中，但按钮区域仍基于当前可见行记录；如果按钮提示自身换行，需要按实际显示行记录 segment。
- **手动滚动 scrollback**：未实现滚动后命中修正；v1.4 只保证当前可见按钮。
- **Windows Terminal / conpty**：不作为 v1.4 验收目标。
- **IME 组合中间态**：v1.4 只处理 IME 提交后的最终字符事件，不实现组合中间态的可见回显。

## 验收标准

### 功能验收

- 在 `conhost.exe` / PowerShell 5.1 console 中，点击当前按钮提示中的按钮会提交对应输入。
- 点击 `[0] Hello` 提交 `0`。
- 点击 `[1] Quit` / `[1] Exit` 提交 `1`。
- 点击空白不产生输入。
- 键盘 `↑/↓ + Enter` 行为保持不变。
- pipe 模式行为保持不变。

### 稳定性验收

- `dwMousePosition` raw buffer 坐标直接参与命中。
- 鼠标释放不会重复提交。
- 旧按钮提示不会继续参与命中。
- 坐标越界不会导致异常。
- 鼠标功能失败时静默 fallback 到键盘。
- 退出后 console input mode 恢复（含 Quick Edit）。
- **键盘事件在鼠标启用期间仍能正常派发**：`↑/↓/Enter/Esc` 及字符输入通过 `KEY_EVENT_RECORD → ConsoleKeyInfo → ProcessKey` 路径生效，不因 `ReadConsoleInput` 统一消费队列而丢失或重复触发。
- `KEY_EVENT_RECORD.bKeyDown == false` 的释放事件不触发 `ProcessKey`。
- `Ctrl+C` 在鼠标启用期间能触发退出，不依赖 `Console.CancelKeyPress`。

### 兼容性验收

- redirected stdin 下不启用鼠标。
- 不支持鼠标的终端不报错。
- 不改变 JSONL、server、WinForms 行为。
- Windows Terminal / conpty 不作为本 MVP 保证目标。

### 调试验收

- 默认情况下，stdout / `Console.Error` 无任何鼠标调试输出。
- 调试开启时，鼠标信息只出现在文件中，终端显示不被污染。
