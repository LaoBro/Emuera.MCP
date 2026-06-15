# Phase 1 详细子计划：UI 抽象层 — IConsoleUI 接口与实现

> 目标：创建 `IConsoleUI` 接口及子接口，实现 `HeadlessConsole` 空实现和 `WinFormsConsole` 适配器。本阶段**只新增文件，不修改任何现有代码逻辑**，确保零回归风险。

---

## 1. 背景与输入

通过对 `EmueraConsole.cs`（92 处）和 `EmueraConsole.Print.cs`（5 处）中所有 `window.` 引用的完整扫描，提取出 `MainWindow` 被使用的全部成员。以下清单是接口设计的唯一依据。

---

## 2. 接口成员完整清单

### 2.1 IConsoleUI（主接口）

| 成员 | 类型 | 来源代码行 | 说明 |
|------|------|-----------|------|
| `Created` | `bool` | 296, 302, 793, 817, 2197 | 窗体是否已创建 |
| `ClientWidth` | `int` | 257 | `window.MainPicBox.Width` |
| `ClientHeight` | `int` | 258 | `window.MainPicBox.Height` |
| `Text` | `string` | 1560, 1563, 1574 | 窗口标题栏文本 |
| `IsActive` | `bool` | 302 | 替代 `Form.ActiveForm != null` |
| `Refresh()` | `void` | 63, 758, 1634 | 触发重绘（OnPaint） |
| `Invoke(Action)` | `void` | 835, 881, 886, 1631 | 跨线程调用 |
| `Focus()` | `void` | 461 | 设置焦点 |
| `Close()` | `void` | 1218, 1230, 1505 | 关闭窗口 |
| `Reboot()` | `void` | 499, 1216, 1491 | 重启应用 |
| `ShowConfigDialog()` | `void` | 1510 | 显示配置对话框 |
| `UpdateLastInput()` | `void` | 652 | 更新最后输入时间 |
| `ResetTextBoxPos()` | `void` | 1097 | 重置文本框位置 |
| `ClearRichText()` | `void` | 651 | 清空富文本框 |
| `TextBoxPosChanged` | `bool` | 1096 | 文本框位置是否改变 |
| `TextBoxIgnoreScrollBarChanges` | `bool` | 2538, 2551 | 是否忽略滚动条变更 |
| `GetMousePosition()` | `Point` | 869, 888, 1195, 1322, 2200 | 获取鼠标在客户区的位置（左下基准） |
| `ExitApplication()` | `void` | 499(else分支) | 退出应用程序 |
| `ScrollBar` | `IScrollBar` | 117, 1583, 1676, 2283, 2286, 2535-2550 | 滚动条子接口 |
| `TextBox` | `ITextBox` | 1568, 1627 | 文本框子接口 |
| `ToolTip` | `IToolTip` | 1791, 1812-1846, 1895, 1902, 1937-1956 | 工具提示子接口 |
| `MainPicBox` | `IPictureBox` | 257, 258, 869, 888, 1195, 1322, 1647, 1677, 1717, 1766, 1776, 1844, 2200, 2286, 2335 | 图片框子接口（尺寸、坐标转换） |

### 2.2 IScrollBar（滚动条子接口）

| 成员 | 类型 | 来源代码行 |
|------|------|-----------|
| `Value` | `int` | 117, 1583, 1676, 2283, 2535-2550 |
| `Maximum` | `int` | 117, 1583, 1676, 2535-2550 |
| `Enabled` | `bool` | 2550 |

### 2.3 ITextBox（文本框子接口）

| 成员 | 类型 | 来源代码行 |
|------|------|-----------|
| `Text` | `string` | 1568 |
| `BackColor` | `Color` | 1627 |

### 2.4 IToolTip（工具提示子接口）

| 成员 | 类型 | 来源代码行 |
|------|------|-----------|
| `RemoveAll()` | `void` | 1791 |
| `Show(string, Control, Point)` | `void` | 1844 |
| `Show(string, Control, Point, int)` | `void` | 1846 |
| `InitialDelay` | `int` | 1830, 1833, 1956 |
| `AutoPopDelay` | `int` | 1967 |
| `OwnerDraw` | `bool` | 1812, 1940, 1945 |
| `Draw` | `event` | 1937, 1942 |
| `Popup` | `event` | 1938, 1943 |
| `ForeColor` | `Color` | 1895, 1902, 1950 |
| `BackColor` | `Color` | 1895, 1902, 1951 |

### 2.5 IPictureBox（图片框子接口）

| 成员 | 类型 | 来源代码行 |
|------|------|-----------|
| `Width` | `int` | 257, 1776, 2286 |
| `Height` | `int` | 258, 1647, 1677, 1717, 1766, 2335 |
| `PointToClient(Point)` | `Point` | 869, 888, 1195, 1322, 1844, 2200 |
| `ClientRectangle` | `Rectangle` | 870, 889, 1196, 1323 |

---

## 3. 文件结构与新增文件

```
Emuera/UI/Game/
├── IConsoleUI.cs          # 所有接口定义
├── HeadlessConsole.cs     # 无头空实现
└── WinFormsConsole.cs     # MainWindow 适配器
```

> 原则：本阶段**不修改** `EmueraConsole.cs`、`EmueraConsole.Print.cs`、`MainWindow.cs`、`Program.cs` 等任何现有文件。

---

## 4. 各子阶段详细计划

### Phase 1a：接口定义（IConsoleUI.cs）

**任务**：根据第 2 节清单，编写完整的接口定义文件。

**关键设计决策**：
- `IToolTip.Draw`/`Popup` 使用 C# 事件还是 Action 委托？→ 使用 `event DrawToolTipEventHandler Draw;` 保持与 WinForms 签名兼容，但定义在接口中的是简化委托，避免直接引用 `System.Windows.Forms`。
- `Show(string, Control, Point)` 中的 `Control` 参数如何处理？→ 改为 `Show(string, Point)`，在无头模式下忽略宿主控件。
- `Invoke(Action)` 在无头模式下应同步执行 → `HeadlessConsole.Invoke` 直接调用 `action()`。

**验收标准**：
1. 文件编译通过（可单独 `csc` 编译或 IDE 无报错）
2. 接口成员覆盖第 2 节清单 100%，无遗漏

---

### Phase 1b：HeadlessConsole 空实现（HeadlessConsole.cs）

**任务**：实现 `IConsoleUI` 的所有成员，提供无头环境下的空操作或合理默认值。

**各成员实现策略**：

| 成员 | Headless 实现 |
|------|--------------|
| `Created` | 始终返回 `true` |
| `ClientWidth` / `ClientHeight` | 返回 `Config.WindowX` / `Config.WindowY`（或固定 800x600） |
| `Text` | 空字符串，setter 忽略 |
| `IsActive` | 始终返回 `true` |
| `Refresh()` | 空操作 |
| `Invoke(Action)` | 同步调用 `action()` |
| `Focus()` | 空操作 |
| `Close()` / `Reboot()` | 设置标志位，不实际退出进程 |
| `ShowConfigDialog()` | 空操作 |
| `UpdateLastInput()` | 空操作 |
| `ResetTextBoxPos()` | 空操作 |
| `ClearRichText()` | 空操作 |
| `TextBoxPosChanged` | 始终 `false` |
| `TextBoxIgnoreScrollBarChanges` | 始终 `false`，setter 忽略 |
| `GetMousePosition()` | 返回 `Point.Empty` |
| `ExitApplication()` | 设置退出标志 |
| `ScrollBar` | 返回 `HeadlessScrollBar` 实例（Value=Maximum=0） |
| `TextBox` | 返回 `HeadlessTextBox` 实例 |
| `ToolTip` | 返回 `HeadlessToolTip` 实例 |
| `MainPicBox` | 返回 `HeadlessPictureBox` 实例 |

**验收标准**：
1. `HeadlessConsole` 可被 `new HeadlessConsole()` 创建
2. 所有属性访问不抛异常
3. `Invoke(() => Console.WriteLine("ok"))` 同步输出 "ok"

---

### Phase 1c：WinFormsConsole 适配器（WinFormsConsole.cs）

**任务**：包装现有的 `MainWindow`，将 `IConsoleUI` 调用转发到 `MainWindow` 的对应成员。

**实现要点**：
- 构造函数接收 `MainWindow window`
- `ClientWidth` → `window.MainPicBox.Width`
- `ClientHeight` → `window.MainPicBox.Height`
- `Invoke(Action)` → `window.Invoke(action)`
- `ScrollBar` → 返回内部类 `WinFormsScrollBar`，包装 `window.ScrollBar`
- `TextBox` → 返回内部类 `WinFormsTextBox`，包装 `window.TextBox`
- `ToolTip` → 返回内部类 `WinFormsToolTip`，包装 `window.ToolTip`
- `MainPicBox` → 返回内部类 `WinFormsPictureBox`，包装 `window.MainPicBox`

**验收标准**：
1. `WinFormsConsole` 可被 `new WinFormsConsole(mainWindow)` 创建
2. 所有属性访问返回与直接访问 `MainWindow` 相同的值
3. `Invoke` 在跨线程时正确调度到 UI 线程

---

### Phase 1d：编译与隔离验证

**任务**：确保新增文件与现有项目能一起编译，且未引入任何行为变更。

**步骤**：
1. 将 `IConsoleUI.cs`、`HeadlessConsole.cs`、`WinFormsConsole.cs` 加入 `.csproj`
2. 执行 `dotnet build Emuera/Emuera.csproj -c Debug-NAudio`（NAudio 配置，因非 NAudio 配置含 COMReference 不支持 dotnet build）
3. 运行 `Emuera.exe`，确认 WinForms 模式正常启动
4. 临时写一段启动代码（不提交）验证：
   ```csharp
   var headless = new HeadlessConsole();
   Debug.Assert(headless.Created == true);
   Debug.Assert(headless.ClientWidth > 0);
   ```

**验收标准**：
1. `dotnet build -c Debug-NAudio` 0 错误（非 NAudio 配置含 COMReference，需用 MSBuild）
2. WinForms 模式正常运行（肉眼确认窗口出现）
3. 临时验证代码通过

---

## 5. 风险与缓解

| 风险 | 缓解措施 |
|------|---------|
| 接口遗漏成员 | 基于完整扫描清单（第 2 节），实现后二次核对 |
| `IToolTip` 事件类型与 WinForms 强耦合 | 定义简化委托，适配器层做类型转换 |
| `Invoke` 在无头模式下死锁 | 明确约定 HeadlessConsole 同步执行 |
| 新增文件导致编译变慢 | 文件数仅 +3，影响可忽略 |

---

## 6. 验收前检查清单（提交前自检）

- [ ] `IConsoleUI.cs` 包含所有接口及子接口定义
- [ ] `HeadlessConsole.cs` 实现所有成员，无 `NotImplementedException`
- [ ] `WinFormsConsole.cs` 正确转发到 `MainWindow`
- [ ] `dotnet build -c Debug-NAudio` 通过（或 MSBuild 非 NAudio 配置）
- [ ] WinForms 模式正常启动，无异常
- [ ] 未修改任何现有 `.cs` 文件（除 `.csproj` 添加文件引用外）

---

## 7. 变更日志

| 日期 | 版本 | 变更内容 |
|------|------|---------|
| 2026-06-05 | v0.1 | 基于 `window.` 引用扫描完成 Phase 1 详细子计划 |
