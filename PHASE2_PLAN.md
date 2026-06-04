# Phase 2: 协议层解耦 — 详细子计划

> 目标：移除 `AgentProtocolBase` 及其子类对 `MainWindow` 的强依赖，使其通过 `IConsoleUI` 接口与 UI 层交互。为 Phase 3 无头模式启动做准备。

---

## 2.1 现状分析

### 2.1.1 依赖扫描结果

| 文件 | 行 | 引用 | 用途 | 接口映射 |
|------|-----|------|------|----------|
| `AgentJsonlProtocol.cs` | 22 | `window.MainPicBox?.Height` | 计算可见行数 | `IConsoleUI.ClientHeight` |
| `AgentJsonlProtocol.cs` | 134 | `window.Invoke(...)` | UI 线程调度输入 | `IConsoleUI.Invoke(...)` |
| `AgentJsonlProtocol.cs` | 154 | `window.BeginInvoke(...Close())` | 关闭窗口 | `IConsoleUI.Invoke(...Close())` |
| `AgentCliProtocol.cs` | 33 | `window.BeginInvoke(...ProcessKey)` | UI 线程调度按键 | `IConsoleUI.Invoke(...)` |

- `AgentProtocolBase.cs` 本身无 `window.` 引用，但持有 `MainWindow` 字段并暴露给子类
- `DetectAndRun` 方法签名硬编码 `MainWindow`

### 2.1.2 修改范围

| 文件 | 操作 | 说明 |
|------|------|------|
| `AgentProtocolBase.cs` | 修改 | 字段 `MainWindow window` → `IConsoleUI ui`；构造函数 + `DetectAndRun` 签名 |
| `AgentJsonlProtocol.cs` | 修改 | 构造函数 + 3 处 `window.` → `ui.` |
| `AgentCliProtocol.cs` | 修改 | 构造函数 + 1 处 `window.` → `ui.` |
| `EmueraConsole.cs` | 修改 | `DetectAndRun` 调用处（Phase 4 统一处理，Phase 2 临时兼容） |

---

## 2.2 接口设计

### 2.2.1 IConsoleUI 已具备成员（Phase 1 已完成）

```csharp
public interface IConsoleUI
{
    int ClientHeight { get; }          // ← 替代 window.MainPicBox.Height
    void Invoke(Action action);        // ← 替代 window.Invoke / BeginInvoke
    void Close();                      // ← 替代 window.Close
    // ... 其他成员
}
```

> **注意**：`BeginInvoke` 与 `Invoke` 在跨平台语义上可统一。WinForms 中 `BeginInvoke` 是异步，`Invoke` 是同步；但协议层两处 `BeginInvoke` 实际都不等待返回值，语义上等价于 `Invoke`。为简化接口，统一使用 `Invoke`。

---

## 2.3 修改步骤

### Step 2.3.1 AgentProtocolBase.cs

**变更点：**
1. `using MinorShift.Emuera.Forms;` → `using MinorShift.Emuera.UI.Game;`
2. `protected readonly MainWindow window;` → `protected readonly IConsoleUI ui;`
3. 构造函数参数 `MainWindow window` → `IConsoleUI ui`
4. `DetectAndRun` 签名同步修改

**代码变更（SEARCH/REPLACE）：**

```csharp
// SEARCH
using MinorShift.Emuera.Forms;
// ...
protected readonly MainWindow window;
// ...
internal AgentProtocolBase(EmueraConsole console, MainWindow window)
{
    this.console = console;
    this.window = window;
}
// ...
public static AgentProtocolBase DetectAndRun(EmueraConsole console, MainWindow window)
```

```csharp
// REPLACE
using MinorShift.Emuera.UI.Game;
// ...
protected readonly IConsoleUI ui;
// ...
internal AgentProtocolBase(EmueraConsole console, IConsoleUI ui)
{
    this.console = console;
    this.ui = ui;
}
// ...
public static AgentProtocolBase DetectAndRun(EmueraConsole console, IConsoleUI ui)
```

### Step 2.3.2 AgentJsonlProtocol.cs

**变更点：**
1. 构造函数参数 + `base()` 调用
2. `window.MainPicBox?.Height` → `ui.ClientHeight`
3. `window.Invoke(...)` → `ui.Invoke(...)`
4. `window.BeginInvoke(...)` → `ui.Invoke(...)`

**代码变更（SEARCH/REPLACE）：**

```csharp
// SEARCH
public AgentJsonlProtocol(EmueraConsole console, MainWindow window)
    : base(console, window)
{
    int clientHeight = window.MainPicBox?.Height ?? Config.WindowY;
```

```csharp
// REPLACE
public AgentJsonlProtocol(EmueraConsole console, IConsoleUI ui)
    : base(console, ui)
{
    int clientHeight = ui.ClientHeight;
```

```csharp
// SEARCH
window.Invoke(new Action(() =>
{
    if (console.State == ConsoleState.WaitInput)
        console.PressEnterKey(false, value, false);
}));
```

```csharp
// REPLACE
ui.Invoke(() =>
{
    if (console.State == ConsoleState.WaitInput)
        console.PressEnterKey(false, value, false);
});
```

```csharp
// SEARCH
window.BeginInvoke(new Action(() => window.Close()));
```

```csharp
// REPLACE
ui.Invoke(() => ui.Close());
```

### Step 2.3.3 AgentCliProtocol.cs

**变更点：**
1. 构造函数参数 + `base()` 调用
2. `window.BeginInvoke(...)` → `ui.Invoke(...)`

**代码变更（SEARCH/REPLACE）：**

```csharp
// SEARCH
public AgentCliProtocol(EmueraConsole console, MainWindow window)
    : base(console, window) { }
// ...
window.BeginInvoke(new Action(() => ProcessKey(key)));
```

```csharp
// REPLACE
public AgentCliProtocol(EmueraConsole console, IConsoleUI ui)
    : base(console, ui) { }
// ...
ui.Invoke(() => ProcessKey(key));
```

### Step 2.3.4 EmueraConsole.cs（临时兼容）

> Phase 4 才会将 `EmueraConsole` 的 `window` 字段改为 `IConsoleUI`。Phase 2 只需在 `DetectAndRun` 调用处做临时适配。

**定位调用点：**
```bash
grep -n "DetectAndRun" Emuera/UI/Game/EmueraConsole.cs
```

**临时适配方案（二选一，Phase 4 统一清理）：**
- **方案 A**：调用处直接传 `new WinFormsConsole(window)`（引入一次临时 new，Phase 4 删除）
- **方案 B**：`EmueraConsole` 新增 `IConsoleUI UIAdapter { get; }` 属性，在构造时创建 `WinFormsConsole` 实例

**推荐方案 B**，因为 Phase 4 正好需要这个属性。

```csharp
// 在 EmueraConsole.cs 中新增（SEARCH/REPLACE）
// SEARCH
private MainWindow window;
// ...
public EmueraConsole(MainWindow mainWindow)
{
    window = mainWindow;
```

```csharp
// REPLACE
private MainWindow window;
private readonly IConsoleUI _uiAdapter;
public IConsoleUI UIAdapter => _uiAdapter;
// ...
public EmueraConsole(MainWindow mainWindow)
{
    window = mainWindow;
    _uiAdapter = new WinFormsConsole(mainWindow);
```

然后 `DetectAndRun` 调用改为传 `_uiAdapter`。

---

## 2.4 验收标准

```bash
# 1. 编译通过（NAudio 配置）
dotnet build Emuera/Emuera.csproj -c Debug-NAudio

# 2. 协议层无 MainWindow 引用
grep -r "MainWindow" Emuera/UI/Game/Agent*.cs
# → 应无结果（仅 using 语句可保留，但推荐清理）

# 3. WinForms 模式功能正常
Emuera.exe
# → 原有窗口启动正常

# 4. JSONL 管道模式功能正常
echo '{"type":"input","value":""}' | Emuera.exe
# → 输出初始回合 JSON（需 Phase 3 完成后才能真正无头运行，Phase 2 仅验证编译与接口）
```

---

## 2.5 检查清单

- [ ] `AgentProtocolBase.cs` 字段/构造函数/DetectAndRun 签名改为 `IConsoleUI`
- [ ] `AgentJsonlProtocol.cs` 3 处 `window.` 改为 `ui.`，构造函数签名更新
- [ ] `AgentCliProtocol.cs` 1 处 `window.` 改为 `ui.`，构造函数签名更新
- [ ] `EmueraConsole.cs` 新增 `UIAdapter` 属性，调用 `DetectAndRun` 时传入 `_uiAdapter`
- [ ] `dotnet build -c Debug-NAudio` 通过
- [ ] WinForms 模式肉眼验证正常

---

## 2.6 风险与缓解

| 风险 | 影响 | 缓解 |
|------|------|------|
| `BeginInvoke` → `Invoke` 语义差异 | 极低 | 两处 `BeginInvoke` 均不等待返回值，改为 `Invoke` 无行为变更 |
| `window.MainPicBox?.Height` → `ui.ClientHeight` 空值行为 | 低 | `WinFormsConsole.ClientHeight` 直接访问 `_window.MainPicBox.Width`，与原来一致；`HeadlessConsole` 返回 `Config.WindowY`，与原来 `?? Config.WindowY` 兜底一致 |
| `EmueraConsole` 临时引入 `WinFormsConsole` new | 中 | Phase 4 会统一将 `window` 字段改为 `IConsoleUI`，届时删除临时 new |

---

## 2.7 与后续 Phase 的衔接

- **Phase 3**：`Program.cs` 创建 `HeadlessConsole` 实例传入 `DetectAndRun`，无需修改协议层
- **Phase 4**：`EmueraConsole` 将 `window` 字段彻底改为 `IConsoleUI`，删除 `WinFormsConsole` 临时 new
