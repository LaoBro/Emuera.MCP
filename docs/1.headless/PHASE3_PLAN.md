# Phase 3: 入口改造 — 支持 `--headless` 无头模式启动

> 目标：修改 `Program.cs`，使其支持通过 `--headless` 参数启动无头模式。无头模式下不创建 `MainWindow`，而是创建 `HeadlessConsole` 实例，直接启动 `EmueraConsole` 和协议层。

---

## 3.1 现状分析

### 3.1.1 Program.cs 启动流程

```
Main(args)
├── 参数解析 (System.CommandLine)
├── 设置目录路径
├── 加载配置 (ConfigData, JSONConfig)
├── 加载语言文件
├── 加载图标
├── 检查目录存在 (csv, erb)
├── 加载字体文件
├── Debug 模式处理
├── Analysis 模式处理
├── 创建 MainWindow
│   └── MainWindow 构造函数中创建 EmueraConsole
│       └── EmueraConsole 构造函数中调用 DetectAndRun
├── Application.Run(win)
└── 退出
```

### 3.1.2 WinForms 依赖点

| 行 | 代码 | 无头模式处理 |
|----|------|-------------|
| 150 | `Application.SetCompatibleTextRenderingDefault(false)` | 跳过 |
| 187,193,199,225 | `Dialog.Show(...)` | 改为 `Console.Error.WriteLine` + `Environment.Exit(1)` |
| 241,259 | `MessageBox.Show(...)` | 改为 `Console.Error.WriteLine` + `Environment.Exit(1)` |
| 271 | `using var win = new Forms.MainWindow(args)` | 跳过，创建 `HeadlessConsole` |
| 281 | `Application.Run(win)` | 跳过，手动运行主循环 |

### 3.1.3 MainWindow 构造函数中的关键操作

`MainWindow` 构造函数除了创建 `EmueraConsole`，还做了以下事情：
- 初始化控件样式（`SetStyle`, `initControlSizeAndLocation`）
- 设置颜色、字体
- 设置对话框路径
- 设置宏菜单项

**无头模式下需要保留的：**
- 创建 `EmueraConsole`
- `EmueraConsole` 内部会调用 `DetectAndRun`，这在无头模式下是期望的（JSONL 协议）

**无头模式下不需要的：**
- 所有 UI 控件初始化
- `Application.Run` 消息循环

---

## 3.2 修改方案

### 3.2.1 新增 `--headless` 参数

在 `System.CommandLine` 参数解析中添加：

```csharp
var headlessOption = new Option<bool>(
    name: "--headless",
    description: "无头模式：不创建 GUI 窗口，通过 stdin/stdout 进行 JSONL 交互"
);
headlessOption.AddAlias("-headless");
headlessOption.AddAlias("-HEADLESS");
rootCommand.AddOption(headlessOption);
```

### 3.2.2 提取公共初始化逻辑

将配置加载、目录检查、字体加载等逻辑提取为独立方法 `InitializeCore()`，供 WinForms 和无头模式共用。

```csharp
private static bool InitializeCore(string[] args, ParseResult result)
{
    // 1. 设置目录
    var exeDir = result.GetValueForOption(exeDirOption);
    if (exeDir != null) SetDirPaths(exeDir);
    else SetDirPaths(AssemblyData.WorkingDir);

    ExeName = Path.GetFileNameWithoutExtension(AssemblyData.ExeName);
    DebugMode = result.GetValueForOption(debugModeOption);
    var genLang = result.GetValueForOption(genLangOption);
    if (genLang) Lang.GenerateDefaultLangFile();

    // 2. 加载配置
    ConfigData.Instance.LoadConfig();
    JSONConfig.Load();
    Lang.LoadLanguageFiles();
    Lang.SetLanguage();

    // 3. 检查目录
    if ((!Config.AllowMultipleInstances) && AssemblyData.PrevInstance())
    {
        Console.Error.WriteLine(Lang.UI.MainWindow.MsgBox.InstaceExists.Text);
        return false;
    }
    if (!Directory.Exists(CsvDir))
    {
        Console.Error.WriteLine(Lang.UI.MainWindow.MsgBox.NoCsvFolder.Text);
        return false;
    }
    if (!Directory.Exists(ErbDir))
    {
        Console.Error.WriteLine(Lang.UI.MainWindow.MsgBox.NoErbFolder.Text);
        return false;
    }

    // 4. 加载字体
    if (Directory.Exists(FontDir))
    {
        foreach (string fontFile in Directory.GetFiles(FontDir, "*.ttf", SearchOption.AllDirectories))
            GlobalStatic.Pfc.AddFontFile(fontFile);
        foreach (string fontFile in Directory.GetFiles(FontDir, "*.otf", SearchOption.AllDirectories))
            GlobalStatic.Pfc.AddFontFile(fontFile);
    }

    // 5. Debug 模式
    if (DebugMode)
    {
        ConfigData.Instance.LoadDebugConfig();
        if (!Directory.Exists(DebugDir))
        {
            try { Directory.CreateDirectory(DebugDir); }
            catch
            {
                Console.Error.WriteLine(Lang.UI.MainWindow.MsgBox.FailedCreateDebugFolder.Text);
                return false;
            }
        }
    }

    // 6. Analysis 模式（无头模式下也支持）
    var fileArgs = result.GetValueForArgument(filesArg);
    if (fileArgs.Length > 0)
    {
        AnalysisMode = true;
        AnalysisFiles = [];
        foreach (var path in fileArgs)
        {
            if (!File.Exists(path) && !Directory.Exists(path))
            {
                Console.Error.WriteLine(Lang.UI.MainWindow.MsgBox.ArgPathNotExists.Text);
                return false;
            }
            // ... 原有分析文件收集逻辑
        }
    }

    return true;
}
```

### 3.2.3 无头模式启动流程

```csharp
static void Main(string[] args)
{
    // ... 编码、Culture 设置 ...

    var rootCommand = new RootCommand("Emuera");
    // ... 原有选项 ...
    var headlessOption = new Option<bool>(name: "--headless", description: "...");
    rootCommand.AddOption(headlessOption);
    // ...

    var result = rootCommand.Parse(args);
    var headless = result.GetValueForOption(headlessOption);

    if (!InitializeCore(args, result))
        return;

    if (headless)
    {
        RunHeadless(args);
    }
    else
    {
        RunWinForms(args, result);
    }
}
```

### 3.2.4 RunHeadless 方法

```csharp
private static void RunHeadless(string[] args)
{
    // 无头模式不需要 Application.SetCompatibleTextRenderingDefault
    // 不需要 ApplicationConfiguration.Initialize (WinForms 专用)

    var ui = new HeadlessConsole();
    var console = new EmueraConsole(ui);

    // 无头模式下需要手动启动主循环
    // EmueraConsole 内部已经通过 DetectAndRun 启动了协议线程
    // 但进程需要保持运行，等待协议线程结束或控制台关闭

    // 等待退出信号
    var protocol = console.AgentBridge; // 需要暴露 _agentBridge
    if (protocol != null)
    {
        // 主线程阻塞等待，或定期轮询
        while (!protocol.IsStopped()) // 需要添加公共访问方法
        {
            Thread.Sleep(100);
        }
    }
    else
    {
        // 协议未启动（如 stdin 是 NullStream），直接运行游戏逻辑
        // 这种情况下需要手动驱动游戏循环
        Console.Error.WriteLine("[headless] 未检测到输入管道，游戏逻辑需要手动驱动");
    }
}
```

### 3.2.5 EmueraConsole 新增无头模式构造函数

当前 `EmueraConsole` 构造函数签名：
```csharp
public EmueraConsole(MainWindow parent)
```

需要新增：
```csharp
public EmueraConsole(IConsoleUI ui)
{
    // 不设置 window 字段（保持 null）
    _uiAdapter = ui;
    // ... 其余初始化与现有构造函数相同
}
```

或者修改现有构造函数，接受 `IConsoleUI` 并在内部判断类型。但为最小修改，推荐新增构造函数。

**注意：** `EmueraConsole` 中有大量 `window.` 引用（Phase 4 处理），Phase 3 中无头模式构造函数传入 `HeadlessConsole` 后，这些引用在运行时可能抛 `NullReferenceException`。因此 Phase 3 的验收标准是**编译通过 + WinForms 模式正常**，无头模式真正可用需等 Phase 4。

---

## 3.3 修改步骤

### Step 3.3.1 添加 `--headless` 参数选项

**文件：** `Program.cs`

在 `genLangOption` 之后、`filesArg` 之前添加 `headlessOption`。

### Step 3.3.2 提取公共初始化方法

**文件：** `Program.cs`

将配置加载到 `ApplicationConfiguration.Initialize()` 之前的逻辑提取为 `InitializeCore`。注意保持原有逻辑不变，仅将 `Dialog.Show`/`MessageBox.Show` 改为 `Console.Error.WriteLine`。

### Step 3.3.3 添加无头模式启动方法

**文件：** `Program.cs`

添加 `RunHeadless` 和 `RunWinForms` 两个私有方法。

### Step 3.3.4 EmueraConsole 新增构造函数

**文件：** `EmueraConsole.cs`

添加接受 `IConsoleUI` 的构造函数，用于无头模式。

### Step 3.3.5 暴露 AgentBridge（可选）

**文件：** `EmueraConsole.cs`

如果 `RunHeadless` 需要访问 `_agentBridge`，可添加公共属性：
```csharp
public AgentProtocolBase AgentBridge => _agentBridge;
```

---

## 3.4 验收标准

```bash
# 1. 编译通过（NAudio 配置）
dotnet build Emuera/Emuera.csproj -c Debug-NAudio

# 2. WinForms 模式正常运行
Emuera.exe
# → 窗口正常出现，功能无异常

# 3. --headless 参数识别（Phase 3 仅验证编译和参数解析，不验证完整运行）
Emuera.exe --headless
# → 进程启动（可能因 EmueraConsole 中 window. 引用而崩溃，属预期，Phase 4 修复）
```

---

## 3.5 检查清单

- [ ] `Program.cs` 添加 `--headless` 参数选项
- [ ] `Program.cs` 提取 `InitializeCore` 公共初始化方法
- [ ] `Program.cs` 添加 `RunHeadless` 和 `RunWinForms` 方法
- [ ] `EmueraConsole.cs` 新增接受 `IConsoleUI` 的构造函数
- [ ] `EmueraConsole.cs` 暴露 `AgentBridge` 属性（如需要）
- [ ] `dotnet build -c Debug-NAudio` 通过
- [ ] WinForms 模式肉眼验证正常

---

## 3.6 风险与缓解

| 风险 | 影响 | 缓解 |
|------|------|------|
| `EmueraConsole` 中大量 `window.` 引用在无头模式下 NRE | 高 | Phase 3 仅要求编译通过 + WinForms 正常；无头模式可用性在 Phase 4 解决 |
| `ApplicationConfiguration.Initialize()` 被跳过 | 中 | 该方法为 WinForms 专用，无头模式不需要 |
| `Dialog.Show` 改为 `Console.Error` 后行为不一致 | 低 | 无头模式下用户通过控制台查看错误，行为合理 |

---

## 3.7 与后续 Phase 的衔接

- **Phase 4**：`EmueraConsole` 将 `window` 字段改为 `IConsoleUI`，消除所有 `window.` 引用，无头模式真正可用
- **Phase 5**：`--headless` 启动后可通过 `mcp_relay.py` 进行完整 JSONL 交互测试
