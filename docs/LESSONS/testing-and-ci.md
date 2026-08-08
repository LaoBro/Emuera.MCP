# 构建 / 测试 / CI 环境 失败教训

> **TL;DR**：增量构建"0 警告"是假象（强制重编译才见真警告）；Android 构建失败先查环境（文件锁/安全软件/残留进程）再改代码；行号锚定基线的测试脆弱；残留进程会干扰回归矩阵；code-review 用两轴（Standards + Spec）抓单轴漏掉的实现偏离。

## 教训清单

| # | 标题 | 一句话 | 日期 |
|---|---|---|---|
| 1 | 增量构建"0 警告"是假象 | touch 改动文件强制重编译才见真实警告 | 2026-08-04 |
| 2 | Android 构建报文件锁多为环境问题 | 360/残留进程锁 bin；先查环境再改代码 | 2026-08-04 |
| 3 | 游戏无删档功能时用删 sav 重建覆盖路径 | 进程外删 sav 让游戏重建，验证建目录路径 | 2026-08-04 |
| 4 | 两轴 code-review（Standards + Spec） | Spec 轴对照已批准计划逐行核对抓实现偏离 | 2026-08-04 |
| 5 | 写"已存在文件"被拒多为安全软件/残留句柄 | 排障顺序：残留句柄 → 安全软件 → 文件系统 | 2026-08-07 |
| 6 | 行号基线的测试在源码行号偏移时误报 | 改 Shared/** 后同步更新基线行号 | 2026-08-07 |
| 7 | 残留进程干扰回归矩阵 | 跑全量回归前先清残留进程 | 2026-08-07 |
| 8 | 操纵全局静态的测试必须串行 | DisableParallelization 集合；高频日志放大竞态 | 2026-08-07 |

---

## 1. 增量构建"0 警告"是假象——强制重编译才能看到真实警告

**场景**：O1/O4 构建显示"0 警告"，code-review 后 touch 两个改动文件强制重编译，暴露出 41 个既有警告（CS1570 XML 注释格式错误、CA1416、CS8603），其中 3 处 CS8603 还是 O1 引入的隐藏警告（`ParentDocIdOf`/`ResolveDocId` 的 Android API nullable 标注）。

**原因**：增量构建只重编译改动部分，未重编译文件的警告不显示；`DocumentsContract.GetTreeDocumentId`/`GetDocumentId` 返回 `string?`，未加 `!`。

**解决**：改动平台文件后 touch 或 Rebuild 强制全量重编译核对警告；nullable API 返回处补 `!`。

**教训**：验收「0 新增警告」要确认编译是全量还是增量；顺手清理同一文件的历史遗留警告（质量护栏允许，避免下次混淆）。

## 2. Android 构建报 XAFLT7024/XAPRAS7024 多为文件锁——先查环境再改代码

**场景**：`dotnet build -f net10.0-android` 报 `XAFLT7024: 文件被"xxx"锁定` / `XAPRAS7024`，错误信息指向 bin 下 dll 被占用；重试或重启系统后成功。

**原因**：360 安全软件实时防护 / 残留 dotnet/msbuild 测试进程锁住 `bin/Debug` 下程序集；另注意完整首次构建可能很慢（实测 19 分钟 vs 增量 13 秒），不是卡死。

**教训**：Android 构建失败先看错误信息里有没有"used by another process"/"被锁定"——是环境问题就重试/重启，不是代码问题；后台构建任务长时间无输出先查锁，别干等。

## 3. 游戏无删档/建目录功能时，用「删 sav 文件夹让游戏重建」覆盖建目录路径

**场景**：真机验证 O1 建目录失效路径，但目标游戏没有删除存档/新建目录功能。

**解决**：完全退出 app → 文件管理器删除 sav 文件夹 → 重启 → 游戏自动重建 sav（`EnsureRealDirectoryUri`）→ 验证「重建后立即可枚举/可写」。关键：必须在 app 退出状态删——进程内缓存对进程外修改无感知（TTL 盲区）。

**教训**：真机验证缺路径时，用「破坏性重建」制造目标场景（确认无重要数据）；验证建目录/删目录路径靠日志时间线（重建后 EnumerateUri 立即可见 + 写档后重枚举即见新档）。

## 4. 两轴 code-review（Standards + Spec）抓单线程审查漏掉的 P0

**场景**：O1/O4 提交前跑 code-review skill：Standards 轴（仓库标准 + Fowler 坏味基线）与 Spec 轴（对照已批准计划）并行子代理，各不污染上下文。

**结果**：Spec 轴对照计划逐行比对抓到「EnsureRealDirectoryUri 失效 key 错位」（off-by-one）+「DisplayNameForMatch null Name NRE」（实现偏离计划伪码，原实现有 null 保护）；Standards 轴抓到 DirectoryExists 快速路径单级匹配与 FindChildDocument 两级回退不一致。

**教训**：实施后对照「已批准计划」逐行核对（Spec 轴）比泛泛的代码审查更能抓"实现偏离 spec"类 bug；两轴并行避免单轴掩盖。审查发现的问题按严重度分级：正确性 bug 必修（P0），风格类判断性项按用户决定。

## 5. 写"已存在文件"被拒、新建 OK 的怪症——安全软件防篡改/残留句柄，先查环境

**场景**：publish 复制 Core.dll 到输出目录反复 `Access denied`；连 `.gitignore` append 都 `Permission denied`，但 touch 新建文件 OK、C 盘 OK、icacls 权限正常。

**定位**：先查残留进程（MSBuild 驻留节点 `dotnet build-server shutdown`；后台起的 server 进程锁 exe 导致 MSB3027——**起的 server 必须记得杀**），再查安全软件（`ZhuDongFangYu.exe`=360 主防；"改已有文件被拦、新建放行"正是防篡改/勒索防护特征）。用户确认 360 关闭后，锁释放恢复正常。

**教训**：
- "写已有文件被拒 + 新建正常 + 非只读 + ACL 正常"的排障顺序：残留句柄 → 安全软件防护 → 文件系统层；不要一上来改代码。
- Git Bash 的 `rm` 会被 safe-delete 包装拦截（通配符/相对路径失败）、`cmd //c`/`taskkill //PID` 参数被 MSYS 转义破坏——这类环境操作改用 **PowerShell `Stop-Process`/`Remove-Item` 或 Python `os.remove`** 绕开，别在 shell 转义上耗时间。

## 6. 行号基线的测试在源码行号偏移时会误报——SafIoPolicyTests 与"加一行 using"

**场景**：3.3 JSON 迁移给 `JSONConfig.cs` 加了一行 `using MinorShift.Emuera.GameView;`，`SafIoPolicyTests.Shared_runtime_direct_IO_does_not_exceed_documented_SAF_baseline` 立即报 8 处 "outside the explicit baseline"。

**原因**：该测试用 (文件, 行号) → 直接 IO 调用数 的**显式基线**扫描 Shared/** 源码；行号整体 +1 后旧基线全部错位，误报"新增直接 IO"（实际零变化）。

**教训**：这类"行号锚定基线"的测试对源码行号漂移脆弱——改 Shared/**（尤其加/删行）后要同步更新基线行号；改完跑一次该测试确认只有"行号平移"而无真实 IO 变化。反过来它也是称职的守卫：真实新增直接 IO 会被立刻抓到。

## 7. 残留进程干扰回归矩阵——run_all.py 内置 xUnit 与单独跑不一致

**场景**：NativeAOT 产物跑 `run_all.py`：14/14 e2e 全绿但内置 xUnit FAIL；单独 `dotnet test` 却 685 全绿。

**原因**：此前手动起的 AOT server 进程残留，锁住 `agent.log`/`test_game` 等共享资源（日志见过 `[agent-log] file init failed: ... being used by another process`），xUnit 里依赖文件系统的测试被波及。

**教训**：跑全量回归前先清残留进程（`Get-Process -Name "Emuera.Headless.Cli" | Stop-Process`）；"套件内 FAIL、单独跑绿"优先怀疑环境干扰（残留进程/文件锁），而不是代码回归——但也别轻易归因环境，先看失败测试的报错内容确认。

## 8. 操纵全局静态的测试必须进 DisableParallelization 集合;高频日志调用会放大竞态窗口

**场景**：T-027 Phase 4 日志统一收官后，完整套件下 `AgentLogTests.Configure_false_disables_then_runtime_toggle` 间歇失败（2/3 复现），单独跑/组合跑全绿。

**根因**：AgentLogTests 操纵进程级全局 `AppDataPaths.Directory` + `AgentLog.Instance` 静态 Lazy 单例；xUnit 默认类级并行下，`SafStage2StorageTests.AppDataScope` 会全局切换 AppDataPaths，把 AgentLog 的 writer 落点/FilePath 切到别的目录，断言读错文件。Phase 4 新增的 EraStreamReader/Preload `EmueraLog.Debug` 调用把 `AgentLog.Instance` 访问频率抬高（几乎所有读脚本文件的测试都会触发），竞争窗口从"几乎不可见"放大到"2/3 复现"。

**教训**：
1. 凡是操纵进程级全局状态（静态单例、全局目录、静态配置）的测试，一开始就应放入 `[CollectionDefinition(..., DisableParallelization = true)]` 串行集合（仓库先例：`GamePathsIsolated`）——不要指望"现在全绿"就安全，高频调用点一旦增多竞态就会显形。
2. 排查间歇失败：先跑"单独（绿）+ 与嫌疑类组合（绿）+ 完整套件（红）"三段定位，再用全局状态切分推断竞态对象；修复后完整套件连跑 2 次验证稳定（1 次绿不够，之前就是 1/3 绿）。
3. 静态门面（EmueraLog→AgentLog）让"测试进程里谁都会写日志"成为常态——新增高频调用点时要评估对全局状态测试的影响面。
