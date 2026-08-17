# Emuera 项目未使用代码扫描报告

> 扫描时间：2026-08-17 · 扫描方式：静态引用分析（文件级 import 图 + 导出符号引用计数 + 逻辑级死模式），并辅以 `vue-tsc --noEmit` 校验
> 结论概览：**前端可确认清理 2 处链路级死代码；store/导出层多为"多余暴露"或"仅测试消费"，需逐项决策；C# 侧不建议脚本清理**

---

## 一、扫描方法与可信度

| 手段 | 覆盖 | 可信度 |
| --- | --- | --- |
| 文件级 import 图（64 个源文件） | 整文件未引用 | 高 |
| 导出符号引用计数 | 函数/类型/常量未消费 | 中（需人工复核，已逐个验证） |
| store 成员消费分析 | Pinia return 暴露面 | 高（已校准内部引用） |
| 逻辑级死模式（ref 永不赋值等） | DirectoryBrowser 类问题 | 高 |
| `vue-tsc --noEmit` | 文件内未使用局部变量/参数 | 通过（EXIT 0，说明文件内无死变量） |

## 二、✅ A 级：确凿死代码（建议清理）

### A1. `lib/hitTest.ts` 整文件（含 `lib/__tests__/hitTest.test.ts`）
- **业务零引用**：全 src 无任何 `.ts/.vue` import 它，仅测试引用
- 内容：`ButtonRegion`、`hitTestButton`（按钮点击命中检测，UI 链路已不用）

### A2. DirectoryBrowser 目录浏览链路（Vue 侧整条是死的）
上一轮已确认 `DirectoryBrowser.vue` 挂载但 `showDirectoryBrowser` 永不为 true。涉及：

| 文件 | 位置 | 内容 |
| --- | --- | --- |
| `src/components/DirectoryBrowser.vue` | 整文件 | 目录浏览弹窗组件 |
| `src/components/MauiGameList.vue` | L42, L119-130, L302-306 | `showDirectoryBrowser` ref、`onDirectoryConfirm`/`onDirectoryCancel`、模板挂载块 |
| `src/lib/mauiBridge.ts` | `listDirectories` | 唯一消费者是 DirectoryBrowser |
| `src/stores/game.ts` | L56-62, L317, L1064-1071, L1398/1402/1403 | `DirectoryListResult` 类型、`directoryList` state、`setDirectoryList`/`clearDirectoryList` |
| `src/composables/useAppInit.ts` | L214-226 | `directoriesListed` 消息 handler |
| `src/stores/__tests__/gameLibrary.test.ts` | 相关用例 | 同步删除对应测试 |

> ⚠️ C# 侧（`BridgeHost.HandleListDirectories` L663、`DirectoryLister`）**建议暂不动**：Android SAF（ADR-0019）已替代此路径，但 C# 删除风险高、且 `AndroidJsBridge.cs` 注释仍引用旧方案，列为观察项。

### A3. `types/protocol.ts` 的 `CURRENT_PROTOCOL_VERSION`
- 定义文件内部都不使用（纯死导出）

## 三、⚠️ B 级：仅测试消费 / 疑为预留（需你决策）

| 位置 | 符号 | 现状 | 风险 |
| --- | --- | --- | --- |
| `lib/imageLayout.ts` | `readPixel`、`readSrcmPixelFromContext`、`PixelCoord` | 仅测试用；文件本身被 TerminalDisplay 用（`clickToImagePixel` 等 3 个函数在用） | 低 |
| `lib/inputRouting.ts` | `isAnyKeyInput`、`ButtonClickContext`、`TerminalClickContext` | 仅测试用；文件本身在用（`shouldSubmitButtonValue` 等 2 个函数） | 低 |
| `stores/connection.ts` | `applyControlEvent`、`lastControlEvent`、`controlState` | 仅测试消费；`controlState` 被业务写（`applyControlStatus`）但业务不读 | 中——疑为"旁观/接管"控制协议预留 |
| `types/protocol.ts` | `ControlEventType`、`ControllerKind` | 仅协议文件自用，业务不引用 | 中（与上一行同组） |
| `stores/ui.ts` | `detectPlatform`、`Platform` | 仅测试用；ui.ts 主体在用 | 低 |
| `stores/game.ts` | `GameEntry`、`LoadGameError`、`LoadGameErrorCode`、`readGameDirFromStorage`、`readLastPlayedGameFromStorage`、`readMainGameDirFromStorage`、`ServerState` | 仅测试用或多余导出 | 低（动 store 需同步测试） |
| `stores/connection.ts` | `computeBackoffMs`、`INITIAL_BACKOFF_MS`、`MAX_BACKOFF_MS` | 仅测试用 | 低 |

> 备注：`ensureSession` / `fetchSnapshot`（connection.ts）经校准**不是死代码**——被 `connect()` 内部调用，保留。

## 四、📋 C 级：多余导出（去掉 export 即可，低价值可选）

`PinchZoomOptions`（usePinchZoom.ts）、`VirtualScrollOptions`/`VirtualScrollReturn`（useVirtualScroll.ts）、`ReloadStatus`（loadingStatus.ts）——均为函数签名自用类型，外部无消费。

## 五、🐍 Python 侧

- **`emuera_gateway/session.py` 是孤儿模块**：全项目（含 tests/）无任何 import 它；`SessionWrapper` 类未被使用

## 六、❌ 不建议清理

1. **C# 侧脚本清理**：粗扫出 167 个"疑似孤儿类型"，绝大多数为误报——Emuera 是 fork 自上游的完整运行时，`Lang.cs` 是资源字符串表、`OperatorMethod` 系列经反射注册、大量类型靠名称/partial 关联。准确分析请用 Rider / VS 的"整个解决方案分析"，**不要脚本驱动删除**
2. **package.json 依赖**：pinia/vue/vite/vitest 等全部在用
3. **测试文件、`env.d.ts`、`main.ts`**：非死代码（Vitest 配置 / tsconfig / index.html 引用）

## 七、建议执行顺序（待审批）

1. **A1 + A2（前端）**：删除 hitTest 链路 + DirectoryBrowser 链路，同步改 2 个测试文件 → `vue-tsc --noEmit` + `vitest run` 验证
2. **A3**：删 `CURRENT_PROTOCOL_VERSION`
3. **Python**：删 `session.py`
4. **B 级**：默认建议**保留**（测试即行为规范；控制协议疑似预留），除非你确认无规划
5. **C 级 / C# 侧**：暂不动

> 所有修改均在获得你审批后执行；执行后我会重跑 typecheck + 测试确认无回归。

---

## 八、执行结果（2026-08-17 审批后执行）

| 项 | 状态 | 说明 |
| --- | --- | --- |
| A1 hitTest.ts + 测试 | ✅ 已删 | 整文件 + hitTest.test.ts |
| A2 DirectoryBrowser 链路 | ✅ 已删 | 组件 / MauiGameList 4 处 / mauiBridge.listDirectories / game.ts 4 处 / useAppInit handler / gameLibrary.test.ts 对应用例 + 相关注释同步更新 |
| A3 CURRENT_PROTOCOL_VERSION | ✅ 已删 | 含说明注释 |
| Python session.py | ✅ 已删 | 孤儿模块 |
| B 级低风险项 | ⚠️ 经核查无可删项 | `isAnyKeyInput`/`readPixel` 等全部在定义文件**内部**被使用（纯函数实现细节），删除会破坏功能；`detectPlatform`/`Platform` 是 ui store 初始化依赖；store 成员均为内部逻辑在用。全部保留 |
| C# 侧 | 未动 | 按计划：脚本清理误报率高，建议 IDE 方案级分析 |

**验证**：`vue-tsc --noEmit` EXIT 0；`vitest run` 21 文件 / 467 测试全部通过。
**变更规模**：4 文件删除 + 6 文件修改，净 -656 行。
**残留**：C# 侧 `BridgeHost.HandleListDirectories` / `DirectoryLister` 仍存在（Android SAF 已替代，列入观察，未删除）。
