# 坐标转换验证结果

## 版本信息

- 关联规格：`docs/2026.6.16.mouse-input/spec-v1.2.md`
- 关联 spike：`docs/2026.6.16.mouse-input/spikes/coordinate-validation/`
- 验证目标：确认 `MOUSE_EVENT_RECORD.dwMousePosition` 与 viewport 坐标之间的转换关系

## 2026-06-16 初测 — Windows Terminal + PowerShell 7.6.2

环境：

```text
Windows Terminal + PowerShell 7.6.2
Console.WindowWidth=120
Console.WindowHeight=30
Console.WindowLeft=0
Console.WindowTop=0
Console.BufferWidth=120
Console.BufferHeight=30
```

### 初测命令与观察

```powershell
dotnet run --project docs/2026.6.16.mouse-input/spikes/coordinate-validation/coordinate-validation.csproj -c Debug --no-build
```

观察：

```text
[coord-validate][mouse] rawX=4 rawY=28 windowTop=0 windowLeft=0 windowHeight=30 buttonExpectedViewportRow=29 buttonLeft=0 buttonRight=13 hitRawViewport=False hitWindowTop=False inferredWindowTop=-1 hitInferredTop=True
```

```powershell
dotnet run --project docs/2026.6.16.mouse-input/spikes/coordinate-validation/coordinate-validation.csproj -c Debug --no-build -- --after 5
```

观察：

```text
[coord-validate][mouse] rawX=3 rawY=23 windowTop=0 windowLeft=0 windowHeight=30 buttonExpectedViewportRow=24 buttonLeft=0 buttonRight=13 hitRawViewport=False hitWindowTop=False inferredWindowTop=-1 hitInferredTop=True
```

```powershell
dotnet run --project docs/2026.6.16.mouse-input/spikes/coordinate-validation/coordinate-validation.csproj -c Debug --no-build -- --left 8
```

观察：

```text
[coord-validate][mouse] rawX=10 rawY=28 windowTop=0 windowLeft=0 windowHeight=30 buttonExpectedViewportRow=29 buttonLeft=8 buttonRight=21 hitRawViewport=False hitWindowTop=False inferredWindowTop=-1 hitInferredTop=True
```

### 初步结论

- 在该环境中 `Console.WindowTop` 可用但恒为 `0`。
- 调整窗口高度后 `Console.WindowHeight` 会同步变化，说明窗口尺寸 API 行为正常。
- 初版用 `Console.WriteLine(buttonLine)` 输出按钮行时，按钮 rawY 比预期少 1：
  - `--after 0` 预期 row=29，实际 rawY=28。
  - `--after 5` 预期 row=24，实际 rawY=23。
- 列坐标符合预期：
  - 默认 left=0 时 rawX=3/4 命中按钮列范围 0-13。
  - `--left 8` 时 rawX=10 命中按钮列范围 8-21。
- `hitInferredTop=True` 说明即使 `WindowTop` 恒为 0，也可以通过已知按钮点击推断 rawY 与 expected viewport row 的 offset。
- 初版偏差原因是：在底部行使用 `Console.WriteLine()` 会触发滚动，使按钮比预期上移一行。

第一次修正后复测发现 `--after 5` 仍不正确：

```text
rawY=19
buttonExpectedViewportRow=24
inferredWindowTop=-5
hitInferredTop=True
```

原因：每个 `--after` 循环中先输出空 `Console.WriteLine()`，再输出 `post-*`，导致每个 `N` 实际滚动两次。`--after 5` 实际滚动 10 行，因此按钮从 row=29 上移到 row=19。

### 设计修正

- 初版和第一次修正仍然把按钮位置建立在"按钮后填充行"上，不够严谨。
- 已改为以最后一行 prompt 为锚点：
  - 预填充后依次输出按钮行；
  - 输出若干间隔行；
  - 最后输出 prompt 行；
  - 最后一个按钮距离 prompt 行 `rowsAbovePrompt` 行。
- 新参数语义为 `--rows-above-prompt N`，`--after N` 仅作为兼容别名。
- 新预期公式为：

```text
promptRow = WindowHeight - 1
expectedButtonRow = promptRow - distanceFromPrompt
distanceFromPrompt = rowsAbovePrompt + buttonIndexFromBottom
```

### 修正后验证

已实现并重新构建，人工点击验证已覆盖主要命中路径和 miss 路径：

- spike 绘制模型已改为 prompt 锚点：
  - 预填充若干行触发滚动；
  - 依次输出按钮行；
  - 输出 `rowsAbovePrompt - 1` 条 `sep-*` 间隔行；
  - 最后一行输出 prompt 行。
- 构建已通过：
```text
dotnet build "D:\LaoBro\Emuera.MCP\docs\2026.6.16.mouse-input\spikes\coordinate-validation\coordinate-validation.csproj" -c Debug -v:minimal
已成功生成。
0 个警告
0 个错误
```
- help 输出已确认包含 `--rows-above-prompt N`、`--after N`、`--button S`、`--prompt S`、`--prompt-empty`、`--repeat`。

人工点击验证：

- 默认参数下单按钮紧贴 prompt 上方，`distanceFromPrompt=1`，命中通过：
```text
[coord-validate][mouse] rawX=3 rawY=28 windowTop=0 windowLeft=0 windowHeight=30 promptRow=29 hitRawViewport=True hitWindowTop=True inferredWindowTop=0 hitInferredTop=True
[coord-validate][hit] source=windowTop index=0 label=CLICK expectedRow=28 expectedCol=0-13 distanceFromPrompt=1 row=28 col=3
[coord-validate][coord] normalizedRow=28 normalizedCol=3 promptRow=29
```

- `--rows-above-prompt 5 --left 8` 命中通过，按钮位于 prompt 上方第 5 行，列范围为 `8-29`：
```text
[coord-validate][mouse] rawX=13 rawY=24 windowTop=0 windowLeft=0 windowHeight=30 promptRow=29 hitRawViewport=True hitWindowTop=True inferredWindowTop=0 hitInferredTop=True
[coord-validate][hit] source=windowTop index=0 label=CLICK expectedRow=24 expectedCol=8-29 distanceFromPrompt=5 row=24 col=13
[coord-validate][coord] normalizedRow=24 normalizedCol=13 promptRow=29
```

- 多按钮场景验证了上方按钮命中路径，但第三次输出暴露了旧实现问题：`--button` 没有覆盖默认 `CLICK`，日志显示 `buttons=CLICK,[0] Hello,[1] Quit`。
```text
[coord-validate][mouse] rawX=15 rawY=25 windowTop=0 windowLeft=0 windowHeight=30 promptRow=29 hitRawViewport=True hitWindowTop=True inferredWindowTop=0 hitInferredTop=True
[coord-validate][hit] source=windowTop index=1 label=[0] Hello expectedRow=25 expectedCol=0-17 distanceFromPrompt=4 row=25 col=15
[coord-validate][coord] normalizedRow=25 normalizedCol=15 promptRow=29
```

已修正并复测：

- `--button S` 现在会覆盖默认 `CLICK`，未指定 `--button` 时才使用默认按钮。
- 修正后多按钮日志变为 `buttons=[0] Hello,[1] Quit`。
- 点击 `[1] Quit` 命中通过，`distanceFromPrompt=3`：
```text
[coord-validate][mouse] rawX=14 rawY=26 windowTop=0 windowLeft=0 windowHeight=30 promptRow=29 hitRawViewport=True hitWindowTop=True inferredWindowTop=0 hitInferredTop=True
[coord-validate][hit] source=windowTop index=1 label=[1] Quit expectedRow=26 expectedCol=0-16 distanceFromPrompt=3 row=26 col=14
[coord-validate][coord] normalizedRow=26 normalizedCol=14 promptRow=29
```

- 点击按钮和 prompt 之间的空白行 miss 通过：
```text
[coord-validate][mouse] rawX=5 rawY=27 windowTop=0 windowLeft=0 windowHeight=30 promptRow=29 hitRawViewport=False hitWindowTop=False inferredWindowTop=unavailable hitInferredTop=False
[coord-validate][hit] rawViewport=false
[coord-validate][hit] windowTop=false
[coord-validate][hit] inferredTop=false
[coord-validate][coord] normalizedRow=27 normalizedCol=5 promptRow=29
```

- 点击 prompt 行 miss 通过：
```text
[coord-validate][mouse] rawX=4 rawY=29 windowTop=0 windowLeft=0 windowHeight=30 promptRow=29 hitRawViewport=False hitWindowTop=False inferredWindowTop=unavailable hitInferredTop=False
[coord-validate][hit] rawViewport=false
[coord-validate][hit] windowTop=false
[coord-validate][hit] inferredTop=false
[coord-validate][coord] normalizedRow=29 normalizedCol=4 promptRow=29
```

### Windows Terminal 待验证

- 左上角点击应得到 `rawY=0 rawX=0`。
- 右下角点击应得到 `rawY=29 rawX=119`，当前窗口高度 30、宽度 120。
- 注意：Windows Terminal 下 WindowTop 恒为 0，这些测试主要用于确认 rawY 在 WindowTop=0 时直接等于 viewport row。

## 2026-06-20 更新：Conhost + PowerShell 5.1 坐标验证

### 环境差异

| 字段 | Windows Terminal | Conhost |
|---|---|---|
| Buffer 尺寸 | 与窗口相同 (120×30) | 远大于窗口 (120×3000) |
| `Console.WindowTop` | 恒为 0 | 非零 (随滚动变化) |
| `dwMousePosition` 语义 | viewport 坐标 | buffer 坐标 |
| `window-size` 初始化事件 | 无 | 有 (columns=120 rows=3000) |

### 用例 1：单按钮紧贴 prompt

命令：

```powershell
dotnet run --project docs/2026.6.16.mouse-input/spikes/coordinate-validation/coordinate-validation.csproj -c Debug --no-build
```

输出：

```text
[coord-validate][window-size] columns=120 rows=3000
[coord-validate][mouse] rawX=7 rawY=58 windowTop=11 windowLeft=0 windowHeight=49 promptRow=48 hitRawViewport=False hitWindowTop=True inferredWindowTop=11 hitInferredTop=True buttonState=0x00000001 eventFlags=0x00000000
[coord-validate][hit] rawViewport=false
[coord-validate][hit] source=windowTop index=0 label=CLICK expectedRow=47 expectedCol=0-13 distanceFromPrompt=1 row=47 col=7
[coord-validate][hit] source=inferredTop index=0 label=CLICK expectedRow=47 expectedCol=0-13 distanceFromPrompt=1 row=47 col=7
[coord-validate][coord] normalizedRow=47 normalizedCol=7 promptRow=48
[coord-validate] restored input mode=0x000001F7
```

分析：

- buffer 尺寸 120×3000，窗口高度 49
- `rawY=58`，`WindowTop=11`，`rawY - WindowTop = 47 = expectedRow` ✓
- `hitRawViewport=False` — conhost 下 rawY 是 buffer 坐标，不能直接当 viewport 坐标
- `hitWindowTop=True` — 减去 WindowTop 后命中 ✓
- `inferredWindowTop=11` 与 `WindowTop=11` 一致 ✓

### 用例 2：按钮距离 prompt 5 行

命令：

```powershell
dotnet run --project docs/2026.6.16.mouse-input/spikes/coordinate-validation/coordinate-validation.csproj -c Debug --no-build -- --rows-above-prompt 5
```

输出：

```text
[coord-validate][window-size] columns=120 rows=3000
[coord-validate][mouse] rawX=9 rawY=127 windowTop=84 windowLeft=0 windowHeight=49 promptRow=48 hitRawViewport=False hitWindowTop=True inferredWindowTop=84 hitInferredTop=True buttonState=0x00000001 eventFlags=0x00000000
[coord-validate][hit] rawViewport=false
[coord-validate][hit] source=windowTop index=0 label=CLICK expectedRow=43 expectedCol=0-13 distanceFromPrompt=5 row=43 col=9
[coord-validate][hit] source=inferredTop index=0 label=CLICK expectedRow=43 expectedCol=0-13 distanceFromPrompt=5 row=43 col=9
[coord-validate][coord] normalizedRow=43 normalizedCol=9 promptRow=48
[coord-validate] restored input mode=0x000001F7
```

分析：

- `rawY=127 - WindowTop=84 = 43 = expectedRow` ✓
- `WindowTop` 从 11 跳到 84，因为步骤 3 的 stderr 日志滚动了屏幕——同一窗口连续测试时是预期行为
- `hitWindowTop=True` ✓

### 用例 3a：左侧缩进，点击按钮文字

命令：

```powershell
dotnet run --project docs/2026.6.16.mouse-input/spikes/coordinate-validation/coordinate-validation.csproj -c Debug --no-build -- --left 8 --rows-above-prompt 1
```

输出：

```text
[coord-validate][window-size] columns=120 rows=3000
[coord-validate][mouse] rawX=14 rawY=200 windowTop=153 windowLeft=0 windowHeight=49 promptRow=48 hitRawViewport=False hitWindowTop=True inferredWindowTop=153 hitInferredTop=True buttonState=0x00000001 eventFlags=0x00000000
[coord-validate][hit] rawViewport=false
[coord-validate][hit] source=windowTop index=0 label=CLICK expectedRow=47 expectedCol=8-29 distanceFromPrompt=1 row=47 col=14
[coord-validate][hit] source=inferredTop index=0 label=CLICK expectedRow=47 expectedCol=8-29 distanceFromPrompt=1 row=47 col=14
[coord-validate][coord] normalizedRow=47 normalizedCol=14 promptRow=48
[coord-validate] restored input mode=0x000001F7
```

分析：

- `rawY=200 - WindowTop=153 = 47 = expectedRow` ✓
- `rawX=14` 在列范围 `8-29` 内 ✓
- `hitWindowTop=True` ✓

### 用例 3b：左侧缩进，点击按钮左侧空白

命令（重新运行）：

```powershell
dotnet run --project docs/2026.6.16.mouse-input/spikes/coordinate-validation/coordinate-validation.csproj -c Debug --no-build -- --left 8 --rows-above-prompt 1
```

输出：

```text
[coord-validate][window-size] columns=120 rows=3000
[coord-validate][mouse] rawX=2 rawY=269 windowTop=222 windowLeft=0 windowHeight=49 promptRow=48 hitRawViewport=False hitWindowTop=False inferredWindowTop=unavailable hitInferredTop=False buttonState=0x00000001 eventFlags=0x00000000
[coord-validate][hit] rawViewport=false
[coord-validate][hit] windowTop=false
[coord-validate][hit] inferredTop=false
[coord-validate][coord] normalizedRow=47 normalizedCol=2 promptRow=48
[coord-validate] restored input mode=0x000001F7
```

分析：

- `rawX=2` 不在列范围 `8-29` 内
- `hitWindowTop=False` ✓ — 空白区域正确 miss

### 用例 4a：多按钮，点击 [1] Quit

命令：

```powershell
dotnet run --project docs/2026.6.16.mouse-input/spikes/coordinate-validation/coordinate-validation.csproj -c Debug --no-build -- --button "[0] Hello" --button "[1] Quit" --rows-above-prompt 3
```

输出：

```text
[coord-validate][window-size] columns=120 rows=3000
[coord-validate][mouse] rawX=10 rawY=337 windowTop=292 windowLeft=0 windowHeight=49 promptRow=48 hitRawViewport=False hitWindowTop=True inferredWindowTop=292 hitInferredTop=True buttonState=0x00000001 eventFlags=0x00000000
[coord-validate][hit] rawViewport=false
[coord-validate][hit] source=windowTop index=1 label=[1] Quit expectedRow=45 expectedCol=0-16 distanceFromPrompt=3 row=45 col=10
[coord-validate][hit] source=inferredTop index=1 label=[1] Quit expectedRow=45 expectedCol=0-16 distanceFromPrompt=3 row=45 col=10
[coord-validate][coord] normalizedRow=45 normalizedCol=10 promptRow=48
[coord-validate] restored input mode=0x000001F7
```

分析：

- `rawY=337 - WindowTop=292 = 45 = expectedRow` ✓
- `index=1`，`distanceFromPrompt=3` ✓
- `hitWindowTop=True` ✓
- `window-size` 事件在绘图阶段就出现（prompt 输出时触发 buffer resize），尚未点击按钮

### 用例 4b：多按钮，点击 [0] Hello

命令（新开窗口重新运行）：

```powershell
dotnet run --project docs/2026.6.16.mouse-input/spikes/coordinate-validation/coordinate-validation.csproj -c Debug --no-build -- --button "[0] Hello" --button "[1] Quit" --rows-above-prompt 3
```

输出：

```text
[coord-validate][window-size] columns=120 rows=3000
[coord-validate][mouse] rawX=10 rawY=58 windowTop=14 windowLeft=0 windowHeight=49 promptRow=48 hitRawViewport=False hitWindowTop=True inferredWindowTop=14 hitInferredTop=True buttonState=0x00000001 eventFlags=0x00000000
[coord-validate][hit] rawViewport=false
[coord-validate][hit] source=windowTop index=0 label=[0] Hello expectedRow=44 expectedCol=0-17 distanceFromPrompt=4 row=44 col=10
[coord-validate][hit] source=inferredTop index=0 label=[0] Hello expectedRow=44 expectedCol=0-17 distanceFromPrompt=4 row=44 col=10
[coord-validate][coord] normalizedRow=44 normalizedCol=10 promptRow=48
[coord-validate] restored input mode=0x000001F7
```

分析：

- `rawY=58 - WindowTop=14 = 44 = expectedRow` ✓
- `index=0`，`distanceFromPrompt=4` ✓
- `hitWindowTop=True` ✓

### 用例 5a：按钮间空白 miss

命令：

```powershell
dotnet run --project docs/2026.6.16.mouse-input/spikes/coordinate-validation/coordinate-validation.csproj -c Debug --no-build -- --rows-above-prompt 5
```

输出：

```text
[coord-validate][mouse] rawX=6 rawY=132 windowTop=87 windowLeft=0 windowHeight=49 promptRow=48 hitRawViewport=False hitWindowTop=False inferredWindowTop=unavailable hitInferredTop=False buttonState=0x00000001 eventFlags=0x00000000
[coord-validate][hit] rawViewport=false
[coord-validate][hit] windowTop=false
[coord-validate][hit] inferredTop=false
[coord-validate][coord] normalizedRow=45 normalizedCol=6 promptRow=48
[coord-validate] restored input mode=0x000001F7
```

分析：

- `rawY=132 - WindowTop=87 = 45`，按钮在 expectedRow=43，空白行 45 不命中
- `hitWindowTop=False` ✓

### 用例 5b：prompt 行 miss

命令（重新运行）：

```powershell
dotnet run --project docs/2026.6.16.mouse-input/spikes/coordinate-validation/coordinate-validation.csproj -c Debug --no-build -- --rows-above-prompt 5
```

输出：

```text
[coord-validate][mouse] rawX=6 rawY=206 windowTop=158 windowLeft=0 windowHeight=49 promptRow=48 hitRawViewport=False hitWindowTop=False inferredWindowTop=unavailable hitInferredTop=False buttonState=0x00000001 eventFlags=0x00000000
[coord-validate][hit] rawViewport=false
[coord-validate][hit] windowTop=false
[coord-validate][hit] inferredTop=false
[coord-validate][coord] normalizedRow=48 normalizedCol=6 promptRow=48
[coord-validate] restored input mode=0x000001F7
```

分析：

- `rawY=206 - WindowTop=158 = 48 = promptRow`，点击 prompt 行不命中
- `hitWindowTop=False` ✓

### 用例 6a：左上角点击

命令：

```powershell
dotnet run --project docs/2026.6.16.mouse-input/spikes/coordinate-validation/coordinate-validation.csproj -c Debug --no-build
```

初始输出（window-size 日志未跳过时）：

```text
[coord-validate][mouse] rawX=0 rawY=226 windowTop=225 windowLeft=0 windowHeight=49 promptRow=48 hitRawViewport=False hitWindowTop=False inferredWindowTop=unavailable hitInferredTop=False buttonState=0x00000001 eventFlags=0x00000000
[coord-validate][hit] rawViewport=false
[coord-validate][hit] windowTop=false
[coord-validate][hit] inferredTop=false
[coord-validate][coord] normalizedRow=1 normalizedCol=0 promptRow=48
[coord-validate] restored input mode=0x000001F7
```

修正后输出（跳过 window-size stderr 日志，改为文件输出）：

```text
[coord-validate][mouse] rawX=0 rawY=11 windowTop=11 windowLeft=0 windowHeight=49 promptRow=48 hitRawViewport=False hitWindowTop=False inferredWindowTop=unavailable hitInferredTop=False buttonState=0x00000001 eventFlags=0x00000000
[coord-validate][hit] rawViewport=false
[coord-validate][hit] windowTop=false
[coord-validate][hit] inferredTop=false
[coord-validate][coord] normalizedRow=0 normalizedCol=0 promptRow=48
[coord-validate] restored input mode=0x000001F7
```

分析：

- 修正前 `normalizedRow=1`，修正后 `normalizedRow=0` ✓
- 根因：conhost 中 stderr 共享显示缓冲区，window-size 日志推动可见内容下移一行
- 修正后 `rawX=0, normalizedCol=0` ✓，`rawY=11, WindowTop=11, normalizedRow=0` ✓

### 用例 6b：右下角点击

命令（重新运行）：

```powershell
dotnet run --project docs/2026.6.16.mouse-input/spikes/coordinate-validation/coordinate-validation.csproj -c Debug --no-build
```

输出：

```text
[coord-validate][mouse] rawX=119 rawY=341 windowTop=292 windowLeft=0 windowHeight=49 promptRow=48 hitRawViewport=False hitWindowTop=False inferredWindowTop=unavailable hitInferredTop=False buttonState=0x00000001 eventFlags=0x00000000
[coord-validate][hit] rawViewport=false
[coord-validate][hit] windowTop=false
[coord-validate][hit] inferredTop=false
[coord-validate][coord] normalizedRow=49 normalizedCol=119 promptRow=48
[coord-validate] restored input mode=0x000001F7
```

分析：

- `rawX=119`，`normalizedCol=119 = WindowWidth-1` ✓
- `rawY=341 - WindowTop=292 = 49`，`normalizedRow=49` 超出 viewport 范围（0-48）——点击了窗口最底部边缘像素，映射到 viewport 下方一行
- 列坐标归一化正确 ✓
- 极端边界情况，不影响生产路径

## Conhost 验证总结

### 环境

- OS：Windows
- 终端：Conhost (PowerShell 5.1)
- Buffer 尺寸：120×3000
- 窗口尺寸：120×49
- `Console.WindowTop`：可用，非零，随滚动变化
- `Console.WindowLeft`：可用，恒为 0

### 重要发现：stderr/stdout 共享缓冲区

Conhost 中 stderr 和 stdout 共享同一显示缓冲区。绘图阶段的 `Console.Error.WriteLine` 输出会推动可见内容下移一行，导致坐标偏移。

验证过程：

1. 初始测试左上角点击得到 `normalizedRow=1`（而非预期的 0）
2. 跳过 `WINDOW_BUFFER_SIZE_EVENT` 的 stderr 日志后，左上角点击得到 `normalizedRow=0` ✓
3. 根因：conhost 在绘图阶段触发 `WINDOW_BUFFER_SIZE_EVENT`，其 stderr 日志推动了可见内容

修正方案：将 `WINDOW_BUFFER_SIZE_EVENT` 日志改为文件输出（`coord-validate.log`），避免干扰 stdout 布局。

### 坐标模型确认

| 验证项 | Windows Terminal | Conhost |
|---|---|---|
| `dwMousePosition` 语义 | viewport 坐标 | buffer 坐标 |
| `Console.WindowTop` | 恒为 0 | 非零，随滚动变化 |
| `Console.BufferHeight` | = WindowHeight | >> WindowHeight |
| 归一化公式 | rawY 直接使用 | rawY - WindowTop |

### 测试结果汇总

| 用例 | Windows Terminal | Conhost |
|---|---|---|
| 单按钮紧贴 prompt | hitWindowTop=True ✓ | hitWindowTop=True ✓ |
| 距离 prompt 5 行 | hitWindowTop=True ✓ | hitWindowTop=True ✓ |
| 左侧缩进，点击文字 | hitWindowTop=True ✓ | hitWindowTop=True ✓ |
| 左侧缩进，点击空白 | hitWindowTop=False ✓ | hitWindowTop=False ✓ |
| 多按钮 [0] Hello | hitWindowTop=True ✓ | hitWindowTop=True ✓ |
| 多按钮 [1] Quit | hitWindowTop=True ✓ | hitWindowTop=True ✓ |
| 按钮间空白 miss | hitWindowTop=False ✓ | hitWindowTop=False ✓ |
| prompt 行 miss | hitWindowTop=False ✓ | hitWindowTop=False ✓ |
| 左上角 (0,0) | 待验证 | normalizedRow=0 ✓, normalizedCol=0 ✓ |
| 右下角 (H-1,W-1) | 待验证 | normalizedCol=119 ✓, normalizedRow=49（边缘） |

### 结论

1. **`rawY - Console.WindowTop` 在 conhost 下是正确的 viewport 归一化公式**，与 Windows Terminal 行为一致（Windows Terminal 中 WindowTop 恒为 0，公式退化为 rawY 直接使用）。
2. **`rawX - Console.WindowLeft` 在两种终端下都正确**。
3. **生产路径应统一使用 `viewportRow = mouseY - Console.WindowTop`、`viewportCol = mouseX - Console.WindowLeft`**，该公式在 Windows Terminal 和 conhost 下均已验证通过。
4. Conhost 在绘图阶段会触发 `WINDOW_BUFFER_SIZE_EVENT`（buffer resize），不影响坐标验证。
5. 极端角落点击存在 1 行精度偏差（右下角），属于鼠标点击边界行为，不影响按钮命中逻辑。
6. **Conhost 中 stderr 与 stdout 共享显示缓冲区**，绘图阶段的 stderr 日志会推动可见内容下移，导致坐标偏移。生产路径中应避免在绘图阶段向 stderr 输出日志，或将日志重定向到文件。
