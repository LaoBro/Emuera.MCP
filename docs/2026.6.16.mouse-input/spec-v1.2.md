# CLI 鼠标点击按钮 — v1.2 修订规格

## 版本信息

- 版本：v1.2
- 基线：`docs/2026.6.16.mouse-input/spec-v1.1.md`
- 目标文件：`Emuera.Headless/Agent/AgentCliProtocol.cs`
- 目标平台：Windows Console
- 输入 backend：Win32 `ReadConsoleInput()`

## 一句话总结

让 Windows CLI 用户直接用鼠标点击当前按钮提示中的按钮文字；键盘 `↑/↓ + Enter` 仍然作为 fallback。

## 范围

v1.2 只做 Windows 原生控制台鼠标输入：

- 使用 Win32 `ReadConsoleInput()` 读取鼠标事件；
- 只处理左键按下；
- 只处理当前按钮提示中可见的按钮；
- 不实现 SGR mouse parser；
- 不实现 raw stdin mouse parser；
- 不实现非 Windows 鼠标路径。

SGR / raw stdin 的验证经验只作为背景，不进入 v1.2 实现范围。

## 基本假设

v1.2 基于以下前提设计：

1. 等待输入时，prompt 一定是当前终端最后一行。
2. v1.2 MVP 假设 prompt 不会换行。
3. 用户不会手动操作滚动条；鼠标只用于点击当前可见按钮。
4. 游戏已有按钮过期逻辑，当前按钮提示只展示可点击按钮。
5. 按钮区域只记录当前按钮提示，不维护复杂 scrollback 坐标。
6. pipe / redirected stdin 模式不启用鼠标。

如果未来需要支持 prompt 换行，可以继续基于最终光标位置确认最后一个 prompt 行；但这不属于 v1.2 MVP。

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

v1.2 使用 `MOUSE_EVENT_RECORD.dwMousePosition`。

在当前验证环境中，Windows Terminal 下该坐标已经是可见视口坐标，且：

```text
Console.WindowTop = 0
Console.WindowLeft = 0
```

实现时仍建议做防御性归一化：

```csharp
row = mouseY - Console.WindowTop;
col = mouseX - Console.WindowLeft;
```

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

v1.2 使用 prompt 作为基准。

等待输入时：

```csharp
lastPromptRow = Console.WindowHeight - 1;
```

按钮区域行号：

```csharp
buttonRow = lastPromptRow - rowsBelowPrompt;
```

其中：

```text
rowsBelowPrompt = 该按钮 terminal row 到最后一个 prompt terminal row 之间的终端行数
```

注意：

- 按终端渲染行计算，不按游戏逻辑行计算；
- 如果按钮因自动换行占多行，每一行都记录一个 segment；
- v1.2 假设 prompt 不换行。

## 鼠标事件处理

### 启用条件

只在真实交互 CLI 中启用鼠标：

```csharp
OperatingSystem.IsWindows()
&& !Console.IsInputRedirected
```

pipe 模式不启用鼠标，不解析鼠标事件，不改变现有文本输入逻辑。

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

v1.2 不单独设计复杂的按钮区域过期策略。

原因：

- 游戏已有按钮过期逻辑；
- 当前按钮提示只展示可点击按钮；
- v1.2 只记录当前按钮提示；
- 不维护 scrollback 中的历史按钮区域。

因此按钮区域的生命周期跟随当前按钮提示，而不是跟随完整终端历史。

## 不做

v1.2 不做：

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
- 生产路径 stdout 鼠标调试输出。

## 清理要求

退出鼠标输入时：

- 停止读取 Win32 console input；
- 恢复原始 console input mode；
- 清空当前按钮区域；
- 不向 stdout 输出鼠标调试信息。

pipe 模式不调用鼠标启用 / 禁用逻辑。

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
- 退出后 console input mode 恢复。

### 兼容性验收

- redirected stdin 下不启用鼠标。
- 不支持鼠标的终端不报错。
- 不改变 JSONL、server、WinForms 行为。
