# v2.0 验证计划

## 版本信息

- 关联规格：`docs/2026.6.23.vt-input/spec-v2.0.md`
- 基线：`docs/2026.6.16.mouse-input/validation-results.md`、`docs/2026.6.16.mouse-input/coordinate-validation-results.md`
- 目标：验证 v2.0 VT 输入路径在 Windows Terminal + conhost 下的可用性

## 验证环境

| 项 | 要求 |
|---|---|
| OS | Windows |
| 终端 | Windows Terminal、conhost（PowerShell 5.1） |
| 输入模式 | 交互式（`Console.IsInputRedirected == false`） |
| .NET | net10.0 |

## 步骤 1 — DA1 探测验证

### 目标

验证 DA1 查询能正确识别 VT 能力，且 200ms 超时合理。

### 用例

| 用例 | 终端 | 预期 |
|---|---|---|
| 1a | Windows Terminal | 200ms 内收到 `ESC[?...c` 响应，判定 VT 可用 |
| 1b | conhost（PowerShell 5.1） | 200ms 内收到响应，判定 VT 可用 |
| 1c | pipe / redirected stdin | 不进入探测，直接走 `RunPipeCliLoop` |

### 验证方法

```powershell
$env:EMUERA_MOUSE_LOG='1'
dotnet exec D:/LaoBro/Emuera.MCP/Emuera.Headless/bin/Debug/net10.0/Emuera.Headless.dll --ExeDir D:/LaoBro/Emuera.MCP/test_game --protocol cli
```

检查 `debug/mouse.log` 中是否有 `[vt-input] DA1 response received` 日志。

## 步骤 2 — 备用屏生命周期验证

### 目标

验证备用屏进入/退出正确，退出后主屏恢复。

### 用例

| 用例 | 操作 | 预期 |
|---|---|---|
| 2a | 启动程序 | 终端清屏，进入备用屏，主屏内容暂存 |
| 2b | 正常退出（游戏结束） | 退出备用屏，主屏恢复进入前内容 |
| 2c | Ctrl+C 退出 | 退出备用屏，主屏恢复 |
| 2d | 异常崩溃 | `UnhandledException` 钩子触发，退出备用屏 |

### 验证方法

- 启动前在终端执行 `echo "before"` 留下标记；
- 启动程序，确认 `before` 消失（进入备用屏）；
- 退出后确认 `before` 恢复。

## 步骤 3 — SGR 鼠标输入验证

### 目标

验证 raw stdin + VT 解析能正确读取 SGR mouse 事件。

### 用例

| 用例 | 操作 | 预期日志 |
|---|---|---|
| 3a | 点击窗口左上角 | `[mouse] left-down row=0 col=0` |
| 3b | 点击窗口右下角 | `[mouse] left-down row={H-1} col={W-1}` |
| 3c | 鼠标移动 | 无日志（`1000h` 不报告移动） |
| 3d | 鼠标释放 | 无日志（忽略释放事件） |
| 3e | 滚轮 | 无日志（忽略滚轮） |

### 验证方法

```powershell
$env:EMUERA_MOUSE_LOG='1'
dotnet exec D:/LaoBro/Emuera.MCP/Emuera.Headless/bin/Debug/net10.0/Emuera.Headless.dll --ExeDir D:/LaoBro/Emuera.MCP/test_game --protocol cli
```

检查 `debug/mouse.log`。

## 步骤 4 — 键盘输入验证

### 目标

验证 VT 解析的键盘事件能正确接入 `ProcessKey`。

### 用例

| 用例 | 按键 | 预期 |
|---|---|---|
| 4a | `Enter` | 提交当前输入 |
| 4b | `Backspace` | 删除最后一个字符 |
| 4c | `Escape` | 清空输入缓冲区 |
| 4d | `↑` / `↓` | 切换按钮选择 |
| 4e | 数字键 `0-9` | 输入字符 |
| 4f | 字母键 | 输入字符 |
| 4g | `Ctrl+C` | 触发退出，终端恢复 |
| 4h | 中文字符（IME 提交） | 输入中文字符 |

### 验证方法

交互式测试，观察游戏响应。

## 步骤 5 — 按钮点击命中验证

### 目标

验证 viewport 坐标命中正确，与 v1.4 行为一致。

### 用例

| 用例 | 操作 | 预期 |
|---|---|---|
| 5a | 点击 `[0] Hello` | 提交 `0`，显示 `You entered: 0` |
| 5b | 点击 `[1] Quit` | 提交 `1` |
| 5c | 点击按钮行空白（buttonMode） | 不提交，不改变状态 |
| 5d | 点击按钮行空白（非 buttonMode） | 提交空字符串 |
| 5e | 点击非按钮行 | 不提交 |
| 5f | 多按钮场景，点击不同按钮 | 分别提交对应输入 |

### 验证方法

```powershell
$env:EMUERA_MOUSE_LOG='1'
dotnet exec D:/LaoBro/Emuera.MCP/Emuera.Headless/bin/Debug/net10.0/Emuera.Headless.dll --ExeDir D:/LaoBro/Emuera.MCP/test_game --protocol cli
```

检查 `debug/mouse.log` 中的 `[region]` 和 `[mouse] hit/miss` 日志。

## 步骤 6 — 渲染验证

### 目标

验证备用屏下绝对定位渲染正确。

### 用例

| 用例 | 操作 | 预期 |
|---|---|---|
| 6a | 启动后初始渲染 | 按钮行正确显示在 viewport 底部 |
| 6b | `FullRefresh` 触发 | 全屏重绘，无错位 |
| 6c | 倒计时行更新 | 倒计时行原地更新，不推动其他行 |
| 6d | 按钮 prompt 更新 | prompt 行原地更新 |
| 6e | 终端 resize | 自动 `FullRefresh`，布局适应新尺寸 |

### 验证方法

交互式测试，视觉确认。

## 步骤 7 — 降级路径验证

### 目标

验证 DA1 探测失败时正确降级到纯键盘模式。

### 用例

| 用例 | 环境 | 预期 |
|---|---|---|
| 7a | 不支持 VT 的终端（模拟） | 不进入备用屏，不启用 SGR mouse，走 `Console.ReadKey` |
| 7b | 降级模式下键盘输入 | `↑/↓ + Enter` 可用 |
| 7c | 降级模式下鼠标点击 | 无反应（预期） |

### 验证方法

- 用 env var 强制 DA1 失败（如 `EMUERA_FORCE_NO_VT=1`，仅调试用）；
- 或在 pipe 模式下验证（pipe 模式不进入 VT 路径）。

## 步骤 8 — 输入超时验证

### 目标

验证 raw stdin 轮询模型下输入超时正确触发。

### 用例

| 用例 | 操作 | 预期 |
|---|---|---|
| 8a | 等待超时 | 显示超时消息，提交超时 |
| 8b | 超时前输入 | 正常处理输入，不触发超时 |
| 8c | 倒计时显示 | 倒计时行正确更新 |

### 验证方法

使用 `test_game` 中带超时的输入请求，观察倒计时与超时行为。

## 步骤 9 — 异常退出验证

### 目标

验证异常退出时终端恢复。

### 用例

| 用例 | 操作 | 预期 |
|---|---|---|
| 9a | Ctrl+C | 终端恢复，主屏内容回来 |
| 9b | 游戏崩溃 | `UnhandledException` 钩子触发，终端恢复 |
| 9c | 进程被 Kill | `ProcessExit` 钩子触发（尽力恢复） |

### 验证方法

- 启动程序后按 Ctrl+C，检查终端状态；
- 用 `kill -9` 或任务管理器结束进程，检查终端是否残留备用屏状态（可能无法完全恢复，记录为限制）。

## 步骤 10 — pipe 模式回归验证

### 目标

验证 pipe 模式不受 v2.0 改动影响。

### 用例

| 用例 | 操作 | 预期 |
|---|---|---|
| 10a | pipe 输入 | 正常处理，不启用 VT |
| 10b | pipe 模式退出 | 终端无残留状态 |

### 验证方法

```powershell
echo "0`n1" | dotnet exec D:/LaoBro/Emuera.MCP/Emuera.Headless/bin/Debug/net10.0/Emuera.Headless.dll --ExeDir D:/LaoBro/Emuera.MCP/test_game --protocol cli
```

## 验证通过标准

所有步骤在 Windows Terminal + conhost 下通过，且：

- 鼠标点击按钮正确提交；
- 键盘 fallback 可用；
- 终端恢复正确（正常退出 + Ctrl+C）；
- pipe 模式回归通过；
- 降级路径可用。

## 已知不验证项

- Unix/Linux/macOS 路径（未实现）；
- VS Code Integrated Terminal（未单独验证）；
- 全角字符按钮列范围（未系统实测）；
- IME 组合态可见回显（不实现）；
- 进程被 Kill 后的终端恢复（尽力恢复，不保证）。
