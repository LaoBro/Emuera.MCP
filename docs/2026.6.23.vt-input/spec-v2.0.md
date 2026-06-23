# CLI 虚拟终端鼠标输入 — v2.0 规格

## 版本信息

- 版本：v2.0
- 基线：`docs/2026.6.16.mouse-input/spec-v1.4.md`
- 关联验证：`docs/2026.6.23.vt-input/validation-plan-v2.0.md`
- 目标文件：
  - 新建：`Emuera.Headless/Agent/AgentCliVtInput.cs`
  - 新建：`Emuera.Headless/Agent/AgentCliVtScreen.cs`
  - 改造：`Emuera.Headless/Agent/AgentCliProtocol.cs`
  - 改造：`Emuera.Headless/Agent/TerminalCursor.cs`
  - 标记废弃：`Emuera.Headless/Agent/AgentCliMouseInput.cs`
  - 不动：`Emuera.Headless/Agent/ButtonRegionTracker.cs`
  - 不动：`Emuera.Headless/Agent/TerminalDisplayWidth.cs`
  - 不动：`Emuera.Headless/UI/Game/EmueraConsole.AgentBridge.cs`
- 目标平台：Windows（Windows Terminal + conhost）
- 输入 backend：raw stdin + VT 解析（SGR mouse + UTF-8 键盘）
- 渲染 backend：备用屏幕缓冲区 + 绝对定位

## 相对 v1.4 的变更

- 目标平台从 `conhost.exe` 收窄扩展为 Windows 下所有支持 VT 的终端（Windows Terminal + conhost）。
- 输入 backend 从 Win32 `ReadConsoleInputW` 改为 raw stdin + VT 序列解析。
- 坐标模型从 conhost console screen buffer 坐标改为 viewport 坐标（备用屏下 `WindowTop` 恒为 0）。
- 渲染层从 `Console.WriteLine` 自然滚动改为备用屏 + 绝对定位。
- 按钮区域记录从 `bufferRow = Console.WindowTop + visibleRowIndex` 改为 `region.Row = visibleRowIndex`。
- 删除 v1.4 的 `Console.WindowTop` 依赖、`WINDOW_BUFFER_SIZE_EVENT` stderr 偏差规避、conhost buffer 坐标归一化逻辑。
- 新增终端能力探测（DA1 查询），不支持 VT 时降级为纯键盘模式。
- 新增备用屏生命周期管理（`ESC[?1049h/l`）。
- 新增 SGR mouse tracking 生命周期管理（`1000h + 1006h`）。
- 新增 raw input mode 生命周期管理（Windows `SetConsoleMode`，Unix termios 本次不实现）。
- `AgentCliMouseInput.cs` 标记 `[Obsolete]` + 文件头注释，不接入主循环，保留代码供参考。
- pipe / redirected stdin 模式仍走 `RunPipeCliLoop`，不启用 VT。
- Unix/Linux/macOS 路径本次不实现，标注为已知限制。

## 一句话总结

让 Windows 下支持 VT 的终端用户直接用鼠标点击当前按钮提示中的按钮文字；统一使用 viewport 坐标，键盘 `↑/↓ + Enter` 仍然作为 fallback。

## 范围

v2.0 只做 Windows 下支持 VT 的终端的鼠标输入：

- 使用 raw stdin 读取 VT 序列；
- 解析 SGR mouse `1000h + 1006h` 格式；
- 只处理左键按下；
- 只处理当前按钮提示中可见的按钮；
- 鼠标坐标使用 SGR mouse 给出的 viewport 坐标；
- 按钮区域使用同一套 viewport 坐标；
- 使用备用屏幕缓冲区，`WindowTop` 恒为 0；
- 渲染使用绝对定位，不依赖自然滚动；
- 不实现 Unix termios 路径；
- 不实现 SGR mouse `1002h`/`1003h`（移动/拖拽）；
- 不实现 hover 高亮、滚轮、右键、中键；
- 不实现内部 scrollback；
- 不实现 IME 组合态可见回显。

## 目标终端约束

v2.0 保证以下环境：

- Windows Terminal；
- Windows `conhost.exe`（经典 Windows Console Host，VT 模式）；
- `Console.IsInputRedirected == false` 的交互模式；
- 终端响应 DA1 查询（`ESC[c`）。

以下环境不属于 v2.0 验收范围：

- Unix/Linux/macOS 终端（termios 路径未实现）；
- VS Code Integrated Terminal（未单独验证）；
- 其他伪终端或终端复用器（未验证）；
- pipe / redirected stdin 模式。

原因：

- v2.0 优先保证 Windows 下 VT 路径可用；
- Unix termios 路径需要单独验证 `poll`/`read` 与 SGR mouse 的兼容性；
- pipe 模式无终端，不启用 VT。

## 基本假设

v2.0 基于以下前提设计：

1. 等待输入时，鼠标只用于点击当前按钮提示中可见的按钮。
2. v2.0 MVP 假设按钮行不会因自动换行产生复杂多行区域；若某按钮因渲染宽度异常产生多行，仍按实际可见行记录 segment。
3. 用户不会手动操作滚动条；备用屏无 scrollback，鼠标只用于点击当前可见按钮。
4. 游戏已有按钮过期逻辑，当前按钮提示只展示可点击按钮。
5. 按钮区域只记录当前按钮提示，不维护复杂 scrollback 坐标。
6. pipe / redirected stdin 模式不启用 VT。
7. prompt 行只负责显示，不参与命中计算。
8. 备用屏下 `Console.WindowTop` 恒为 0，viewport 坐标与 buffer 坐标等价。

## 终端能力探测

### DA1 查询

启动时发送 DA1（Primary Device Attributes）查询：

```text
ESC[c
```

等待 200ms，若收到任何以 `ESC[` 开头、以 `c` 结尾的响应，判定终端支持 VT。

### 探测流程

```text
1. 发送 ESC[c
2. 轮询 stdin 200ms，收集响应字节
3. 若收到 ESC[...c 响应 → VT 可用
4. 若超时无响应 → VT 不可用，降级
```

### 降级路径

DA1 探测失败时：

- 不进入备用屏；
- 不启用 SGR mouse tracking；
- 不启用 raw input mode；
- 主循环走 `Console.ReadKey(true)` 纯键盘路径；
- 保留 `↑/↓ + Enter` 按钮选择 fallback；
- 按钮区域不记录，鼠标点击无效。

降级路径不调用任何 VT 序列，避免在不支持 VT 的终端上污染输出。

## 备用屏幕缓冲区

### 进入时机

DA1 探测通过后，`RunCliLoop` 启动时立即发送：

```text
ESC[?1049h
```

整个 CLI 生命周期都在备用屏。

### 退出时机

`RunCliLoop` 退出时（正常退出、异常退出、取消）发送：

```text
ESC[?1049l
```

退出备用屏后，主屏恢复用户进入前内容，备用屏内容丢弃。

### 坐标语义

备用屏下：

- `Console.WindowTop` 恒为 0；
- `Console.WindowHeight` 等于终端可见行数；
- SGR mouse 的 `Cx/Cy` 是 viewport 坐标（0-based）；
- 按钮区域使用 viewport 坐标；
- 渲染使用 viewport 坐标绝对定位。

## 输入架构

### 主循环轮询模型

v2.0 放弃后台 Task + Channel 模型，改用主循环轮询：

```text
while (!token.IsCancellationRequested)
{
    if (vtInput.HasInputAvailable())
    {
        int b = vtInput.ReadByte();
        vtParser.Feed(b);   // 解析为 KeyEvent / MouseEvent，直接派发
    }
    else
    {
        CheckInputTimeout();
        Thread.Sleep(PollIntervalMs);
    }

    FullRefreshIfNeeded();
    SyncButtonState();
    RefreshButtonRegions();
}
```

### 设计理由

- `CancellationToken` 无法中断阻塞的 `ReadByte`，后台 Task 模型在退出时有死锁风险；
- 主循环轮询模型无后台线程、无跨线程同步、无取消死锁；
- VT 解析器在主循环内运行，解析结果直接派发，无 Channel 开销；
- 退出时 `token.IsCancellationRequested = true`，主循环自然退出，终端恢复干净。

### 平台分支

`HasInputAvailable()` 的实现按平台分支，对外 API 统一：

```csharp
internal abstract bool HasInputAvailable();
internal abstract int ReadByte();
```

#### Windows 实现

```csharp
internal sealed class WindowsVtInput : AgentCliVtInput
{
    // 用 GetNumberOfConsoleInputEvents 查询输入队列
    // 用 ReadFile 读取 raw bytes（不经过 .NET Console 内部缓冲）
    // 关键：不调用 Console.KeyAvailable / Console.ReadKey，避免吞掉 SGR mouse 序列
}
```

Windows 下 `GetNumberOfConsoleInputEvents` 返回值 > 0 时，`ReadFile` 立即返回 raw bytes。

#### Unix 实现

本次不实现，`HasInputAvailable()` 直接返回 `false`，触发降级路径：

```csharp
internal sealed class UnixVtInput : AgentCliVtInput
{
    // 本次不实现，return false 触发降级
    internal override bool HasInputAvailable() => false;
}
```

Unix 路径作为已知限制，后续版本用 `poll` + `read` 实现。

### raw input mode 设置

读取 VT 序列前，必须调整 stdin 的 input mode。

#### Windows

用 `GetConsoleMode()` 读取并保存原始 input mode，退出时恢复。

启用：

```text
ENABLE_VIRTUAL_TERMINAL_INPUT
```

禁用：

```text
ENABLE_PROCESSED_INPUT
ENABLE_ECHO_INPUT
ENABLE_LINE_INPUT
ENABLE_QUICK_EDIT_MODE
```

注意事项：

- `ENABLE_VIRTUAL_TERMINAL_INPUT` 让终端把 SGR mouse 序列作为 raw bytes 传递给 stdin；
- 关闭 `ENABLE_PROCESSED_INPUT` / `ENABLE_ECHO_INPUT` / `ENABLE_LINE_INPUT` 避免终端回显 SGR 序列、避免 .NET Console 内部缓冲消费；
- 关闭 `ENABLE_QUICK_EDIT_MODE` 避免鼠标点击进入终端选择模式；
- `ENABLE_EXTENDED_FLAGS` 必须与上述标志同时设置，否则 Quick Edit 的改动不生效；
- 退出时用保存的原始 mode 调用 `SetConsoleMode()` 恢复。

#### Unix

本次不实现 termios 路径。

### SGR mouse tracking

启用鼠标追踪：

```text
ESC[?1000h   // 按钮事件报告
ESC[?1006h   // SGR 格式
```

禁用鼠标追踪（退出时）：

```text
ESC[?1006l
ESC[?1000l
```

## VT 输入解析

### 解析器状态机

VT 解析器接收 raw bytes，输出两类事件：

- `KeyEvent`：键盘输入；
- `MouseEvent`：鼠标输入。

#### 状态机结构

```text
状态：
  GROUND          // 普通字符
  ESC             // 收到 ESC
  CSI             // 收到 ESC[
  SGR_MOUSE       // 收到 ESC[<（SGR mouse 序列）
  DA1_RESPONSE    // 收到 ESC[（DA1 响应，探测阶段消费）
```

#### GROUND 状态

- 收到 `0x1B` (ESC) → 进入 ESC 状态；
- 收到其他字节 → 作为 UTF-8 字节流缓冲，完整字符后输出 `KeyEvent`。

#### ESC 状态

- 收到 `[` → 进入 CSI 状态；
- 收到其他 → 作为 ESC + 字符处理（如 `ESC` 单独作为按键）。

#### CSI 状态

- 收到 `<` → 进入 SGR_MOUSE 状态；
- 收到 `?` → 记录为 DA1 响应前缀（探测阶段消费）；
- 收到其他 → 收集参数，遇到终结字节（`@`-`~`）时按 CSI 序列处理。

#### SGR_MOUSE 状态

解析 `ESC[<Cb;Cx;Cy M/m` 格式：

- `Cb`：按钮编号（0=左键按下，2=左键释放）；
- `Cx`：列坐标（1-based，需减 1）；
- `Cy`：行坐标（1-based，需减 1）；
- `M`：按下事件；
- `m`：释放事件。

解析完成后输出 `MouseEvent`，回到 GROUND 状态。

### UTF-8 键盘处理

GROUND 状态下收集 UTF-8 字节流：

- 首字节判断字符长度（1/2/3/4 字节）；
- 收集完整字符后输出 `KeyEvent`；
- 不完整字符继续缓冲。

### Ctrl+C 处理

raw input mode 下 `Console.CancelKeyPress` 不再可靠触发。在 GROUND 状态检测 `0x03`：

```csharp
if (b == 0x03)
{
    // 触发退出路径
}
```

退出路径仍需恢复终端（见「清理要求」）。

### IME 组合输入

IME 组合过程中可能产生 `UnicodeChar == 0` 的中间态事件。处理策略：

- IME 组合中间态字节跳过，不派发 `KeyEvent`；
- IME 提交时产生完整 UTF-8 字符，正常派发；
- 不实现 IME 组合中间态的可见回显，保持与 v1.4 行为一致。

### 与 ProcessKey 的对接

VT 解析器输出的 `KeyEvent` 转换为 `ConsoleKeyInfo`，直接传入 `AgentCliProtocol.ProcessKey(ConsoleKeyInfo)`，复用现有 `Enter` / `Backspace` / `Escape` / 字符输入 / 按钮模式 `↑/↓/Enter` 分支。

方向键等特殊键的 VT 序列（如 `ESC[A` = ↑）在 CSI 状态解析后映射为对应 `ConsoleKey`。

## 鼠标事件处理

### 接受的事件

只接受左键按下事件：

```text
Cb == 0
M 终结符（按下）
```

### 忽略的事件

忽略：

- 鼠标移动（`1000h` 不报告移动，但偶发噪声忽略）；
- 鼠标释放（`m` 终结符）；
- 滚轮；
- 右键；
- 中键；
- double-click 噪声；
- 坐标越界事件。

### 鼠标坐标

SGR mouse 的 `Cx/Cy` 是 viewport 坐标（1-based），解析时减 1 转为 0-based：

```csharp
int mouseRow = cy - 1;
int mouseCol = cx - 1;
```

备用屏下 viewport 坐标与按钮区域坐标等价，直接比较即可命中。

## 渲染层

### 新建 AgentCliVtScreen.cs

封装所有 VT 渲染操作，`AgentCliProtocol` 调用新 API：

```csharp
internal sealed class AgentCliVtScreen : IDisposable
{
    // 备用屏生命周期
    void EnterAlternateScreen();
    void LeaveAlternateScreen();

    // 绝对定位渲染
    void ClearScreen();
    void SetCursor(int row, int col);
    void ClearLine(int row);
    void WriteAt(int row, int col, string text);
    void WriteLineAt(int row, string text);  // = SetCursor(row,0) + Write + ClearLineToEnd

    // 尺寸查询
    int WindowWidth { get; }
    int WindowHeight { get; }
}
```

### 渲染策略

混合策略：

- `FullRefresh`：全量绝对定位重绘，用 `ESC[{row};1H` + `ESC[K` 逐行定位重写；
- `EraseTerminalRows` / `OverwriteCountdownLine` / `RenderButtonPrompt`：增量原地更新，用绝对定位 + `ESC[K` 清行。

### FullRefresh 改造

```csharp
private void FullRefresh()
{
    _screen.ClearScreen();

    var lines = console.DisplayLineList;
    if (lines.Count == 0) return;

    int consoleHeight = _screen.WindowHeight;
    int visibleLines = Math.Max(consoleHeight - 1, 1);
    int startLine = Math.Max(0, lines.Count - visibleLines);

    for (int i = 0; i < visibleLines && (startLine + i) < lines.Count; i++)
    {
        int lineIndex = startLine + i;
        int viewportRow = i;
        string formatted = console.FormatLineForTerminal(lines[lineIndex]);
        _screen.WriteLineAt(viewportRow, formatted.Length > 0 ? formatted : "");
    }
}
```

关键变化：

- 用 `_screen.WriteLineAt(viewportRow, ...)` 代替 `Console.WriteLine`；
- `viewportRow` 直接作为渲染行号，不依赖 `WindowTop`；
- 不触发自然滚动。

### EraseTerminalRows 改造

```csharp
private void EraseTerminalRows()
{
    int rows = console._pendingEraseRows;
    if (rows <= 0) return;
    console._pendingEraseRows = 0;

    int consoleWidth = _screen.WindowWidth;
    int currentRow = _screen.GetCurrentRow();  // 或由调用方传入

    for (int i = 0; i < rows; i++)
    {
        int targetRow = currentRow - 1 - i;
        if (targetRow < 0) break;
        _screen.ClearLine(targetRow);
    }

    _screen.SetCursor(0, Math.Max(currentRow - rows, 0));
}
```

### OverwriteCountdownLine 改造

```csharp
private void OverwriteCountdownLine(string newText)
{
    if (_countdownLineRow < 0) return;

    string padded = PadToWidth(newText, _lastCountdownWidth, out int newWidth);

    _screen.WriteLineAt(_countdownLineRow, padded);

    _lastCountdownText = newText;
    _lastCountdownWidth = Math.Max(newWidth, _lastCountdownWidth);
}
```

`_countdownLineTop` 改名为 `_countdownLineRow`，语义从 buffer row 改为 viewport row。

### RenderButtonPrompt 改造

```csharp
private void RenderButtonPrompt()
{
    if (_selectedButtonIndex < 0 || _selectedButtonIndex >= _currentButtons.Count) return;

    var btn = _currentButtons[_selectedButtonIndex];
    string prompt = $"> [{_selectedButtonIndex + 1}/{_currentButtons.Count}] {btn} | [Up/Dn] Switch  [Enter] OK";

    string padded = PadToWidth(prompt, _buttonPromptWidth, out int newWidth);
    _screen.WriteLineAt(_buttonPromptRow, "\r" + padded);
    _buttonPromptWidth = Math.Max(newWidth, _buttonPromptWidth);
}
```

`_buttonPromptRow` 由 `SyncButtonState` 在渲染前记录当前 viewport row。

### resize 检测

主循环每次轮询时检查 `Console.WindowWidth` / `Console.WindowHeight`：

```csharp
private int _lastWindowWidth = -1;
private int _lastWindowHeight = -1;

private bool CheckResize()
{
    int w = _screen.WindowWidth;
    int h = _screen.WindowHeight;
    if (w == _lastWindowWidth && h == _lastWindowHeight) return false;
    _lastWindowWidth = w;
    _lastWindowHeight = h;
    return true;
}
```

变化时触发 `FullRefresh` + `RefreshButtonRegions`。

## 按钮区域记录

### 坐标系统

按钮区域使用 viewport 坐标：

```csharp
private sealed class ButtonTerminalRegion
{
    public required int Row;       // 0-based viewport row
    public required int Left;      // 0-based viewport column, inclusive
    public required int Right;     // 0-based viewport column, inclusive
    public required ConsoleButtonString Button;
    public required long Generation;
}
```

### 行号计算

```text
viewportRow = visibleRowIndex
```

其中 `visibleRowIndex` 是该按钮行在当前可见 viewport 内的 0-based 行号。

备用屏下 `WindowTop` 恒为 0，`viewportRow` 与 SGR mouse 的 `Cy - 1` 使用同一坐标空间。

### 刷新时机

区域刷新必须发生在 `SyncButtonState()` 渲染完成**之后**，且在 `FullRefresh()` 重绘**之后**：

```text
FullRefresh()          // 重绘
SyncButtonState()      // 渲染按钮 prompt
RefreshButtonRegions() // 记录区域
```

### 刷新逻辑

```csharp
private void RefreshButtonRegions(bool force = false)
{
    long currentGen = console.LastButtonGeneration;
    if (!force && currentGen == _lastRegionGeneration) return;

    _lastRegionGeneration = currentGen;
    _vtInput.ClearRegions();

    var lines = console.DisplayLineList;
    if (lines == null || lines.Count == 0) return;

    int windowHeight = _screen.WindowHeight;
    int visibleLines = Math.Min(windowHeight - 1, lines.Count);
    int startLine = Math.Max(0, lines.Count - visibleLines);

    for (int i = 0; i < visibleLines; i++)
    {
        int lineIndex = startLine + i;
        var line = lines[lineIndex];
        if (line?.Buttons == null || line.Buttons.Length == 0) continue;

        string formatted = console.FormatLineForTerminal(line);
        _vtInput.RecordLineRegions(formatted, i, line.Buttons, currentGen);
    }
}
```

关键变化：

- `bufferRow = windowTop + i` 改为 `viewportRow = i`；
- 删除 `Console.WindowTop` 读取。

### 列宽计算基准

`RecordLineRegionsForLine` 接收的字符串必须是 `console.FormatLineForTerminal(lines[lineIndex])` 的返回值，**不是原始 `displayLine`**。

列宽计算使用 `TerminalDisplayWidth.GetDisplayWidth`，与 `RenderButtonPrompt` / `OverwriteCountdownLine` 保持一致。

注意：

- 按终端可见行计算，不按游戏逻辑行计算；
- 如果按钮因自动换行占多行，每一行都记录一个 segment；
- prompt 行不参与区域计算；
- `RecordButtonRegionsForLine` 中若 `buttonWidth <= 0`，直接 `continue` 跳过该按钮，**不要累加 `column`**，避免后续按钮列范围偏移。

## 点击命中规则

鼠标坐标使用 viewport 坐标。

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

5. 点击空白处理：
   - `buttonMode == true`：不提交，不 echo，不改变游戏状态（保护用户）；
   - `buttonMode == false`：提交空字符串（与 v1.4 `DispatchMouseMiss` 一致）。

鼠标点击直接提交，不需要 `Enter`。

### 命中日志建议

调试日志中同时记录 viewport 坐标和命中的按钮区域：

```text
hit row=5 col=9 regionRow=5 regionCol=8-29 input=0
miss row=6 col=9 regionRows=5,6
```

## 与键盘输入的关系

- 鼠标和键盘统一从 raw stdin 读取，VT 解析器分派。
- 鼠标点击按钮直接提交。
- `↑/↓` 继续切换当前按钮。
- `Enter` 继续确认当前按钮。
- 输入事件按实际到达顺序处理。
- 鼠标提交后，后续按钮状态交给游戏现有按钮过期逻辑处理。
- VT 模式下不调用 `Console.KeyAvailable` / `Console.ReadKey()`（降级路径除外）。

## 输入超时

主循环轮询超时模型：

```csharp
while (!token.IsCancellationRequested)
{
    if (vtInput.HasInputAvailable())
    {
        int b = vtInput.ReadByte();
        vtParser.Feed(b);
    }
    else
    {
        // 检查输入超时
        var timeoutMs = console.InputTimeoutMs;
        if (timeoutMs.HasValue && timeoutMs.Value <= 0)
        {
            OverwriteCountdownLine(console.TimeUpMessage ?? "");
            ResetCountdown();
            ClearInputBuffer();
            console.SubmitTimeout();
            FlushBuffer();
            SyncButtonState();
            RefreshButtonRegions();
            continue;
        }
        UpdateCountdown();
        Thread.Sleep(PollIntervalMs);
    }

    FullRefreshIfNeeded();
    SyncButtonState();
    RefreshButtonRegions();
}
```

## 调试输出

- 调试输出默认关闭。
- 可通过 env var `EMUERA_MOUSE_LOG=1` 开启，仅用于开发 / 验证，不属于生产用户接口。
- 开启时写入文件（默认 `<ExeDir>/debug/mouse.log`，追加模式），**不写 `Console.Error`，不写 `stdout`**。
- 单一开关统一控制：所有鼠标 / 按键 / 区域日志都写入同一文件。
- 关闭调试时，生产路径不向终端输出任何鼠标相关信息。

## 清理要求

### 正常退出

退出鼠标输入时，按严格顺序：

1. 禁用 SGR mouse tracking：`ESC[?1006l` + `ESC[?1000l`；
2. 恢复原始 input mode（Windows `SetConsoleMode`）；
3. 退出备用屏：`ESC[?1049l`；
4. 清空当前按钮区域。

用 `try/finally` 保证执行。

### 异常退出

注册多个生命周期钩子保障终端恢复：

- `Console.CancelKeyPress`（Ctrl+C）；
- `AppDomain.CurrentDomain.UnhandledException`；
- `AppDomain.CurrentDomain.ProcessExit`。

每个钩子都调用同一个 `Dispose` 路径，确保异常情况下终端也能恢复。

### pipe 模式

pipe 模式不调用 VT 启用 / 禁用逻辑，仍走 `RunPipeCliLoop`。

## 不清理复杂区域的原因

v2.0 不单独设计复杂的按钮区域过期策略。

原因：

- 游戏已有按钮过期逻辑；
- 当前按钮提示只展示可点击按钮；
- v2.0 只记录当前按钮提示；
- 不维护 scrollback 中的历史按钮区域；
- 区域刷新时直接覆盖当前可见按钮行。

按钮区域的生命周期跟随当前按钮提示，而不是跟随完整终端历史。

## 不做

v2.0 不做：

- Unix termios 路径（标注待验证）；
- 内部 scrollback / `PageUp/Down`；
- SGR mouse `1002h`/`1003h`（移动/拖拽）；
- hover 高亮；
- 拖拽；
- 滚轮；
- 右键；
- 中键；
- 模拟 `PrimitiveMouseKey`；
- pipe 模式鼠标支持；
- IME 组合态可见回显；
- 终端能力探测的细粒度回退（DA1 失败直接降级键盘）；
- 生产路径 stdout / stderr 鼠标调试输出；
- Windows Terminal 与 conhost 之外的终端专项适配。

## 已知限制 / 待验证

以下属于 v2.0 范围内但尚未完全覆盖，应记录为限制：

- **Unix 路径未实现**：`UnixVtInput.HasInputAvailable()` 直接返回 `false`，触发降级。后续版本用 `poll` + `read` 实现。
- **无 scrollback**：备用屏固有特性，用户失去终端原生 scrollback，无法用滚动条查看历史。
- **鼠标功能有限**：不支持 hover 高亮、拖拽、滚轮、右键、中键。
- **IME 组合态无可见回显**：IME 组合过程中不显示预览字符串，提交后直接派发最终字符。
- **全角字符列宽未系统实测**：当前主要验证 ASCII 按钮（`[0] Hello` 等）的列范围。含全角字符（占 2 列）或 ANSI 转义的按钮，列范围计算需按实际渲染宽度处理。v2.0 已要求基于 `FormatLineForTerminal` 后的串计算列宽，但全角场景未系统实测。
- **prompt 换行**：v2.0 不依赖 prompt 行命中，但按钮区域仍基于当前可见行记录；如果按钮提示自身换行，需要按实际显示行记录 segment。
- **DA1 探测的误判风险**：只检查响应存在不解析能力标志，极少数终端可能响应 DA1 但不支持备用屏或 SGR mouse，此时鼠标点击无反应，用户需手动降级。

## 文件清单

### 新建

- `Emuera.Headless/Agent/AgentCliVtInput.cs`
  - VT 输入后端：raw stdin 读取、平台分支 `HasInputAvailable()`、VT 解析状态机、SGR mouse 解析、UTF-8 键盘解析、Ctrl+C 检测、IME 中间态跳过。
  - 按钮区域记录与命中（复用 `ButtonRegionTracker`）。
  - raw input mode 生命周期（Windows `SetConsoleMode`）。
  - SGR mouse tracking 生命周期。
  - DA1 探测。

- `Emuera.Headless/Agent/AgentCliVtScreen.cs`
  - VT 渲染后端：备用屏生命周期、绝对定位渲染、清屏/清行、尺寸查询。
  - 封装所有 VT 渲染序列，`AgentCliProtocol` 调用新 API。

### 改造

- `Emuera.Headless/Agent/AgentCliProtocol.cs`
  - `RunConsoleKeyLoop` 改为 `RunVtLoop`，主循环轮询 `AgentCliVtInput`。
  - `FullRefresh` / `EraseTerminalRows` / `OverwriteCountdownLine` / `RenderButtonPrompt` 改用 `AgentCliVtScreen`。
  - `RefreshButtonRegions` 删除 `Console.WindowTop`，用 viewport 坐标。
  - 降级路径保留 `RunConsoleKeyLoop`（DA1 失败时调用）。
  - pipe 模式不变。

- `Emuera.Headless/Agent/TerminalCursor.cs`
  - 扩展备用屏支持（`EnterAlternateScreen` / `LeaveAlternateScreen`）。
  - 或由 `AgentCliVtScreen` 直接封装，`TerminalCursor` 保持不变。

### 标记废弃

- `Emuera.Headless/Agent/AgentCliMouseInput.cs`
  - 类头加 `[Obsolete("v2.0 使用 AgentCliVtInput，本类不再接入主循环")]`。
  - 文件头加注释块说明 v2.0 后不再接入主循环，保留代码供参考。
  - 代码完全不动，不删除。

### 不动

- `Emuera.Headless/Agent/ButtonRegionTracker.cs`：纯坐标比较，坐标语义从 buffer 变 viewport 不影响逻辑。
- `Emuera.Headless/Agent/TerminalDisplayWidth.cs`：字符宽度判定与游戏设计一致，备用屏不改变 cell 语义。
- `Emuera.Headless/UI/Game/EmueraConsole.AgentBridge.cs`：渲染层桥接不变。
