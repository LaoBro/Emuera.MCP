# TODO — 跨平台前置重构后续工作

## P1-3 Turn 协议版本化与 Schema 定义（已完成）

详见 [架构评估报告.md](./架构评估报告.md) 任务 3、[ADR-0001](../../adr/0001-turn-protocol-versioning.md)。

完成范围：
- `TurnRecord` + `ButtonEntry` record 落地
- `protocolVersion: 1` 仅出现在 initial turn
- wire format 字节级不变（除 initial turn 多 `protocolVersion` 字段）
- `test_jsonl.py` 扩展断言 `protocolVersion`

## 已转 PRD：fatal turn 结构断言

**已转 [PRD-T4-FatalTurn测试.md](./PRD-T4-FatalTurn测试.md)。**

原 TODO 内容保留如下作为历史记录：

### 背景

P1-3 的 grilling 决策 7 明确：fatal 路径内联构造 `TurnRecord`，wire format
保持 `text=""` / `buttons=[]` / `error=ex.Message` 不变。但 [test_jsonl.py](../../../tests/test_jsonl.py)
当前不覆盖 fatal 场景——`test_game/erb/TEST.ERB` 不会抛异常。

### TODO

新增 fatal turn 结构断言测试。建议参照 [test_tinput_timeout.py](../../../tests/test_tinput_timeout.py)
的模式：

1. 复制 `test_game` 到临时目录。
2. 覆盖 `erb/TEST.ERB` 在特定路径抛出异常（如除零、未定义函数调用）。
3. 启动 server，`POST /session` → `GET /turn`（initial）→ `POST /input` 推进到
   抛异常点 → `GET /turn` 取 fatal turn。
4. 断言 fatal turn 结构：
   - `state` 不是 `WaitInput`（应为 `Error` 或游戏崩溃后的稳态）
   - `text == ""`
   - `buttons == []`
   - `error` 字段存在且为字符串
   - `protocolVersion` 字段不存在（fatal turn 不是 initial turn）

### 验收标准

- 测试独立可执行：`python tests/test_fatal_turn.py --binary <path> --game-dir test_game`
- 加入 [run_all.py](../../../tests/run_all.py) 回归套件
- 更新 [tests/README.md](../../../tests/README.md) 的测试清单

## 已排除：富 turn 升级整体推迟

**来源**：grilling 阶段调研发现 turn schema 当前不满足前端渲染需求，提出 R-05
~ R-09 五项升级。但用户决定本次先完成 [PRD-T3](./PRD-T3-Turn协议版本化.md) 与
[PRD-T4](./PRD-T4-FatalTurn测试.md)，富 turn 升级整体推迟到完成后再讨论。

下面把所有升级项（包括已 grilling 收敛的"范围 B 采集遗漏类"和已排除的"非采集
遗漏类"）整体记录，作为未来 PRD-T5 的素材。

### 范围 B：采集遗漏类（数据已存在，turn 字段扩展）

这三项在 grilling 阶段确认属于"采集遗漏"性质——数据在内部已存在，只需扩展
`CollectVisibleButtons` / 新增 turn 字段。

#### R-05 富文本 turn（segment 级样式）

**调研结论**：
- [StringStyle](../../../Emuera.Headless/Shared/UI\Game/StringStyle.cs#L10) 是
  `struct`，5 个字段：`Color` / `ButtonColor` / `ColorChanged` / `FontStyle`
  （bold/italic/regular 枚举）/ `Fontname`。**没有 size 字段**——字号由
  `Config.FontSize` 全局驱动。
- 存储粒度是 **per-segment** 而非 per-line：链路是
  `ConsoleDisplayLine.buttons[] (ConsoleButtonString)` →
  `ConsoleButtonString.strArray[] (AConsoleDisplayNode)` →
  `ConsoleStyledString.StringStyle`。一行可有多个 button、每个 button 可有多个
  不同 style 的段。
- 用户"行样式缺失"成立但描述有偏差——真实存储粒度是 per-segment。

**未来方向**：
- `BuildTurn()` 新增 `lines[]` 字段，每行包含 `segments[]`（text + style.color
  + style.bold + style.italic）。
- `text` 字段保留作纯文本降级。
- turn 版本号升到 `protocolVersion: 2`。

#### R-06 按钮区域数据

**调研结论**：
- [ButtonRegionTracker](../../../Emuera.Headless/Agent/ButtonRegionTracker.cs)
  已计算坐标，`Region` record 含 `Row` / `Left` / `Right` / `Button` /
  `Generation`——是字符列宽（East-Asian-Width 感知），**非像素**。
- 用途仅 `HitTest(row, col)` 一个消费者，供 CLI 模式 `ButtonSelectionMode`
  做键盘/鼠标命中测试。**完全没有暴露给 turn JSON**——server/JSONL 路径不读
  `_regions`。
- `ConsoleButtonString` 也持有 `PointX` / `Width` 字段。

**未来方向**：
- `buttons[]` 扩展 `row` / `col` / `width` 字段，复用 `ButtonRegionTracker`
  的坐标计算逻辑。
- 前端可渲染可点击按钮。

#### 布局对齐信息

**调研结论**：
- [ConsoleDisplayLine.align](../../../Emuera.Headless/Shared/UI/Game/ConsoleDisplayLine.cs#L16-L21)
  字段持 `DisplayLineAlignment { LEFT=0, CENTER=1, RIGHT=2 }`。
- `PrintC` / `PrintButtonC` 等会设置对齐。一旦 `aligned=true` 就冻结。
- turn 完全不输出 `align`——`CollectVisibleButtons` 只读 `btn.IsButton` /
  `btn.Generation` / `btn.ToString()` / `btn.Input/Inputs`，不读 `line.Align`。

**未来方向**：
- `lines[]` 字段中每个 line 带 `align` 字段（`"left"` / `"center"` / `"right"`）。
- 前端可还原居中/右对齐排版。

### 范围 C：非采集遗漏类（已排除，独立立项）

下列三项因性质不同被排除，作为后续独立工作：

#### R-07 操作序列 turn（CLEARLINE / REUSELASTLINE / REPLACE）

**性质**：新设计，非"加字段"。

**调研结论**：
- [CLEARLINE](../../../Emuera.Headless/Shared/Runtime/Script/Statements/Instraction.Child.cs#L679-L693)
  通过 `ConsolePrintManager.DeleteLine` 直接 `RemoveAt` 从 list 末尾硬删——
  **无 tombstone / soft-delete / 标记位**。
- `ReuseLastLine` 在本 headless 代码库中**零匹配**——这个原语根本不存在。
- 当前 turn 输出形态是"增量文本 + 当前可见窗口快照"混合，不是操作序列。

**为何排除**：要做"操作序列"必须新建事件流（在 `ConsolePrintManager.DeleteLine`
里 emit event、`BuildTurn` 收集 pending ops），是架构级变更。应单独立项，
不与"采集遗漏"富 turn 混在一起。

**未来立项建议**：
1. 先定义领域语义——"操作"的边界（CLEARLINE 是删 N 行、REUSELASTLINE 是否需要
   新增 ERB 指令、REPLACE 替换哪一行）。
2. 设计 `ops[]` 字段的 schema——`{op: "clearline", n: 3}` / `{op: "replace", row: 5, ...}`。
3. 在 `ConsolePrintManager` 引入 op emit 点，`BuildTurn` 收集 pending ops。
4. 验证与 `text` 增量字段的关系——保留 `text` 作为降级，还是用 `ops[]` 完全替代。

#### R-09 图片元数据 / Sprite 暴露

**性质**：暴露空实现，会误导前端。

**调研结论**：
- `ConsoleImagePart` 结构上能携带图片资源名（`ResourceName` / `ButtonResourceName` /
  `MappingGraphName` + `AltText`）。
- 但 Headless 下 [AppContents.GetSprite](../../../Emuera.Headless/Shared/UI/Game/Image/AppContents.cs#L25)
  恒返回 `null`，`ConsoleImagePart` 构造时 `cImage == null` 进入 fallback 分支
  把 `Text = AltText`（即原始 `<img src='...' srcb='...' .../>` HTML 标签字符串）。
- [HeadlessImageContext.DrawImage](../../../Emuera.Headless/Headless/HeadlessImageContext.cs#L8-L14)
  是 `{ }` 空操作。
- [EmueraConsole.AddBackgroundImage](../../../Emuera.Headless/UI/Game/EmueraConsole.cs#L395)
  是 `{ }` 空 stub（SETBGIMAGE）。

**为何排除**：turn 暴露的图片字段只能是 `<img .../>` 文本占位——不是富数据。
前端拿到这个字段会以为"有图片可用"，实际只有 HTML 字符串。应该等 Headless
真正实现 sprite 加载后再加 turn 字段。

**未来立项建议**：
1. 先实现 Headless 的 sprite 加载（[AppContents.GetSprite](../../../Emuera.Headless/Shared/UI/Game/Image/AppContents.cs#L25)
   的非空实现）——这是前置工作。
2. 再决定 turn 字段形态：暴露 `ConsoleImagePart` 的 `ResourceName` + 期望尺寸，
   还是暴露已加载的 sprite 二进制（通过 HTTP 端点 `/resources/<name>`）。
3. 与 HTML_PRINT 语义对齐——HTML_PRINT 输出的 HTML 字符串里嵌入的 `<img>` 标签
   与 turn 暴露的图片字段，关系如何？

#### 按钮"显示/禁用状态"字段

**性质**：底层概念不存在。

**调研结论**：
- 搜索 `ButtonEnable|buttonEnable|disable.*button|button.*disabled` 等模式，
  **没有任何 per-button enabled/disabled 字段**。
- `ConsoleButtonString` 持有的状态只有：`IsButton`、`IsInteger`、`Input`、
  `Inputs`、`Generation`、`PointX`、`Width`、`Title`、`ErrPos`。
- `SKIPDISP` 是进程级布尔标志（[Process.ScriptProc.cs#L563-L587](../../../Emuera.Headless/Shared/Runtime/Script/Process.ScriptProc.cs#L563-L587)），
  控制后续 PrintXxx 是否真正写入 buffer——**不是按钮级 disable**。

**为何排除**：用户描述的"按钮显示/禁用状态"在 Emuera 原版协议层面不存在。
即"缺失"的不是字段而是底层根本无此概念。若未来前端需要"按钮禁用"语义，
应先在 ERB 协议层定义（如新增 `DISABLEBUTTON` 指令、或扩展 button 的属性），
再考虑 turn 暴露。

**未来立项建议**：
1. 先确认前端"按钮禁用"的真实场景——是 SKIPDISP 后的渲染差异，还是新需求。
2. 若是新需求，先在 ERB 协议层定义按钮禁用语义。
3. 再扩展 turn 字段。

### 富 turn 升级整体设计方向（grilling 阶段未完成的部分）

富 turn 升级重启时，应先解决以下设计决策（grilling 未完成）：

1. **版本号策略**：是 v2 替换 v1、还是 v2 保留 v1 字段作降级？
   - grilling 倾向"保留 + 扩展"（v1 `text` 保留，新增 `lines[]` / 富 `buttons[]`），
     但用户决定暂缓，未最终确认。
2. **`text` 与 `lines[]` 的关系**：`text` 仍是 `console.TakeAgentBuffer()`
   的输出（增量），还是改用 `lines[]` 拼接？两者信息部分重叠。
3. **segment 边界**：`ConsoleButtonString` 的 `strArray` 是按 button 分段，
   还是按 style 变化分段？影响 `lines[].segments[]` 的设计。
4. **`CollectVisibleButtons` 的演化**：是扩展返回类型（加 row/col/width/style），
   还是新写 `CollectVisibleLines`（按行遍历，含 buttons）？
5. **`ButtonRegionTracker` 在 server 路径的复用**：当前只服务 CLI，server
   路径是否引入？需要哪些初始化？

### 已完成的相关基础工作

富 turn 升级的前置基础已落地：

- [PRD-T3](./PRD-T3-Turn协议版本化.md)：`TurnRecord` + `ButtonEntry` record
  + `protocolVersion` 字段。富 turn 升级时在此基础上扩展字段与版本号。
- [ADR-0001](../../adr/0001-turn-protocol-versioning.md)：记录版本字段策略，
  富 turn 升级时若改版本策略（如升 v2）应更新或追加 ADR。
- [CONTEXT.md](../../../CONTEXT.md)：定义 Turn / Initial Turn / Step Turn /
  Final Turn / Fatal Turn / protocolVersion 等术语，富 turn 升级时新增术语
  （如 Segment / DisplayLine / ButtonRegion）应同步更新。
