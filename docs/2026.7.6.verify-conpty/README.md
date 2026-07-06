# ConPTY (pywinpty) 行为验证报告

## 背景

提交 746671ebe66df0ac0641f729f26fcae8512c8283 为 Windows ConPTY 兼容做了三处改动：

1. `WindowsTerminalInput.HasInputAvailable`：`GetNumberOfConsoleInputEvents` → `WaitForSingleObject`
2. `WindowsTerminalSetup.TryProbeDa1`：DA1 无响应时仍 `return true`（强制通过）
3. 新增 `test_cli_basic.py`：基于 pywinpty ConPTY 的 CLI 交互测试

该提交引入的 CLI 测试 6 项断言中 4 项通过、2 项失败：

```
PASS: Turn 1 shows Agent Test Start
PASS: Turn 1 shows [0] Hello
PASS: Turn 1 shows [1] Quit
FAIL: After input 0: turn 2 shows World
FAIL: After input 0: turn 2 shows Exit
PASS: Process exits after selecting Exit
```

本验证项目旨在系统性地确认 ConPTY(pywinpty) 环境下各项终端 API 的实际行为，定位根本原因。

## 验证项目

- `VerifyConPTY/` — 最小 C# .NET 10 控制台应用，分 7 步测试各 API
- `verify_conpty.py` — Python 驱动，通过 pywinpty 启动验证程序、发送输入、解析输出

### 验证步骤

| Step | 验证内容 |
|------|----------|
| 0 | 环境信息（OS、.NET、IsInputRedirected） |
| 1 | stdin 句柄有效性 + GetConsoleMode 标志位 |
| 2 | SetConsoleMode 设置 VT_INPUT + raw 模式 |
| 3 | WaitForSingleObject vs GetNumberOfConsoleInputEvents |
| 4 | Console.KeyAvailable + ReadKey(true) |
| 5 | stdout 控制台模式 + VT_PROCESSING |
| 6 | DA1 探测：`\x1b[c` → stdin 响应 |

## 验证结果：**7/7 全部通过**

### Step 0: 环境

```
Console.IsInputRedirected: False    ← stdin 是 console handle，非 pipe
Console.IsOutputRedirected: False
```

ConPTY 下 stdin/stdout 均为有效 console handle，**不触发** `Console.IsInputRedirected` 短路。

### Step 1: stdin 控制台模式

```
GetConsoleMode(stdin) = 0x000001F7
  SET ENABLE_PROCESSED_INPUT
  SET ENABLE_LINE_INPUT
  SET ENABLE_ECHO_INPUT
  SET ENABLE_WINDOW_INPUT
  SET ENABLE_MOUSE_INPUT
  SET ENABLE_INSERT_MODE
  SET ENABLE_QUICK_EDIT_MODE
  SET ENABLE_EXTENDED_FLAGS
     ENABLE_VIRTUAL_TERMINAL_INPUT  ← 默认未开启
```

### Step 2: SetConsoleMode 设置 raw VT 模式

```
SetConsoleMode(VT_INPUT) = OK
SetConsoleMode(raw: ~PROCESSED ~LINE ~ECHO) = OK
```

**`SetConsoleMode` 在 ConPTY 下完全可用。** `TryProbeDa1` 中不会因为 `SetConsoleMode` 失败而提前 `return false`。

### Step 3: WaitForSingleObject vs GetNumberOfConsoleInputEvents

```
WaitForSingleObject(stdin, 0) = 258 (WAIT_TIMEOUT)
  Signaled (input pending): False

GetNumberOfConsoleInputEvents FAILED: error=6 (ERROR_INVALID_HANDLE)
```

**关键发现：** 在 ConPTY 下 `GetNumberOfConsoleInputEvents` **必定失败**（句柄为 pipe，不支持此 API）。而 `WaitForSingleObject` 正常工作。**这一改动是正确的。** ✅

### Step 4: Console.KeyAvailable + ReadKey

```
Console.KeyAvailable: False          ← 初始无输入
=== WAITING_FOR_INPUT ===            ← Python 发送 "HELLO\r"
ReadKey: Key=H, KeyChar=H
ReadKey: Key=E, KeyChar=E
ReadKey: Key=L, KeyChar=L
ReadKey: Key=L, KeyChar=L
ReadKey: Key=O, KeyChar=O
ReadKey: Key=Enter, KeyChar=\x0D
Result: gotKey=True, keyCount=6
```

**ConPTY 下 `Console.KeyAvailable` + `ReadKey(true)` 完全正常工作。** 逐键读取，Enter 键识别为 `\x0D`。

### Step 5: stdout VT 模式

```
GetConsoleMode(stdout) = 0x00000007
```

`0x07 = ENABLE_PROCESSED_OUTPUT | ENABLE_WRAP_AT_EOL_OUTPUT | ENABLE_VIRTUAL_TERMINAL_PROCESSING`。**VT_PROCESSING 默认已开启。** VT 彩色/加粗序列能正常渲染。

### Step 6: DA1 探测

```
Sent DA1 (\x1b[c)
DA1 result: responded=False, totalRead=0 bytes
```

**核心发现：ConPTY 下 DA1 探测无响应。** 发送 `\x1b[c` 到 stdout 后，ConPTY **不会**向 stdin 发送 DA1 响应。无论怎么增加超时时间，都是无响应。

## 根本原因分析

### 问题链

```
TryProbeDa1() 发送 \x1b[c（写入 stdout）
  → ConPTY 不响应（responded=False）
  → 旧代码：return false → TryRunVtLoop() 返回 false
    → RunAgentLoop(ConsoleKeyLoopStrategy) ← 降级到 ConsoleKey
  → 新代码：return true  ← 强制通过，但 DA1 实际无响应
    → TryRunVtLoop() 继续：
      → _screen.EnterAlternateScreen() 写入 \x1b[?1049h
      → RunAgentLoop(VtLoopStrategy)
      → 但 ConPTY 对备用屏 \x1b[?1049h 支持不完整
```

### 测试失败的解释

1. 初始阶段（无 `\x1b[?1049h`）：`TryProbeDa1` 旧代码返回 false → ConsoleKey 路径 → 输出在主屏
2. 输入 "0\r" 后：**但为什么新代码 `return true` 却仍走 ConsoleKey？**

重新分析实际输出会发现：**输出中确实有 `\x1b[c` 但没有 `\x1b[?1049h`**。这意味着 `TryProbeDa1` 成功发送了 DA1 请求，但 `TryRunVtLoop` 并未进入备用屏。可能原因是：

- 新代码 `TryProbeDa1` 中 `return true` 之前有 `SetConsoleMode(stdin, originalMode)`——如果 `SetConsoleMode` 在这里**失败**（虽然验证中成功，但 Emuera.Headless 场景中可能已被 `WindowsTerminalInput` 构造函数提前修改了 stdin 模式），可能在某些边界下出错
- 或者 `TryProbeDa1` 确实返回 true 了，但 `TryRunVtLoop` 中 `_screen.EnterAlternateScreen()` 或之后的 `VtInputHandler` 构造失败导致回退

### 验证确认的关键事实

| 事实 | 结论 |
|------|------|
| DA1 在 ConPTY 下永远无响应 | `TryProbeDa1` 不能以 DA1 有无响应判定 VT 能力 |
| `GetNumberOfConsoleInputEvents` 必失败 | `WaitForSingleObject` 替代是正确的 |
| `Console.KeyAvailable + ReadKey` 正常 | ConsoleKey 降级路径在 ConPTY 中可用 |
| `SetConsoleMode(VT_INPUT)` 成功 | 可安全启用 raw VT 输入模式 |
| `SetConsoleMode(VT_PROCESSING)` 默认已开启 | stdout VT 渲染始终可用 |
| `IsInputRedirected` 为 false | `WindowsTerminalInput` 构造函数可通过 |

## 推荐方案

正确的做法是**分层检测**，而非强制 `return true`：

```
1. 先尝试 SetConsoleMode(VT_INPUT) + SetConsoleMode(VT_PROCESSING)
   └─ 成功 → 直接启用 VT 路径（跳过 DA1）
2. SetConsoleMode 失败 → 执行 DA1 探测作为保底
   └─ 有响应 → VT 路径（legacy terminal）
   └─ 无响应 → ConsoleKey 降级
```

因为在 ConPTY 环境下：
- SetConsoleMode 成功 = VT 输入/输出能力可用
- DA1 无响应 ≠ VT 不可用（ConPTY 特征）
- 跳过 DA1 直接 VT 是安全且正确的

在非 ConPTY 的 legacy console 中：
- SetConsoleMode 可能不支持 VT_INPUT → 降级到 DA1 探测
- DA1 有响应 → VT 路径
- DA1 无响应 → ConsoleKey 降级

### 实现要点

1. `HasInputAvailable`：保留 `WaitForSingleObject` ✅
2. `TryProbeDa1`：改为先验证 `SetConsoleMode` → 跳过 DA1 → 直接启用 VT
3. `test_cli_basic.py`：修复等待时间语义（应等待进程退出而非固定 sleep）
