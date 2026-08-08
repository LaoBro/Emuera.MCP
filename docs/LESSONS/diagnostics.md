# 排查方法论 失败教训

> **TL;DR**：分层推进时"下一层失败"是进度不是回归（用阶段日志区分，显式作废过时假设）；"看起来卡死"先定性（忙转/阻塞 IO/等输入/会话已结束）；logcat 中文乱码 ≠ 内存路径损坏（信结构化验收字段）；被 catch 的异常默认不进 logcat（显式加日志）；回调层异常是黑洞（必须 try-catch + 打日志）。

## 教训清单

| # | 标题 | 一句话 | 日期 |
|---|---|---|---|
| 1 | 诊断推进会「揭开」下一层失败 | 下一层失败是进度；阶段日志区分 + 作废过时假设 | — |
| 2 | adb logcat 中文路径乱码 ≠ 路径损坏 | 信结构化验收字段（loaded/hasDVAR/SystemSaveInBinary） | — |
| 3 | Process.Initialize 返回 false 缺少异常诊断 | catch 里被 handleException 的异常不进 logcat；显式 Console.WriteLine | — |
| 4 | 运行时脚本 File.* 是全量迁移最后一块 | 初始化路径与运行时脚本路径两层，后者易忽略 | — |
| 5 | logcat 时间线 + DebugView 定位"假冻结" | 卡死先定性四类；lastTurnJson 是一手证据 | 2026-07-31 |

---

## 1. 诊断推进会「揭开」下一层失败，勿误判为回归

**场景**：修掉「只加载 1 个 ERH」后，游戏输出突然变短，出现大量「バイナリ型セーブ」必須警告，并 `ErhLoadHeaderFilesFalse`。

**原因**：以前几乎没跑 `#DIM`；全量 1692 条 dim 后才触发「未读到 SystemSaveInBinary」的真实错误。再修 config 后标题通；再点接受才暴露 FileStream 存档。

**教训**：分层推进时，下一层失败是**进度**，不是「越修越坏」。用阶段日志区分：`ERH loaded` / `Config SystemSaveInBinary` / `Initialize OK` / 运行时 SAVEDATA。过时 handoff（如「Initialize false 主因」）必须显式作废，避免下一会话退回旧假设。

## 2. adb logcat 中文路径乱码 ≠ 内存中路径损坏

**场景**：`firstKey=鍙ｄ伉\...KOJO_K4.ERH` 一类 mojibake；同时游戏已成功加载上千 ERB。

**原因**：logcat / PowerShell 控制台编码与进程内 UTF-16 字符串不一致。  
真正坏路径通常伴随 `OpenOnCache FAIL`、0 files、或 `hasDVAR` 类逻辑键错误。

**教训**：优先信**结构化验收字段**（`loaded=142/142`、`hasDVAR`、`SystemSaveInBinary`、相对路径警告是否为 `SYSTEM\...`），不要仅凭 adb 中文乱码判定文件名损坏。

## 3. `Process.Initialize` 返回 false 时缺少异常诊断

**场景**：`Process.Initialize` 返回 false，`ConsoleStateManager` 设 `ConsoleState.Error` 状态，游戏显示"错误：未知错误"。

**结果**：Exception 被 `Initialize` 的 catch 块捕获，调用 `handleException` 输出到游戏控制台（可能被清空或覆盖），开发者在 adb 日志中看不到异常详情。

**原因**：`Initialize` 的 catch 块：
```csharp
catch (Exception e)
{
    handleException(e, null!, true);
    console.PrintSystemLine(trsl.ErhLoadingError.Text);
    return false;
}
```
`handleException` 把异常信息写入 `console`（游戏输出缓冲区），但这个缓冲区后续可能被 `OutputLog`/`RefreshStrings` 清空或覆盖，不会输出到 `Console.WriteLine` 能到达的 logcat DOTNET tag。

**解决**：在 catch 块中加 `Console.WriteLine` 日志：
```csharp
catch (Exception e)
{
    Console.WriteLine($"[proc] Initialize exception: {e}");
    handleException(e, null!, true);
    ...
}
```

**教训**：任何在 MAUI/Android 游戏内部被 catch 的异常，默认不会出现在 `adb logcat` 可见的日志流中。`handleException` 把错误写入游戏输出缓冲区，但该缓冲区可能在后续操作中被覆盖。必须显式加 `Console.WriteLine` 才能让异常信息出现在 logcat DOTNET tag。

## 4. 运行时脚本 `File.*` 调用是全量迁移的最后一块

**场景**：`Creator.Method.cs`（10 处）和 `VariableEvaluator.cs`（26 处）中有大量 `File.Exists`/`Directory.Exists`/`File.ReadAllText`/`FileStream` 等原始 System.IO 调用。初始化路径的 `File.*` 调用已迁移，但运行时脚本路径（ERB `GETDIR`、`EXIST`、`SAVEDATA`、`LOADDATA` 等指令）还有大量未迁移。

**解决**：新增 `SafCompat` 辅助类，包装 `File.*`/`Directory.*` 调用：
```csharp
internal static bool FileExists(string path)
{
    if (!path.StartsWith("content://")) return File.Exists(path);
    return GamePaths.Current?.DirAccessor?.FileExists(path) ?? false;
}
```
在脚本函数中逐处替换。SAF 写路径（`SAVEDATA`/`FileStream`）暂不支持，静默跳过并打出警告。

**教训**：SAF 文件读取迁移不是一次性的——它分布在初始化路径和运行时脚本路径两个层次。初始化路径（Preload/EraStreamReader/Loader）是核心，运行时脚本路径（Creator.Method/VariableEvaluator）是 ERB 脚本执行时触发的第二层。后者同样重要但易被忽略。

## 5. 用 logcat 时间线 + DebugView 定位"看起来卡死"的假冻结

**场景（2026-07-31）**：自动存档后游戏冻结，怀疑是存档慢 / 死锁 / IO 挂起 / 会话退出。

**定位步骤**：
1. **logcat 时间线**：`[SaveTo] begin → defer buffer → flush OK → [SaveTo] ok` 拆出每段耗时，区分"写缓冲慢"vs"ContentResolver 慢"
2. **前端 DebugView 的 `lastTurnJson`**：直接看到最后一回合的 `state` + `error`——`error="script requested exit (QUIT/EXIT)"` + `state=Running` 直接指向 GameExitException 被误处理
3. **`[bridge] game loop completed normally`**：游戏循环正常返回 ≠ 没问题——查它是被 Stop / 通道关闭 / 超时结束的
4. **线程 TID 消失 + 进程 CPU≈0%**：async 任务挂起在 await（如等输入），不是死锁也不是忙转；`/proc/<pid>/task/<tid>` 不存在不代表进程死了

**教训**：游戏"卡死"先定性——忙转（CPU 高）/ 阻塞 IO（wchan=binder_thread_read）/ 等输入（TID 消失、CPU 低）/ 会话已结束前端没收到通知（game loop completed normally + 前端停在旧帧）。DebugView 的 `lastTurnJson` 是最快的一手证据，`[bridge]` / `SaveTo` / `OpenWrite` 日志是拆时间线的骨架。
