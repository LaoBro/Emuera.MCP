# CLI 鼠标点击按钮 — v1.3 修订规格

## 版本信息

- 版本：v1.3
- 基线：`docs/2026.6.16.mouse-input/spec-v1.2.md`
- 关联验证：`docs/2026.6.16.mouse-input/validation-results.md`、`coordinate-validation-plan.md`
- 目标文件：`Emuera.Headless/Agent/AgentCliProtocol.cs`
- 目标平台：Windows Console
- 输入 backend：Win32 `ReadConsoleInput()`

### 相对 v1.2 的变更

- 新增「Console input mode 设置」一节，明确启用 / 恢复的标志位，包括禁用 Quick Edit。
- 行号计算明确要求在 `RenderButtonPrompt()` 渲染时捕获 prompt 行，不能用 `cursorTop - 1` 反推。
- 坐标模型措辞从「假设」升级为「已验证」。
- 启用条件明确：交互 Windows 模式下鼠标始终开启，不使用 env-var 门控。
- 新增「调试输出」一节：默认关闭，开启时写入文件，不写 `Console.Error` / `stdout`。
- 新增「已知限制 / 待验证」一节。

## 一句话总结

让 Windows CLI 用户直接用鼠标点击当前按钮提示中的按钮文字；键盘 `↑/↓ + Enter` 仍然作为 fallback。

## 范围

v1.3 只做 Windows 原生控制台鼠标输入：

- 使用 Win32 `ReadConsoleInput()` 读取鼠标事件；
- 只处理左键按下；
- 只处理当前按钮提示中可见的按钮；
- 不实现 SGR mouse parser；
- 不实现 raw stdin mouse parser；
- 不实现非 Windows 鼠标路径。

SGR / raw stdin 的验证经验只作为背景，不进入 v1.3 实现范围。

## 基本假设

v1.3 基于以下前提设计：

1. 等待输入时，prompt 一定是当前终端最后一行。
2. v1.3 MVP 假设 prompt 不会换行。
3. 用户不会手动操作滚动条；鼠标只用于点击当前可见按钮。
4. 游戏已有按钮过期逻辑，当前按钮提示只展示可点击按钮。
5. 按钮区域只记录当前按钮提示，不维护复杂 scrollback 坐标。
6. pipe / redirected stdin 模式不启用鼠标。

如果未来需要支持 prompt 换行，可以继续基于最终光标位置确认最后一个 prompt 行；但这不属于 v1.3 MVP。

## 坐标系统

所有鼠标坐标和按钮区域坐标统一使用：

```text
0-based visible terminal viewport coordinate
```

含义：

- 原点是当前终端可见区域左上角；
- row 向下增加；
- col 向右增加；
- 不包含 scrollback；
- 不包含不可见历史行。

### Win32 鼠标坐标

v1.3 使用 `MOUSE_EVENT_RECORD.dwMousePosition`。

Step 5 坐标转换 spike 已确认：在目标环境（Windows Terminal + PowerShell）下，该坐标即 visible viewport 坐标，`Console.WindowTop` 恒为 `0`，`hitRawViewport` / `hitWindowTop` / `hitInferredTop` 一致命中（见 `validation-results.md` Step 5）。

实现仍保留防御性归一化：

```csharp
row = mouseY - Console.WindowTop;
col = mouseX - Console.WindowLeft;
```

在 `WindowTop=0` 时该归一化是 no-op；在其他终端环境下若 `WindowTop` 非零也安全。

如果坐标不在当前窗口范围内，则忽略：

```text
row < 0
row >= Console.WindowHeight
col < 0
col >= Console.WindowWidth
```

## 按钮区域记录

按钮区域使用可见终端坐标记录。

```csharp
private sealed class ButtonTerminalRegion
{
    public required int Row;       // 0-based visible terminal row
    public required int Left;      // 0-based visible terminal column, inclusive
    public required int Right;     // 0-based visible terminal column, inclusive
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
- scrollback 中的历史按钮。

### 行号计算

v1.3 使用 prompt 作为基准。

**prompt 行必须在渲染时捕获，不能用 `cursorTop` 反推。**

#### 坐标修复经验（来源：`validation-results.md` Step 4「步骤 4 期间完成的坐标修复」）

Step 4 实现过程中经历了三次迭代，前两次的反推方案都在某些场景下失效：

1. **初始方案：`cursorTop - 1`**
   - 假设最后一行显示行总是 `cursorTop - 1`。
   - 失效场景：按钮提示行渲染后，`cursorTop` 已位于提示行下方，而不是正好位于按钮列表下方，导致行号偏移。

2. **临时修复：`cursorTop - 2`**
   - 失效场景：第一次区域捕获发生时 prompt 尚未渲染，`cursorTop - 2` 会让初始菜单过度修正。

3. **最终修复：在 `RenderButtonPrompt()` 中记录实际提示行**
   - 如果存在提示行，则最后一个按钮行 = `promptRow - 1`；
   - 否则，最后一个按钮行 = `cursorTop - 1`。
   - 同时修正了 `FullRefresh()` 的区域记录时机：不再在 `SyncButtonState()` 渲染提示之前记录区域，因为此时会捕获到过期的提示行。

最终修复后,Step 4 MVP 的两轮按钮点击（初始菜单、第二菜单）均能正确提交。

#### 规范约束

基于上述经验，v1.3 明确以下约束：

- prompt 行的唯一可信来源是 `RenderButtonPrompt()` 渲染时的实际位置：

```csharp
// 在 RenderButtonPrompt() 中捕获，渲染完 prompt 行后：
lastPromptRow = <渲染 prompt 行后的实际 terminal row>;
```

- 按钮区域记录必须发生在 prompt 渲染之后，不能在 `SyncButtonState()` 渲染 prompt 之前由 `FullRefresh()` 记录。

- 捕获后的 `lastPromptRow` 即按钮区域行号基准。按钮区域行号：

```csharp
buttonRow = lastPromptRow - distanceFromPrompt;
```

其中：

```text
distanceFromPrompt = 该按钮 terminal row 到最后一个 prompt terminal row 之间的终端行数
```

对于直接位于 prompt 上方的按钮列表，最底部的按钮 `distanceFromPrompt = 1`，向上递增。

注意：

- 按终端渲染行计算，不按游戏逻辑行计算；
- 如果按钮因自动换行占多行，每一行都记录一个 segment；
- v1.3 假设 prompt 不换行。

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

使用 Win32 `ReadConsoleInput()` 读取 `INPUT_RECORD`。

只处理：

```text
EventType == MOUSE_EVENT
```

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
- 坐标越界事件。

释放事件不会重复提交。

## 调试输出

- 调试输出默认关闭。
- 可通过 env var `EMUERA_MOUSE_LOG=1` 开启，仅用于开发 / 验证，不属于生产用户接口。
- 开启时写入文件（默认 `<ExeDir>/debug/mouse.log`，追加模式），**不写 `Console.Error`，不写 `stdout`**。
- 单一开关统一控制：原 spike 阶段的 `EMUERA_DEBUG_MOUSE` / `_VERBOSE` / `_KEYS` / `_REGIONS` / `EMUERA_ENABLE_MOUSE_CLICK` 五个细分开关已合并为 `EMUERA_MOUSE_LOG` 一个，开启后所有鼠标 / 按键 / 区域日志都写入同一文件。
- 原因：Step 3 / Step 4 验证中发现，`Console.Error` 在 PowerShell 终端显示中会与 `stdout` 视觉交错，既干扰判断游戏 `stdout` 是否被污染，也容易把终端回显误判为程序输出。
- 关闭调试时，生产路径不向终端输出任何鼠标相关信息。

## 点击命中规则

鼠标坐标归一化后：

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

## 与键盘输入的关系

- 鼠标和键盘可以同时启用。
- 鼠标点击按钮直接提交。
- `↑/↓` 继续切换当前按钮。
- `Enter` 继续确认当前按钮。
- 输入事件按实际到达顺序处理。
- 鼠标提交后，后续按钮状态交给游戏现有按钮过期逻辑处理。

## 不清理复杂区域的原因

v1.3 不单独设计复杂的按钮区域过期策略。

原因：

- 游戏已有按钮过期逻辑；
- 当前按钮提示只展示可点击按钮；
- v1.3 只记录当前按钮提示；
- 不维护 scrollback 中的历史按钮区域。

因此按钮区域的生命周期跟随当前按钮提示，而不是跟随完整终端历史。

## 不做

v1.3 不做：

- SGR mouse parser；
- raw stdin mouse parser；
- 非 Windows 鼠标支持；
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

以下属于 v1.3 范围内但尚未实测确认，应记录为限制，不视为 MVP 已完全覆盖：

- **边界点击**：左上角 `(0,0)`、右下角 `(WindowHeight-1, WindowWidth-1)` 的 raw 坐标尚未实测（见 `validation-results.md` Step 5 末尾）。越界判断逻辑已设计，但边界值未确认。
- **全角字符 / ANSI 按钮列宽**：当前只验证了 ASCII 按钮（`[0] Hello` 等）的列范围。含全角字符（占 2 列）或 ANSI 转义的按钮，列范围计算未验证，需按实际渲染宽度处理。
- **`RecordButtonRegionsForLine` 中 `buttonWidth<=0` 的 column 重复累加**：宽度为 0 的按钮会让 `column` 被累加两次（`if (buttonWidth <= 0)` 分支内一次，循环末尾又一次），导致后续按钮列范围偏移。该路径仅在按钮渲染宽度为 0 时触发（罕见），本次生产化改造未修复。
- **prompt 换行**：v1.3 假设 prompt 不换行，未实现换行后区域重建。
- **手动滚动 scrollback**：未实现滚动后命中修正。

## 验收标准

### 功能验收

- 在 Windows Terminal / PowerShell 中，点击当前按钮提示中的按钮会提交对应输入。
- 点击 `[0] Hello` 提交 `0`。
- 点击 `[1] Quit` / `[1] Exit` 提交 `1`。
- 点击空白不产生输入。
- 键盘 `↑/↓ + Enter` 行为保持不变。
- pipe 模式行为保持不变。

### 稳定性验收

- 鼠标释放不会重复提交。
- 旧按钮提示不会继续参与命中。
- 坐标越界不会导致异常。
- 鼠标功能失败时静默 fallback 到键盘。
- 退出后 console input mode 恢复（含 Quick Edit）。

### 兼容性验收

- redirected stdin 下不启用鼠标。
- 不支持鼠标的终端不报错。
- 不改变 JSONL、server、WinForms 行为。

### 调试验收

- 默认情况下，stdout / `Console.Error` 无任何鼠标调试输出。
- 调试开启时，鼠标信息只出现在文件中，终端显示不被污染。
