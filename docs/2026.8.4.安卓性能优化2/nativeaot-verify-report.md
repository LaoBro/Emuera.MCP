# NativeAOT 验证报告（3.2 + 3.3 核心）

> 日期：2026.8.7
> 分支：`nativeaot-verify`（验证分支，2026.8.7 已 squash 为 2 提交并入主分支 `Headless`：`63adc78` feat + `bf75ddf` docs）
> 状态：✅ **3.2 验收核心达成**（ILC 全链路打通 + 回归矩阵全绿 + 体积/耗时记录在案）；3.3 JSON 源生成器迁移提前完成 + DataTable 子集验证通过
> 关联：`android-perf-2.md` 阶段 1

---

## 1. 结论

**引擎 NativeAOT（win-x64 先行）验证通过**：`Emuera.Headless.Cli` 以 NativeAOT 发布成功（单文件 exe），跑完整回归矩阵（xUnit 685/685 + `run_all.py` 14/14）**输出与托管/JIT 基线一致**。Server（Kestrel）端点在 AOT 下全链路可用。

**过程中提前完成 3.3 的 System.Text.Json 源生成器迁移**——它是 3.2 回归矩阵变绿的硬前置（JSONConfig.Load 反射反序列化在 AOT 下直接抛 `JsonSerializerIsReflectionDisabled`，server 启动即崩）。

---

## 2. 工具链路径（本机无 VS C++ workload 的完整方案，写死避免重查）

| 组件 | 本机现状 | 方案 |
|---|---|---|
| ILC 编译器 | NuGet 自带（microsoft.dotnet.ilcompiler 10.0.10） | 无需额外安装 |
| 链接器 | **无** VS `link.exe`（vswhere 认不出 VS 18 组件） | `IlcUseEnvironmentalTools=true` + `CppLinker=` MSVC `link.exe` 绝对路径 |
| MSVC CRT | `VC/Tools/MSVC/14.51.36231/lib/onecore/x64/`（libcmt/libcpmt/oldnames 在） | `LIB` 环境变量含 onecore/x64 |
| Windows SDK | 已装 10.0.26100（本次验证用 winget 安装） | `LIB` 含 `um/x64` + `ucrt/x64` |

发布命令（可复现）：
```bash
export LIB="C:\Program Files\Microsoft Visual Studio\18\Community\VC\Tools\MSVC\14.51.36231\lib\onecore\x64;C:\Program Files (x86)\Windows Kits\10\Lib\10.0.26100.0\um\x64;C:\Program Files (x86)\Windows Kits\10\Lib\10.0.26100.0\ucrt\x64"
dotnet publish Emuera.Headless.Cli -c Release -r win-x64 \
  -p:IlcUseEnvironmentalTools=true \
  -p:CppLinker="...\MSVC\14.51.36231\bin\Hostx64\x64\link.exe" \
  -p:CppLibCreator="...\lib.exe" -p:TreatWarningsAsErrors=false
```
> `TreatWarningsAsErrors=false` 仅为验证期让 ILC 分析警告降级放行；**ILC 警告清零是 3.3 收尾目标**（见 §5）。
> 另：lld-link 方案不可行（不认 `/NOEXP`、缺 sourcelink 语义）；完整 MSVC `link.exe` 是正解。

---

## 3. 回归矩阵（NativeAOT 产物 vs 托管基线）

| 套件 | 结果 | 说明 |
|---|---|---|
| xUnit（Emuera.Headless.Tests） | ✅ 685/685 | wire 契约含 relaxed escaping 断言（Server 端点测试） |
| run_all.py（e2e） | ✅ 14/14 | JSONL+buttons / server single-session / TINPUT timeout / fatal turn / I-11 survival / SELECTCASE / CLI basic / CLI scroll / clearline / VT-only / WebSocket / GET /snapshot / load-game（46 用例）/ /assets |
| 前端协议兼容 | ✅（e2e 覆盖） | JSONL/WS/snapshot 消费链路一致 |

**行为等价要点**：CLI 直跑模式、server 全端点（含错误响应 400/404/500 形状）、WebSocket 传输、静态资源通道，AOT 与托管输出一致。

---

## 4. 体积与编译耗时

| 项 | 值 |
|---|---|
| 最终产物 `Emuera.Headless.Cli.exe` | **26.2 MB**（单文件，含 Core+Server+Kestrel+运行时） |
| 调试符号 pdb | 117 MB（发布可剥离） |
| 发布耗时（增量） | ~52s（首次全量 ILC ~2-4min） |
| 泛型结构体膨胀 | 体积已含（`GlobalInt1dWrapper` 等值类型包装器为已知膨胀源；未单独拆分量化——android-arm64 阶段再看） |

---

## 5. ILC 警告清单（3.2 要求"记录并清零/逐条豁免"）

### 5.1 已消除（本次迁移）
- ❌ System.Text.Json 反射序列化/反序列化（Core 8 处 + Server 匿名类型）→ **源生成器迁移完成**（`EmueraJsonContext` / `ServerJsonContext`），运行期零反射

### 5.2 剩余（登记，3.3 收尾逐项处理）

| 来源 | 位置 | 警告 | 处理 |
|---|---|---|---|
| `Serialize<TurnRecord>(TurnRecord, JsonSerializerOptions)` | `AgentJsonlProtocol.cs:79/93`（L76/90 调用点） | IL2026/IL3050 | 运行时安全（`TurnJsonOptions.TypeInfoResolver` 已指向 context）；静态分析器只见重载签名——**收尾改显式 `Serialize(turn, EmueraJsonContext.Default.TurnRecord)` 消除** |
| `System.Data.DataTable` 反射 | `EraBinaryDataWriter.cs:181/184`、`EraBinaryDataReader.cs:274/276`、`Creator.Method.cs:1389/1559/1694/1695/1732/1735/1758/1762` | IL2026/IL3050/IL2072 | 3.3 子集验证（建表/读值/改值实际用到的 API）后按结论处理 |
| PluginManager 动态加载 | `PluginManager.cs:292/293/305` | IL2026/IL2072 | **豁免**：Android/SAF 路径直接禁用（抛 ExeEE）；桌面路径无 Plugins 目录即 return（test_game 已验证） |
| `Results.Json` 反射重载 | Server `WarningsNotAsErrors` 豁免 | IL2026/IL3050 | **豁免（理由已注释）**：运行时经 `ConfigureHttpJsonOptions` resolver chain（ServerJsonContext）解析，无反射；TypeInfo 显式注入会破坏 wire（转义 `'` → `\u0027`），故保留 options 重载 + resolver |

---

## 6. 关键改造记录（3.3 提前完成，wire 契约不变）

1. **`EmueraJsonContext`（Core）**：源生成上下文，注册 TurnRecord/DisplayDiff/DisplaySnapshot/DisplayLine/DisplayEntry/PrintSegment/SegmentImage/SegmentCrop/SegmentShape/BgImageState/ButtonRef/JsonlCommand/JSONConfigData + LineOp/TurnOp 全部多态叶子（converter 按运行时类型解析 TypeInfo 所需）。
2. **`ServerJsonContext`（Server）**：注册 JsonObject/HttpInput/LoadGameRequest。
3. **resolver 注入**：`TurnJsonOptions`/`DisplayState.JsonOpts` 加 `TypeInfoResolver`；Server `ConfigureHttpJsonOptions` 把 context 插到 resolver chain 首——`Results.Json(JsonObject)` 在 AOT 下可用且 **Encoder 保持 HttpJsonOptions 默认 UnsafeRelaxedJsonEscaping**（wire 逐字节一致）。
4. **匿名类型响应 → JsonObject**（KestrelGameServer/Session 全部响应）。
5. **minimal API 请求委托生成器**：`Server.csproj` 加 `EnableRequestDelegateGenerator=true`（**库项目必须显式开启**——否则 AOT 下 RequestDelegateFactory 反射路径，所有端点 500）+ `IsAotCompatible=true`。

---

## 7. 遗留与后续（下一步）

- [x] **android-arm64 验证（3.2 后半，2026.8.7）**：NDK 已装（27.2.12479018），但 **.NET SDK NativeAOT 对 android 目标无官方支持路径**——三证：
  1. `Microsoft.NETCoreSdk.BundledVersions.props` 的 `KnownILCompilerPack`（net10.0）`ILCompilerRuntimeIdentifiers` **不含 android**；
  2. ILC `Publish.targets` 显式报 `Cross-OS native compilation is not supported`（Windows host 上 `_targetOS != win` 一律拒绝；`DisableUnsupportedError` 绕过后续仍缺 target 侧 ILC 包）；
  3. `runtime.android-arm64.Microsoft.DotNet.ILCompiler` 在 nuget.org **不存在**（BlobNotFound）——target 编译输入（sdk dll）无从获取；而 `Microsoft.NETCore.App.Runtime.NativeAOT.android-arm64` 存在但 SDK 不解析（restore 后 assets 里无 android pack）。
  → **结论**：纯 SDK NativeAOT（net10.0 + `-r android-arm64`）在当前 .NET 10 不可行；Android NativeAOT 的官方路径只有 **MAUI/Xamarin.Android 管道**（`Microsoft.Android.Runtime.NativeAOT.36` pack，本机已随 workload 就位）——即 3.4 壳层门控项（.NET 11 GA 后实验分支）。
  → **已保留的基础**：`Emuera.Headless.Cli` 的 android RID 条件编译（排除 Server/Kestrel 引用 + `ANDROID_NO_SERVER` 符号 + `obj-aot/**` glob 排除修复 CS0579）——真实 Android 形态本就不含 Kestrel，将来壳层集成引擎时直接可用。
- [ ] **ILC 警告清零收尾**（§5.2）：AgentJsonlProtocol 显式 context 调用（已完成）+ DataTable 子集验证（已完成，见 `tests/test_datatable_aot.py`）
- [ ] **壳层门控（3.4）**：维持 .NET 11 GA 后实验分支结论（见 `android-perf-2-net11-eval.md`）
- [ ] **测试遗留（与本次无关，stash 证明为既有问题）**：`test_snapshot.py` 稳定 1 failed（`post-input turn diff contains 'You entered: 0'`——turn2 diff 序列化为 `{"lineOps":[{"type":"append"}]}`，`AppendLinesOp.newLines` 为 null；托管/AOT 均复现；xUnit 685 全绿但未覆盖该路径）。待查：test_game input 回合 diff 生成为何 newLines 为空。
- [ ] 验证期产物 `publish/nativeaot-win-x64/` 与 `bin-aot/obj-aot` 已 gitignore，不入库

---

## 8. 参考
- 主计划：`android-perf-2.md` 阶段 1（3.2/3.3）
- 环境结论：`.gitignore`（bin-aot/obj-aot）+ `Emuera.Headless.Cli/Directory.Build.props`（Base*OutputPath 重定向，规避本机 bin 目录历史文件锁）
