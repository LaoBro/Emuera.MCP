# 坐标转换可行性验证计划

## 版本信息

- 关联规格：`docs/2026.6.16.mouse-input/spec-v1.2.md`
- 关联 spike：`docs/2026.6.16.mouse-input/spikes/coordinate-validation/`
- 目标平台：Windows Console
- 输入 backend：Win32 `ReadConsoleInput()`
- 验证重点：`MOUSE_EVENT_RECORD.dwMousePosition` 与 visible viewport 坐标之间的转换关系

## 一句话总结

通过自然输出滚动产生一个以 prompt 行为锚点的按钮区域，不使用 `Console.WindowTop` 定位，也不使用 `Console.SetCursorPosition()` 定位；点击后同时记录 raw Win32 坐标、`Console.WindowTop`、归一化 viewport 坐标和命中结果，用于确认 v1.2 坐标模型。

## 当前选择

- 验证范围：自然滚动，不主动强制 `Console.WindowTop > 0`。
- 内部坐标模型：生产内部统一使用 visible viewport 坐标；验证日志同时打印 raw buffer 坐标和 normalized viewport 坐标。
- 调试输出：验证期间写入 `Console.Error`。

## 为什么不用 `SetCursorPosition`

`Console.WindowTop` 在当前目标环境中是否能稳定工作仍待验证。如果第一版验证项目直接依赖：

```csharp
bufferRow = Console.WindowTop + viewportRow;
Console.SetCursorPosition(0, bufferRow);
```

则验证本身会被 `WindowTop` 的可靠性影响。

因此本验证采用更保守的相对滚动方案，并把最后一行显式作为 prompt 行：

```text
输出多行预填充
→ 输出按钮行
→ 输出若干间隔行
→ 输出 prompt 行
→ 按钮可见位置 = promptRow - rowsAbovePrompt
```

该方案不依赖 `Console.WindowTop` 定位，也不依赖 `Console.SetCursorPosition()` 定位。

## 项目位置

```text
docs/2026.6.16.mouse-input/spikes/coordinate-validation/
```

主要文件：

```text
coordinate-validation.csproj
Program.cs
```

## 构建

```powershell
dotnet build docs/2026.6.16.mouse-input/spikes/coordinate-validation/coordinate-validation.csproj -c Debug
```

## 运行

必须直接在真实终端中运行，不要重定向 stdin：

```powershell
dotnet run --project docs/2026.6.16.mouse-input/spikes/coordinate-validation/coordinate-validation.csproj -c Debug --no-build
```

可选参数：

```powershell
# 单按钮，紧贴 prompt 上方，默认行为
dotnet run --project docs/2026.6.16.mouse-input/spikes/coordinate-validation/coordinate-validation.csproj -c Debug --no-build

# 单按钮，距离 prompt 5 行
dotnet run --project docs/2026.6.16.mouse-input/spikes/coordinate-validation/coordinate-validation.csproj -c Debug --no-build -- --rows-above-prompt 5

# --after 是兼容别名，语义同 --rows-above-prompt
dotnet run --project docs/2026.6.16.mouse-input/spikes/coordinate-validation/coordinate-validation.csproj -c Debug --no-build -- --after 5

# 按钮左侧缩进 8 列
dotnet run --project docs/2026.6.16.mouse-input/spikes/coordinate-validation/coordinate-validation.csproj -c Debug --no-build -- --left 8

# 两个按钮，最后一个按钮距离 prompt 3 行
dotnet run --project docs/2026.6.16.mouse-input/spikes/coordinate-validation/coordinate-validation.csproj -c Debug --no-build -- --button "[0] Hello" --button "[1] Quit" --rows-above-prompt 3

# 最后一行显示空格，模拟空 prompt 行
dotnet run --project docs/2026.6.16.mouse-input/spikes/coordinate-validation/coordinate-validation.csproj -c Debug --no-build -- --prompt-empty

# 持续等待多次点击，直到按 Esc
dotnet run --project docs/2026.6.16.mouse-input/spikes/coordinate-validation/coordinate-validation.csproj -c Debug --no-build -- --repeat
```

## 程序行为

### 1. 初始化 Win32 console input

程序启动后：

1. 检查 `OperatingSystem.IsWindows()`。
2. 检查 stdin 是否 redirected。
3. 获取 `STD_INPUT_HANDLE`。
4. 临时启用：
   - `ENABLE_MOUSE_INPUT`
   - `ENABLE_WINDOW_INPUT`
   - `ENABLE_EXTENDED_FLAGS`
5. 临时禁用：
   - `ENABLE_QUICK_EDIT_MODE`
6. 向 `Console.Error` 打印启动时 console 状态。
7. 恢复 input mode 后退出。

### 2. 通过自然滚动生成按钮和 prompt

程序不会调用 `Console.Clear()`，也不会调用 `Console.SetCursorPosition()` 来定位按钮。

程序会：

1. 输出 `WindowHeight + 2` 行预填充文本，把光标推到底部附近。
2. 使用 `Console.WriteLine()` 输出按钮行，例如：

   ```text
   [BUTTON] CLICK
   ```

3. 如果有多个按钮，从上到下依次输出多个按钮行。
4. 输出若干间隔行，使最后一个按钮距离 prompt 行 `rowsAbovePrompt` 行。
5. 使用 `Console.Write()` 输出 prompt 行，例如：

   ```text
   [PROMPT]
   ```

最后一行固定作为 prompt 行。按钮的预期 viewport row 为：

```text
promptRow = WindowHeight - 1
expectedViewportRow = promptRow - distanceFromPrompt
distanceFromPrompt = rowsAbovePrompt + buttonIndexFromBottom
```

单按钮时：

```text
distanceFromPrompt = rowsAbovePrompt
expectedViewportRow = WindowHeight - 1 - rowsAbovePrompt
```

按钮的预期 viewport col 范围为：

```text
left .. right
```

默认 `left = 0`，默认 `rowsAbovePrompt = 1`。

### 3. 等待鼠标点击

程序只接受：

```text
EventType == MOUSE_EVENT
dwButtonState 包含 FROM_LEFT_1ST_BUTTON_PRESSED
dwEventFlags == 0
且不是左键重复按下
```

忽略：

- 鼠标移动；
- 鼠标释放；
- 滚轮；
- 右键；
- double-click 噪声。

按 `Esc` 退出。

### 4. 点击后输出调试信息

点击按钮后，程序向 `Console.Error` 输出类似信息：

```text
[coord-validate][mouse] rawX=12 rawY=35 windowTop=20 windowLeft=0 windowHeight=30 promptRow=29 hitRawViewport=true hitWindowTop=true inferredWindowTop=20 hitInferredTop=true buttonState=0x00000001 eventFlags=0x00000000
[coord-validate][hit] source=windowTop index=0 label=CLICK expectedRow=24 expectedCol=0-14 distanceFromPrompt=5 row=10 col=12
[coord-validate][coord] normalizedRow=10 normalizedCol=12 promptRow=29
```

字段含义：

| 字段 | 含义 |
|---|---|
| `rawX` / `rawY` | `MOUSE_EVENT_RECORD.dwMousePosition.X/Y` |
| `windowTop` / `windowLeft` | `Console.WindowTop` / `Console.WindowLeft`，不可用时显示 `unavailable` |
| `promptRow` | 最后一行 prompt 的 viewport row，通常为 `WindowHeight - 1` |
| `hitRawViewport` | 假设 raw 坐标已经是 viewport 坐标时是否命中 |
| `hitWindowTop` | 使用 `raw - WindowTop/Left` 归一化后是否命中 |
| `inferredWindowTop` | 通过已知按钮点击推断出的 `rawY - expectedViewportRow` |
| `[hit] index/label/distanceFromPrompt` | 命中的按钮索引、标签和距离 prompt 的行数 |
| `hitInferredTop` | 使用推断 offset 归一化后是否命中 |

## 验证目标

本验证必须回答：

1. `dwMousePosition` 在当前 Windows 终端中更接近 viewport 坐标还是 screen buffer 坐标。
2. `Console.WindowTop` 是否可用，且是否能用于归一化。
3. `rawY - Console.WindowTop == expectedViewportRow` 是否能稳定命中按钮。
4. `rawX - Console.WindowLeft == expectedViewportCol` 是否能稳定命中按钮。
5. 自然滚动后，prompt 行是否稳定位于 `WindowHeight - 1`。
6. 按钮位置是否能通过 `promptRow - distanceFromPrompt` 稳定推断。
7. 点击 prompt 行是否不会命中任何按钮。

## 建议手动测试用例

### 用例 1：按钮紧贴 prompt 上方

命令：

```powershell
dotnet run --project docs/2026.6.16.mouse-input/spikes/coordinate-validation/coordinate-validation.csproj -c Debug --no-build
```

点击 `[BUTTON] CLICK`。

期望：

```text
promptRow=WindowHeight - 1
distanceFromPrompt=1
hitWindowTop=true
```

或如果 `WindowTop` 不可用：

```text
inferredWindowTop=<某个稳定值>
hitInferredTop=true
```

### 用例 2：按钮距离 prompt 5 行

命令：

```powershell
dotnet run --project docs/2026.6.16.mouse-input/spikes/coordinate-validation/coordinate-validation.csproj -c Debug --no-build -- --rows-above-prompt 5
```

点击 `[BUTTON] CLICK`。

期望：

```text
promptRow=WindowHeight - 1
distanceFromPrompt=5
expectedRow=WindowHeight - 6
hitWindowTop=true
```

### 用例 3：按钮左侧缩进

命令：

```powershell
dotnet run --project docs/2026.6.16.mouse-input/spikes/coordinate-validation/coordinate-validation.csproj -c Debug --no-build -- --left 8 --rows-above-prompt 1
```

点击按钮文本区域。

期望：

```text
expectedCol=8..right
hitWindowTop=true
```

点击按钮左侧空白 2 列。

期望：

```text
hitWindowTop=false
```

### 用例 4：按钮下方空白点击

命令：

```powershell
dotnet run --project docs/2026.6.16.mouse-input/spikes/coordinate-validation/coordinate-validation.csproj -c Debug --no-build -- --rows-above-prompt 5
```

点击按钮同一列、但位于按钮和 prompt 之间的空白行。

期望：

```text
hitWindowTop=false
```

### 用例 5：prompt 行点击

命令：

```powershell
dotnet run --project docs/2026.6.16.mouse-input/spikes/coordinate-validation/coordinate-validation.csproj -c Debug --no-build -- --prompt-empty
```

点击最后一行 prompt 区域。

期望：

```text
promptRow=WindowHeight - 1
hitWindowTop=false
hitInferredTop=false
```

### 用例 6：多个按钮

命令：

```powershell
dotnet run --project docs/2026.6.16.mouse-input/spikes/coordinate-validation/coordinate-validation.csproj -c Debug --no-build -- --button "[0] Hello" --button "[1] Quit" --rows-above-prompt 3
```

点击 `[1] Quit`。

期望：

```text
index=1
distanceFromPrompt=3
hitWindowTop=true
```

点击 `[0] Hello`。

期望：

```text
index=0
distanceFromPrompt=4
hitWindowTop=true
```

### 用例 7：左上角 / 右下角点击

先用默认参数生成按钮和 prompt，然后分别点击：

- 终端左上角；
- 终端右下角。

期望：

```text
normalizedRow=0 normalizedCol=0
```

以及：

```text
normalizedRow=WindowHeight - 1
normalizedCol=WindowWidth - 1
```

如果 `Console.WindowTop` 不可用，则记录 raw 坐标和 `Console.WindowHeight/Width`，并在验证结果中说明。

## 通过标准

满足以下条件即可认为坐标转换验证通过：

- 不依赖 `Console.SetCursorPosition()` 也能稳定生成目标位置的按钮。
- prompt 行稳定位于最后一行。
- `--rows-above-prompt N` 时，最后一个按钮距离 prompt 行 N 行。
- 多按钮时，按钮从上到下依次位于 prompt 上方递增距离处。
- 点击按钮文字区域时，使用 `raw - WindowTop/Left` 归一化后能稳定命中。
- 点击按钮同一行空白处不会命中。
- 点击按钮和 prompt 之间的空白行不会命中。
- 点击 prompt 行不会命中。
- 左上角点击归一化为 `row=0 col=0`。
- 右下角点击归一化为 `row=WindowHeight - 1 col=WindowWidth - 1`。
- 程序退出后 Win32 console input mode 被恢复。

## 失败或限制记录

如果出现以下情况，需要记录为限制，而不是直接判定 v1.2 失败：

- 当前终端下 `Console.WindowTop` 抛异常或始终返回 `0`。
- 当前终端下 `rawY - WindowTop` 与按钮位置不一致。
- 当前终端下 raw 坐标看起来已经是 viewport 坐标。
- `Console.Error` 日志在第一次点击后继续滚动屏幕，导致 `--repeat` 后续点击位置变化。
- 终端 resize 后按钮预期位置失效。

## 与生产实现的关联

如果本验证确认：

```text
rawY - Console.WindowTop == viewportRow
rawX - Console.WindowLeft == viewportCol
```

则 `Emuera.Headless/Agent/AgentCliProtocol.cs` 中鼠标事件进入按钮命中逻辑前应先归一化：

```csharp
viewportRow = mouseY - Console.WindowTop;
viewportCol = mouseX - Console.WindowLeft;
```

然后再与 viewport 坐标记录的按钮区域比较。

如果本验证确认 raw 坐标已经是 viewport 坐标，则生产路径可以保持当前直接使用 raw 坐标的方式，但需要在 spec 中明确该 terminal/backend 的前提。

## 记录结果

验证结果应写入：

```text
docs/2026.6.16.mouse-input/validation-results.md
```

建议记录：

- 日期；
- OS / terminal / shell；
- 使用的 `--rows-above-prompt` / `--after` / `--left` / `--button` / `--prompt` 参数；
- `Console.WindowTop` 是否可用；
- 点击按钮时的 raw 坐标、归一化坐标和 hit 结果；
- 左上角 / 右下角点击的归一化结果；
- 最终结论。
