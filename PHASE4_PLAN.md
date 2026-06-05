# Phase 4: 核心控制器改造 — EmueraConsole 解耦 WinForms

> 目标：`EmueraConsole` 不再直接引用 `MainWindow`，改为通过 `IConsoleUI` 交互。无头模式下 `window` 字段为 `null`，所有 UI 操作通过 `_uiAdapter` 完成。

---

## 4.1 现状分析

### 4.1.1 当前字段与双构造函数

```csharp
private readonly MainWindow window;          // WinForms 强引用
private readonly IConsoleUI _uiAdapter;      // 抽象接口（Phase 1 已添加）
public MainWindow Window { get { return window; } }
public IConsoleUI UIAdapter => _uiAdapter;
```

已有两个构造函数：
- `public EmueraConsole(MainWindow parent)` — WinForms 模式，设置 `window = parent`
- `public EmueraConsole(IConsoleUI ui)` — 无头模式，`window = null`，`_uiAdapter = ui`

### 4.1.2 `window.` 引用完整清单（共 92 行）

| 类别 | 行号 | 代码片段 | 替换目标 |
|------|------|---------|---------|
| **尺寸** | 289-290 | `window.MainPicBox.Width/Height` | `_uiAdapter.MainPicBox.Width/Height` |
| **生命周期** | 331 | `window.Created` | `_uiAdapter.Created` |
| **激活状态** | 337 | `Form.ActiveForm == null` | `_uiAdapter.IsActive` |
| **焦点** | 496 | `window.Focus()` | `_uiAdapter.Focus()` |
| **Reboot** | 534, 1251, 1526 | `window.Reboot()` | `_uiAdapter.Reboot()` |
| **输入辅助** | 687 | `window.update_lastinput()` | `_uiAdapter.UpdateLastInput()` |
| **刷新** | 793, 1669 | `window.Refresh()` | `_uiAdapter.Refresh()` |
| **Invoke** | 870, 916, 921 | `window.Invoke(...)` | `_uiAdapter.Invoke(...)` |
| **鼠标位置** | 904, 923, 1230, 1357, 1874 | `window.MainPicBox.PointToClient(...)` | `_uiAdapter.GetMousePosition()` 或 `_uiAdapter.MainPicBox.PointToClient(...)` |
| **鼠标区域** | 905, 924, 1231, 1358 | `window.MainPicBox.ClientRectangle.Contains(...)` | `_uiAdapter.MainPicBox.ClientRectangle.Contains(...)` |
| **文本框位置** | 1131-1132 | `window.TextBoxPosChanged` / `ResetTextBoxPos()` | `_uiAdapter.TextBoxPosChanged` / `ResetTextBoxPos()` |
| **关闭** | 1253, 1265, 1540 | `window.Close()` | `_uiAdapter.Close()` |
| **配置对话框** | 1545 | `window.ShowConfigDialog()` | `_uiAdapter.ShowConfigDialog()` |
| **标题** | 1595-1598 | `window.Text = ...` | `_uiAdapter.Text = ...` |
| **文本框内容** | 1603 | `window.TextBox.Text = ...` | `_uiAdapter.TextBox.Text = ...` |
| **标题读取** | 1609 | `return window.Text` | `return _uiAdapter.Text` |
| **滚动条** | 1618, 1683, 1711, 1715, 2318, 2321, 2570-2586 | `window.ScrollBar.Value/Maximum/Enabled` | `_uiAdapter.ScrollBar.Value/Maximum/Enabled` |
| **文本框颜色** | 1662 | `window.TextBox.BackColor = ...` | `_uiAdapter.TextBox.BackColor = ...` |
| **绘图高度** | 1682, 1712, 1752, 1801, 1804, 2324, 2336, 2370, 2425 | `window.MainPicBox.Height` | `_uiAdapter.MainPicBox.Height` |
| **绘图宽度** | 1811 | `window.MainPicBox.Width` | `_uiAdapter.MainPicBox.Width` |
| **ToolTip** | 1826, 1865, 1879, 1881, 1930, 1937, 1972-2002 | `window.ToolTip.RemoveAll/Show/Draw/Popup/ForeColor/BackColor/InitialDelay/AutoPopDelay/OwnerDraw` | `_uiAdapter.ToolTip.*` |
| **忽略滚动条** | 2573, 2586 | `window.TextBoxIgnoreScrollBarChanges` | `_uiAdapter.TextBoxIgnoreScrollBarChanges` |
| **GetMousePosition** | 2232-2235 | `window.MainPicBox.PointToClient(Cursor.Position)` | 已有 `GetMousePosition()` 方法，内部改用 `_uiAdapter` |
| **Application.Exit** | 538 | `Application.Exit()` | `_uiAdapter.ExitApplication()` |
| **Application.DoEvents** | 1666 | `Application.DoEvents()` | 条件编译或 `_uiAdapter.ProcessEvents()`（无头模式空操作） |

### 4.1.3 特殊处理点

| 问题 | 位置 | 处理方案 |
|------|------|---------|
| `Form.ActiveForm` | 337 | `IsActive` 已在 `IConsoleUI` 中定义，WinForms 适配器返回 `Form.ActiveForm != null`，Headless 返回 `true` |
| `Control.MousePosition` | 904, 923 等 | 继续使用 `Control.MousePosition`（WinForms 静态属性），但在无头模式下通过 `GetMousePosition()` 封装；无头模式返回 `Point.Empty` |
| `Cursor.Position` | 1874, 2235 | 同上，WinForms 模式下仍可用，`GetMousePosition()` 内部处理 |
| `Screen.FromPoint` | 1876 | 仅 WinForms 模式有意义，ToolTip 在无头模式为空操作 |
| `MessageBox.Show` | 510-530 | `ForceQuit()` 中的 `MessageBox` 属于业务逻辑，保留在 WinForms 模式下；无头模式下直接退出 |
| `Application.DoEvents()` | 1666 | 无头模式不需要消息泵，HeadlessConsole 不实现；WinFormsConsole 调用 `Application.DoEvents()` |

---

## 4.2 修改方案

### 4.2.1 字段与属性替换（Phase 4a）

**文件：** `Emuera/UI/Game/EmueraConsole.cs`

**Step 4a.1：删除 `window` 字段，统一使用 `_uiAdapter`**

```csharp
// 删除
// private readonly MainWindow window;
// public MainWindow Window { get { return window; } }

// 保留并增强
private readonly IConsoleUI _uiAdapter;
public IConsoleUI UIAdapter => _uiAdapter;
```

**Step 4a.2：修改构造函数**

```csharp
public EmueraConsole(MainWindow parent)
{
    _uiAdapter = new WinFormsConsole(parent);
    // 删除 window = parent;
    // ... 其余不变
}

public EmueraConsole(IConsoleUI ui)
{
    _uiAdapter = ui;
    // ... 其余不变
}
```

**Step 4a.3：替换 `ClientWidth` / `ClientHeight` / `Enabled` / `IsActive`**

```csharp
public int ClientWidth { get { return _uiAdapter.MainPicBox.Width; } }
public int ClientHeight { get { return _uiAdapter.MainPicBox.Height; } }
public bool Enabled { get { return _uiAdapter.Created; } }
internal bool IsActive
{ get { return !(_uiAdapter == null || !_uiAdapter.Created || !_uiAdapter.IsActive); } }
```

**Step 4a.4：替换 `Window` 属性外部引用**

搜索整个解决方案中 `emuera.Window` 或 `.Window` 的引用，改为 `.UIAdapter`。目前仅在 `EE_MOUSEB` 区域使用：

```csharp
// #region EE_MOUSEB
// public MainWindow Window { get { return window; } }
// #endregion
// 改为：
#region EE_MOUSEB
public IConsoleUI UI => _uiAdapter;
#endregion
```

> **注意：** 如果外部代码（如 `MainWindow.cs` 或其他类）有 `console.Window` 引用，需要同步修改。Phase 4a 验收前需全局搜索确认。

### 4.2.2 Invoke 与消息循环解耦（Phase 4b）

**文件：** `Emuera/UI/Game/EmueraConsole.cs`

**Step 4b.1：替换所有 `window.Invoke(...)`**

| 原代码 | 新代码 |
|--------|--------|
| `window.Invoke(() => changeLastLine(...))` | `_uiAdapter.Invoke(() => changeLastLine(...))` |
| `window.Invoke(() => { verticalScrollBarUpdate(); window.Refresh(); })` | `_uiAdapter.Invoke(() => { verticalScrollBarUpdate(); _uiAdapter.Refresh(); })` |

**Step 4b.2：`Application.DoEvents()` 处理**

在 `RefreshStrings` 方法中：

```csharp
// 原代码
while (_drawStopwatch.ElapsedMilliseconds < msPerFrame)
{
    Application.DoEvents();
}

// 新代码：条件编译或接口扩展
while (_drawStopwatch.ElapsedMilliseconds < msPerFrame)
{
    _uiAdapter.ProcessEvents?.Invoke();
}
```

**接口扩展：**

```csharp
// IConsoleUI.cs
internal interface IConsoleUI
{
    // ... 现有成员 ...
    void ProcessEvents(); // WinForms 调用 Application.DoEvents()，Headless 空实现
}
```

**Step 4b.3：`Application.Exit()` 替换**

在 `ForceQuit()` 中：

```csharp
// 原代码
Application.Exit();

// 新代码
_uiAdapter.ExitApplication();
```

### 4.2.3 输入与鼠标系统解耦（Phase 4c）

**文件：** `Emuera/UI/Game/EmueraConsole.cs`

**Step 4c.1：`GetMousePosition()` 内部改用 `_uiAdapter`**

```csharp
internal Point GetMousePosition()
{
    if (_uiAdapter == null || !_uiAdapter.Created)
        return new Point();
    return _uiAdapter.GetMousePosition();
}
```

**Step 4c.2：替换所有 `window.MainPicBox.PointToClient(Control.MousePosition)`**

这些代码分布在 `endTimer` (904)、`window.Invoke` 内部 (923)、`endMacro` (1230)、`OpenErrorFile` 后 (1357)、ToolTip 异步代码 (1874)。

统一替换为：

```csharp
// 原代码
Point point = window.MainPicBox.PointToClient(Control.MousePosition);
if (window.MainPicBox.ClientRectangle.Contains(point))
    MoveMouse(point);

// 新代码
Point point = GetMousePosition();
// GetMousePosition 已转换为左下基准，但 MoveMouse 期望的是左上基准
// 需要检查：GetMousePosition 当前实现是左下基准（Y -= ClientHeight）
// 而 MoveMouse 内部第一行就是 clientPoint.Y = point.Y - ClientHeight
// 所以这里存在双重转换问题

// 修正方案：新增一个获取原始鼠标位置的方法，或调整调用点
```

**详细分析 `GetMousePosition` 与调用点的坐标系：**

```csharp
// 当前 GetMousePosition()
Point pos = window.MainPicBox.PointToClient(Cursor.Position);
pos.Y -= ClientHeight;  // 转为左下基准
return pos;

// 调用点（如 endTimer 中）
Point point = window.MainPicBox.PointToClient(Control.MousePosition); // 左上基准
if (window.MainPicBox.ClientRectangle.Contains(point)) // 左上基准
    MoveMouse(point); // MoveMouse 内部第一行：clientPoint.Y = point.Y - ClientHeight

// 结论：GetMousePosition 返回的是左下基准，不能直接替代这里的 point
```

**解决方案：** 不替换为 `GetMousePosition()`，而是直接使用 `_uiAdapter.MainPicBox`：

```csharp
// 新代码
Point point = _uiAdapter.MainPicBox.PointToClient(Control.MousePosition);
if (_uiAdapter.MainPicBox.ClientRectangle.Contains(point))
    MoveMouse(point);
```

> 但 `Control.MousePosition` 是 WinForms 静态属性，无头模式下不可用。由于这些代码只在 `state == ConsoleState.WaitInput && inputReq.NeedValue` 时执行，而无头模式下没有鼠标输入，所以实际上不会走到这些分支。为安全起见，添加 null 检查：

```csharp
if (_uiAdapter.MainPicBox != null)
{
    Point point = _uiAdapter.MainPicBox.PointToClient(Control.MousePosition);
    if (_uiAdapter.MainPicBox.ClientRectangle.Contains(point))
        MoveMouse(point);
}
```

**Step 4c.3：`Form.ActiveForm` 已处理**

在 4a.3 中已将 `Form.ActiveForm` 替换为 `_uiAdapter.IsActive`。

### 4.2.4 绘图与定时器解耦（Phase 4d）

**文件：** `Emuera/UI/Game/EmueraConsole.cs`

**Step 4d.1：替换 `window.Refresh()`**

```csharp
// tickRedrawTimer 中
// window.Refresh();
_uiAdapter.Refresh();

// RefreshStrings 中
// window.Invoke(() => { verticalScrollBarUpdate(); window.Refresh(); });
_uiAdapter.Invoke(() => { verticalScrollBarUpdate(); _uiAdapter.Refresh(); });
```

**Step 4d.2：替换 `window.ScrollBar` 所有引用**

```csharp
// 原：window.ScrollBar.Value / Maximum / Enabled
// 新：_uiAdapter.ScrollBar.Value / Maximum / Enabled

// verticalScrollBarUpdate 方法中全部替换
window.TextBoxIgnoreScrollBarChanges = true;
// → _uiAdapter.TextBoxIgnoreScrollBarChanges = true;
window.ScrollBar.Maximum = max;
// → _uiAdapter.ScrollBar.Maximum = max;
// ... etc
```

**Step 4d.3：替换 `window.TextBox`**

```csharp
// 原：window.TextBox.Text = str;
// 新：_uiAdapter.TextBox.Text = str;

// 原：window.TextBox.BackColor = bgColor;
// 新：_uiAdapter.TextBox.BackColor = bgColor;
```

**Step 4d.4：替换 `window.Text` / `window.ToolTip`**

```csharp
// SetWindowTitle
_uiAdapter.Text = str + " (Debug Mode)";

// GetWindowTitle
return _uiAdapter.Text;

// ToolTip 系列全部替换为 _uiAdapter.ToolTip.*
```

**Step 4d.5：替换 `window.MainPicBox.Height/Width`（OnPaint 及辅助方法）**

```csharp
// OnPaint 中
int pointY = _uiAdapter.MainPicBox.Height - Config.LineHeight;
// ...
int topLineNo = bottomLineNo - (_uiAdapter.MainPicBox.Height / Config.LineHeight);
// ...
int relPointY = pointY - _uiAdapter.MainPicBox.Height;
// ...
var bottomLineBase = _uiAdapter.MainPicBox.Height - Config.LineHeight;
// ...
curLineY = _uiAdapter.MainPicBox.Height - Config.LineHeight * (bottomLineNo - i + 1);

// rikaichan.OnPaint
rikaichan.OnPaint(graph, stringMeasure, _uiAdapter.MainPicBox.Width);

// cbg GraphicsDraw
img.GraphicsDraw(graph, new Point(cbgList[cidx].x, cbgList[cidx].y + _uiAdapter.MainPicBox.Height - img.DestBaseSize.Height));
```

**Step 4d.6：`redrawTimer` 保持现状**

`redrawTimer` 是 `System.Windows.Forms.Timer`，仅在 WinForms 模式下使用。无头模式下 `redrawTimer.Enabled = false`，不会触发。无需替换。

### 4.2.5 ToolTip 特殊事件处理

**文件：** `Emuera/UI/Game/EmueraConsole.cs`

`ToolTip_Draw` 和 `ToolTip_Popup` 方法签名使用了 `System.Windows.Forms.DrawToolTipEventArgs` 和 `PopupEventArgs`，这是 WinForms 专用类型。

**当前绑定方式：**

```csharp
window.ToolTip.Draw += new DrawToolTipEventHandler(ToolTip_Draw);
window.ToolTip.Popup += new PopupEventHandler(ToolTip_Popup);
```

**问题：** `IToolTip` 接口已定义了自定义的 `ToolTipDrawEventArgs` 和 `ToolTipPopupEventArgs`，但 `EmueraConsole` 内部方法仍使用 WinForms 类型。

**解决方案：**

1. 在 `WinFormsConsole` 的 `WinFormsToolTip` 中，已将 WinForms 事件转换为自定义事件（`OnDraw` / `OnPopup` 方法）。
2. `EmueraConsole` 中应改为订阅 `_uiAdapter.ToolTip.Draw` / `Popup` 自定义事件，而非直接操作 WinForms 事件。

**修改：**

```csharp
// CustomToolTip 方法中
public void CustomToolTip(bool b)
{
    if (!b)
    {
        _uiAdapter.ToolTip.Draw -= ToolTip_Draw_Custom;
        _uiAdapter.ToolTip.Popup -= ToolTip_Popup_Custom;
    }
    else if (!_uiAdapter.ToolTip.OwnerDraw)
    {
        _uiAdapter.ToolTip.Draw += ToolTip_Draw_Custom;
        _uiAdapter.ToolTip.Popup += ToolTip_Popup_Custom;
    }
    _uiAdapter.ToolTip.OwnerDraw = b;
}

// 新增适配方法
private void ToolTip_Draw_Custom(object sender, ToolTipDrawEventArgs e)
{
    // 将自定义 EventArgs 转换为 ToolTip_Draw 内部逻辑
    // 或直接修改 ToolTip_Draw 方法签名接受自定义 EventArgs
}
```

> **简化方案：** 由于 `ToolTip_Draw` / `ToolTip_Popup` 方法内部主要使用 `e.Graphics`、`e.ToolTipText`、`e.Bounds` 等属性，这些在自定义 `ToolTipDrawEventArgs` 中已定义。直接将方法签名改为接受自定义类型即可。

---

## 4.3 修改步骤汇总

### Phase 4a: 字段与属性替换

- [ ] **Step 4a.1** 删除 `private readonly MainWindow window` 字段
- [ ] **Step 4a.2** 删除 `public MainWindow Window` 属性，或改为返回 `WinFormsConsole` 的兼容属性（如外部有引用）
- [ ] **Step 4a.3** 修改 `EmueraConsole(MainWindow)` 构造函数，内部创建 `WinFormsConsole` 赋值给 `_uiAdapter`
- [ ] **Step 4a.4** 替换 `ClientWidth`、`ClientHeight`、`Enabled`、`IsActive`
- [ ] **Step 4a.5** 全局搜索 `.Window` 引用，改为 `.UIAdapter`

### Phase 4b: Invoke 与消息循环解耦

- [ ] **Step 4b.1** 替换所有 `window.Invoke(...)` 为 `_uiAdapter.Invoke(...)`
- [ ] **Step 4b.2** `IConsoleUI` 接口添加 `ProcessEvents()` 方法
- [ ] **Step 4b.3** `HeadlessConsole` 实现空 `ProcessEvents()`
- [ ] **Step 4b.4** `WinFormsConsole` 实现 `ProcessEvents()` 调用 `Application.DoEvents()`
- [ ] **Step 4b.5** 替换 `Application.DoEvents()` 为 `_uiAdapter.ProcessEvents()`
- [ ] **Step 4b.6** 替换 `Application.Exit()` 为 `_uiAdapter.ExitApplication()`

### Phase 4c: 输入与鼠标系统解耦

- [ ] **Step 4c.1** 修改 `GetMousePosition()` 内部使用 `_uiAdapter`
- [ ] **Step 4c.2** 替换所有 `window.MainPicBox.PointToClient(Control.MousePosition)` 为 `_uiAdapter.MainPicBox.PointToClient(Control.MousePosition)`（加 null 检查）
- [ ] **Step 4c.3** 替换所有 `window.MainPicBox.ClientRectangle.Contains(...)` 为 `_uiAdapter.MainPicBox.ClientRectangle.Contains(...)`

### Phase 4d: 绘图与定时器解耦

- [ ] **Step 4d.1** 替换所有 `window.Refresh()` 为 `_uiAdapter.Refresh()`
- [ ] **Step 4d.2** 替换所有 `window.ScrollBar.*` 为 `_uiAdapter.ScrollBar.*`
- [ ] **Step 4d.3** 替换所有 `window.TextBox.*` 为 `_uiAdapter.TextBox.*`
- [ ] **Step 4d.4** 替换所有 `window.Text` 为 `_uiAdapter.Text`
- [ ] **Step 4d.5** 替换所有 `window.ToolTip.*` 为 `_uiAdapter.ToolTip.*`
- [ ] **Step 4d.6** 替换所有 `window.MainPicBox.Height/Width` 为 `_uiAdapter.MainPicBox.Height/Width`
- [ ] **Step 4d.7** 修改 `ToolTip_Draw` / `ToolTip_Popup` 方法签名，使用自定义 `ToolTipDrawEventArgs` / `ToolTipPopupEventArgs`
- [ ] **Step 4d.8** 修改 `CustomToolTip` 方法，订阅 `_uiAdapter.ToolTip` 的自定义事件

---

## 4.4 接口补充清单

基于扫描结果，`IConsoleUI` 及子接口已覆盖全部引用点，无需新增成员。但需确认以下成员已在 Phase 1 中定义：

| 成员 | 状态 | 说明 |
|------|------|------|
| `IConsoleUI.ProcessEvents()` | **需新增** | Phase 4b 中使用 |
| `IConsoleUI.MainPicBox` | 已定义 | `IPictureBox` 类型 |
| `IPictureBox.PointToClient(Point)` | 已定义 | Phase 4c 中使用 |
| `IPictureBox.ClientRectangle` | 已定义 | Phase 4c 中使用 |
| `IToolTip.Draw` / `Popup` 事件 | 已定义 | Phase 4d 中使用，但需确认事件参数类型匹配 |

---

## 4.5 验收标准

### Phase 4a 验收

```bash
# 1. 编译通过（NAudio 配置）
dotnet build Emuera/Emuera.csproj -c Debug-NAudio

# 2. WinForms 模式正常启动
Emuera.exe
# → 窗口出现，无编译错误
```

### Phase 4b 验收

```bash
# 1. 编译通过
dotnet build -c Debug-NAudio

# 2. WinForms 模式按钮点击、输入响应正常
# → 测试 INPUT、按钮、菜单等交互
```

### Phase 4c 验收

```bash
# 1. 编译通过
dotnet build -c Debug-NAudio

# 2. 鼠标悬停/点击/宏功能正常
# → 测试鼠标悬停 ToolTip、按钮点击、宏执行
```

### Phase 4d 验收

```bash
# 1. 编译通过
dotnet build -c Debug-NAudio

# 2. 画面刷新/滚动/定时器功能正常
# → 测试滚动条、画面刷新、TINPUT 定时器
```

### Phase 4 整体验收

```bash
# 1. 编译通过（NAudio 配置）
dotnet build -c Debug-NAudio

# 2. WinForms 模式完整功能测试
# - 正常游戏流程
# - 按钮点击、输入、宏、定时器
# - 调试窗口、配置对话框

# 3. --headless 参数测试
Emuera.exe --headless < test_input.txt
# → 能读取脚本、输出文本到 stdout
# → JSONL 协议正常交互
```

---

## 4.6 风险与缓解

| 风险 | 影响 | 缓解 |
|------|------|------|
| `window` 字段删除后外部代码编译失败 | 高 | Phase 4a 前全局搜索 `\.Window\b` 和 `window` 字段引用，同步修改 |
| `Control.MousePosition` 在无头模式下理论上不可达，但静态分析可能报警 | 低 | 保留 `Control.MousePosition` 调用，但加 `_uiAdapter.MainPicBox != null` 保护 |
| `ToolTip` 事件参数类型变更导致编译错误 | 中 | 同时修改 `EmueraConsole` 方法签名和 `WinFormsConsole` 事件转发逻辑 |
| `Application.DoEvents()` 替换后 WinForms 模式性能变化 | 低 | `WinFormsConsole.ProcessEvents()` 直接调用 `Application.DoEvents()`，行为完全一致 |
| 坐标系混乱（`GetMousePosition` 左下基准 vs `PointToClient` 左上基准） | 中 | 不混用 `GetMousePosition()` 替代 `PointToClient`，各自保持独立 |

---

## 4.7 与后续 Phase 的衔接

- **Phase 5**：`--headless` 启动后进行端到端验证，确认 JSONL 协议在无窗体环境下完整运行
- **Phase 6**：服务器模式添加 TCP/HTTP 接口，复用已解耦的 `EmueraConsole`
- **Phase 7**：Python 网关扩展，C# 端无需再改动 UI 相关代码

---

## 4.8 检查清单

- [ ] `EmueraConsole.cs` 删除 `window` 字段和 `Window` 属性
- [ ] `EmueraConsole.cs` 修改 `MainWindow` 构造函数，创建 `WinFormsConsole`
- [ ] `EmueraConsole.cs` 替换所有 `window.` 为 `_uiAdapter.`（按 4a-4d 分组）
- [ ] `IConsoleUI.cs` 添加 `ProcessEvents()` 方法
- [ ] `HeadlessConsole.cs` 实现 `ProcessEvents()` 空方法
- [ ] `WinFormsConsole.cs` 实现 `ProcessEvents()` 调用 `Application.DoEvents()`
- [ ] `EmueraConsole.cs` `ToolTip_Draw` / `ToolTip_Popup` 方法签名改为自定义 EventArgs
- [ ] `EmueraConsole.cs` `CustomToolTip` 方法订阅 `_uiAdapter.ToolTip` 自定义事件
- [ ] 全局搜索并修复外部对 `console.Window` 的引用
- [ ] `dotnet build -c Debug-NAudio` 通过
- [ ] WinForms 模式肉眼验证正常
- [ ] `--headless` 模式能启动并进入 JSONL 交互
