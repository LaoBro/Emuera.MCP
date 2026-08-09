# Web 字体跨平台网格 失败教训（2026-08-09）

> **TL;DR**：Emuera 终端网格依赖「半角 0.5em / 全角 1.0em」的固定宽度，Windows 靠 MS Gothic、Android 靠内置 IPA ゴシック（EmueraMonoJP）+ 补充字体（EmueraBlock）。本次踩坑链：**MS Gothic 符号区宽度并非均匀**（◢◣/框线半角、● 全角）→ 回退链缺口导致逐字形落到系统字体宽度随机 → GNU Unifont 选字提取（位图阶梯斜边锯齿）→ **Ubuntu Sans Mono 换 Box Drawing**（平滑但 ◢◣ 缺覆盖、0.56em 等宽需缩放）→ **DejaVu Sans 全接管**（四区全覆盖 + 平滑矢量 + 按 MS bbox 仿射缩放，Unifont/Ubuntu 全退出）；另有构建/发布链路暗坑（dist-maui 目录、SkipVueBuild、Vite 内联、WebView 缓存）。

## 教训清单

| # | 标题 | 一句话 |
|---|---|---|
| 1 | MS Gothic 符号区宽度非均匀 | Box/Geometric/Misc 内半角（◢◣、框线 0.5em）与全角（● 1.0em）并存，`IsWideChar` 整区全角判定是简化，补齐字体必须以**逐字符实测 advance** 为准 |
| 2 | 回退链缺口 = 宽度随机 | IPAGothic 缺失字符会逐字形回退到系统字体（Android Noto 等），宽度/观感不可控，滑动条 ◢◣ 从 MS Gothic 半角变全角而错位 |
| 3 | Unifont 选字按实测宽度 | Unifont 是 dual-width（8px/16px）字体，Box Drawing 半角与 MS Gothic 部分不一致；**宽度一致者才提取**，避免引入新错位 |
| 4 | 跨 UPM 提取漏 scale | Unifont UPM=64，提取到 2048 UPM 字体必须 ×32 缩放；只平移不缩放 → 字形只有 3% → 表现"字符消失、advance 正确" |
| 5 | Vite 内联小字体 | `assetsInlineLimit`（默认 4KB）把 <4KB 的 woff2 内联成 css `data:font` base64，产物里**没有独立字体文件 ≠ 没打包**；排查先搜 `data:font` |
| 6 | MAUI 前端产物目录是 dist-maui | VueBuild.targets `VueOutDir=dist-maui`（不是 `npm run build` 默认的 dist），改前端资产后必须按 dist-maui 构建，否则 wwwroot/APK 用旧前端 |
| 7 | SkipVueBuild 跳过整个前端构建 | publish 带 `-p:SkipVueBuild=true` 时 wwwroot 不更新；README 示例命令都带它（默认场景"没改前端"），改了字体/前端资产必须去掉 |
| 8 | WebView 资源缓存 | Android WebView 对 file:// 资源有缓存，覆盖安装可能继续用旧 css/字体；改字体后**卸载重装**（或清缓存） |
| 9 | fontTools 版本差异 | 旧版 `T2CharString.draw()` 不接受 `glyphSet` 关键字参数；Unifont 符号区无 seac，直接 `draw(pen)` 即可 |
| 10 | glyf 坐标返回格式 | `glyf.Glyph.getCoordinates()` 返回**元组列表** `[(x,y),...]`，诊断脚本别按扁平数组 `[x0,y0,...]` 切分 |
| 11 | 等宽字体位图 vs 矢量平滑 | Unifont 是位图转矢量（斜边阶梯锯齿，╱╲╳╭╮╰╯ 观感差）；Ubuntu Sans Mono 是平滑二次贝塞尔——选字按"字符是否含斜边/曲线"分工：Box Drawing 走 Ubuntu、Block/Geo/Misc 走 Unifont、◢◣◤◥ 程序化直边 |
| 12 | TTGlyphPen 分解组合字形 | Ubuntu 的 ░▒▓ 是组件 glyph；TTGlyphPen 需要 glyphSet 元素有单参 `draw(pen)` 协议（glyf.Glyph.draw 需 glyfTable 参数），要包一层 wrapper；同时实现 `__contains__` |
| 13 | 缩放目标 ≠ advance 比例 | Ubuntu 是 0.56em 等宽、MS 是 0.5em，按 advance 比例缩放使字形居中留白（视觉宽度差一倍）；MS 框线是**满格绘制**，应"水平字形宽 → 目标格子宽"拉伸到 0..1024 |
| 14 | glyf numberOfContours 判定 | `contours=-1` 是组合字形、contours=0 是空字形（space），提取时跳过空字形、放行组合字形让 TTGlyphPen 递归分解 |
| 15 | MS 几何符号非满格 | MS Gothic 的 ◢◣◤◥ 是**居中的 0.445em 等边三角**（宽/高各占格子 89%/44.5%），程序化复刻必须按实测 bounds，不能画满格子——"小一圈"是 MS 的设计而非 Bug |
| 16 | DejaVu Sans 全接管 | DejaVu Sans 四区全覆盖（Box/Block/Geo 全、Misc 189/256）且平滑矢量；按 **MS 实测 bbox** 仿射缩放（源 bbox → 目标 bbox）替代"满格拉伸"——┄ 留白、═ 细线、╭ 半格都精确对齐 MS，Unifont/Ubuntu/程序化全退出（265 字形 100% DejaVu） |
| 17 | 手工构建 Glyph 缺字段 | 用 `getCoordinates` 解析组合字形后手工构建 `Glyph()` 必须设置 `numberOfContours` 和 `program`（ttProgram.Program()），否则编译为空字形（contours=0 重读无轮廓） |

---

## 1. MS Gothic 符号区宽度非均匀——按区段一刀切是错的

**场景**：安卓端滑动条 ◢◣ 显示为全角，Windows（MS Gothic）正常。

**结果**：最初按 `IsWideChar`（Box/Geometric/Misc = 全角）推断 ◢◣ 应补全角字形，方向反了。

**原因**：`IsWideChar` 的整区判定是引擎简化。实测 `msgothic.ttc`（UPM=256）：◢◣◤◥ 与各类框线 advance=128（0.5em 半角），而 ● advance=256（1.0em 全角）——同一 Geometric 区段内宽度并不统一。脚本作者按 MS Gothic 实际渲染设计滑动条（半角），引擎列数与字体宽度在 Windows 上自洽。

**解决**：`analyze_unifont_widths.py` 对每个 IPAGothic 缺失字符**逐字符比对 MS Gothic 与候选字体的 advance（em 归一）**，宽度一致才采用，不复刻区段假设。

**教训**：字体宽度判定必须实测到字符级，不能信区间/区段假设；"游戏设计宽度"（IsWideChar）与实际字体度量是两层，跨平台补齐以前者语义、后者度量。

## 2. 回退链缺口 → 系统字体宽度随机

**场景**：`游戏字体名 → EmueraMonoJP(IPAGothic) → EmueraBlock → ui-monospace` 回退链中，IPAGothic 缺 ◢◣◤◥。

**结果**：◢◣ 逐字形回退到 Android 系统等宽字体（CJK 字体按全角渲染），滑动条轨道宽一倍、错位。

**解决**：`EmueraBlock` 补充字体接住这些字符（Unifont 193 + 程序化兜底 10），回退链不再落到系统字体。

**教训**：回退链的"缺口"（中间字体缺失的字形）是宽度随机化的来源；排查字体问题时先确认"字符在回退链每一环的覆盖情况"（`check_font_coverage.py`）。

## 3. Unifont 选字：宽度一致才提取

**场景**：需要覆盖 IPAGothic 缺失的大量字符，程序化逐字绘制成本高。

**方案**：GNU Unifont（OFL 1.1 / GPLv2+ 嵌入例外双许可）字符覆盖极全且是 dual-width（8px 半宽 / 16px 全宽混合）。对每个候选字符比对 MS Gothic 与 Unifont 的 advance，一致才从 Unifont 提取（CFF → TrueType），不一致丢弃（如 62 个"Unifont 半角 vs MS 全角"）。最终 193 字符入选：Box 96/96、Block 22/32、Geometric 57/73、Misc 18/228。

**教训**：用"第三方字体 + 实测宽度筛选"批量补字形，比程序化逐字符绘制覆盖广且省力；筛选条件是"宽度与主字体实测一致"，不是"Unifont 有"。

## 4. 跨 UPM 提取漏 scale——字形消失但 advance 正确

**场景**：Unifont 提取后真机"字符消失，但排版宽度正确"。

**原因**：Unifont UPM=64，字形坐标 0..64；提取到 2048 UPM 字体必须 ×32。实现时 `TransformPen(pen, (1, 0, 0, 1, 0, dy))` 只做了垂直平移，**漏了 scale**，字形只有原始尺寸 ~3%（几乎不可见），而 advance 是按 em 换算的（正确）——两者割裂造成"宽度对、字形没"。

**解决**：
```python
scale = UPM / uni_upm          # 2048 / 64 = 32
dy = round(YC - (ymin + ymax) / 2 * scale)   # 缩放后居中到格子中心 778
tpen = TransformPen(pen, (scale, 0, 0, scale, 0, dy))
```
加 `check_glyph_bounds.py` 验证字形轮廓尺寸（防回归）：半角字符 x 跨度 ≈1024、y 跨度 ≈2048。

**教训**：跨 UPM 提取字形,变换矩阵必须同时含 scale 与 translate；"advance 正确"不能证明"字形正确"，要用轮廓 bbox 验证（advance 是布局、轮廓是视觉，两层独立）。

## 5. Vite 内联小字体——产物里没有独立 woff2 ≠ 没打包

**场景**：`dist-maui/assets/` 里只有 IPAGothic-xxx.woff2（3MB），没有 EmueraBlock.woff2，误判"没打包"。

**原因**：Vite `assetsInlineLimit` 默认 4096 字节，EmueraBlock.woff2（~3KB）被内联成 css `data:font/woff2;base64,...`，IPAGothic（3MB）超限才独立成文件。

**解决**：排查时搜 css 里 `data:font`，用 fontTools 解码 base64 验证内联字体内容（含哪些字符、advance 多少）。

**教训**：小资源内联是构建工具默认行为，"产物里没有文件"不等于"没进产物"；验证要按"渲染链路实际加载的内容"来查，不是按文件名存在性。

## 6/7. MAUI 前端产物目录 dist-maui + SkipVueBuild

**场景**：改了字体后重新 `npm run build`（输出 dist/），publish 后真机无变化。

**原因**：
- `build/VueBuild.targets` 的 `VueOutDir=dist-maui`——MAUI 的 wwwroot 由它从 **dist-maui**（不是 dist）拷贝；
- publish 命令带 `-p:SkipVueBuild=true` 时 `BuildVueFrontend`/`CopyVueFrontendToWwwroot` 整个跳过，wwwroot 不更新，APK 打包旧前端。

**解决**：改前端资产后要么手动 `npm run build -- --base=./ --outDir=dist-maui`，要么 publish **去掉** SkipVueBuild 让 targets 自动重建+拷贝；发布前验证 `dist-maui/assets/` 产物（含内联字体）。

**教训**：构建产物目录、构建开关都要按"实际消费方"确认（MAUI 消费 dist-maui，不是开发者习惯的 dist）；README 示例命令带 SkipVueBuild 是"未改前端"的默认场景，改前端资产时必须显式去掉。

## 8. WebView 资源缓存——覆盖安装不刷新

**场景**：重新 publish + 覆盖安装 APK，字体依旧旧版。

**原因**：Android WebView 对 file:// 资源有缓存，覆盖安装不一定失效。

**解决**：卸载重装（或清 WebView 缓存）后新字体才生效。

**教训**：前端资源类修复验证时，覆盖安装可能假失败；先排除缓存再怀疑构建链路。

## 9. fontTools 版本差异：draw() 不接受 glyphSet 关键字

**场景**：`T2CharString.draw()` 报 `unexpected keyword argument 'glyphSet'`，193 字符全部跳过（Unifont 提取 0 字符）。

**原因**：`draw(pen, glyphSet=...)` 是较新 fontTools 的签名，旧版本只有 `draw(pen)`。

**解决**：Unifont 符号区为简单轮廓（无 seac 组合），直接 `cs.draw(pen)`；组件由 `TTGlyphPen/BoundsPen` 构造时的 glyphSet 参数处理。

**教训**：跨环境脚本避免依赖版本敏感的 API 关键字；异常保护（逐字符 try/except）让批量提取不至于整体失败，但"0 成功"要当错误看而非静默通过。

## 10. glyf 坐标返回格式

**场景**：`check_glyph_bounds.py` 输出 `x[(0,1802)..(1024,-246)]` 的元组形式。

**原因**：`glyf.Glyph.getCoordinates()` 返回 `[(x,y),...]` 元组列表（或 complex 数组），不是扁平 `[x0,y0,x1,y1,...]`。

**解决**：按元素类型分支提取（tuple → `p[0]/p[1]`；complex → `.real/.imag`；否则扁平切分）。

**教训**：解析字体内部数据结构先确认实际形态（可打印一两个元素看类型），别按记忆中的格式写。

## 11. 等宽字体位图 vs 矢量平滑——按字符特性分工

**场景**：上一轮 EmueraBlock 主体用 Unifont（193 字符），真机测试发现 ╱╲╳╭╮╰╯┄┅ 等斜边/曲线字符"锯齿感严重"。

**原因**：GNU Unifont 是**位图转矢量**（8×16 / 16×16 dual-width 像素网格），轮廓是离散阶梯——斜边（╱╲╳）、圆角（╭╮╰╯）的曲线在像素级是阶梯直线，终端字体大小的视觉锯齿明显。MS Gothic 这类矢量字体却是平滑二次贝塞尔曲线。

**解决**：选 **Ubuntu Sans Mono v1.100**（Canonical 官方，Ubuntu Font Licence 1.0 可嵌入）——v1.100 公告明确"This version adds box drawing characters"——**专门补 Box Drawing**，字形为平滑二次贝塞尔轮廓（╱╱╳╭╮╰╯ 7 个 off-curve 圆弧点）。但分析显示 Ubuntu **不覆盖 ◢◣◤◥**（Geometric 只 2/96），且所有符号 advance 都是 0.56em（纯等宽）与 MS Gothic 的 0.5em/1.0em 不同——故最终**三层分工**：

| 区段 | 来源 | 理由 |
|---|---|---|
| Box Drawing（╱╲╳╭╮╰╯┄┅═╔╗） | Ubuntu Sans Mono | 平滑贝塞尔，斜边/曲线不再锯齿 |
| Block / Geometric / Misc 直线字符 | Unifont | 直线矩形无锯齿问题；Unifont 满格与 MS 一致 |
| ◢◣◤◥（滑动条三角） | 程序化直边三角 | Ubuntu 没覆盖；Unifont 位图斜边锯齿；程序化直线斜边与 MS Gothic 一致（MS 本来就是直线三角） |

**教训**：选第三方字体解决"观感"问题时，先看字形是位图还是矢量；位图字体即使宽度正确也有阶梯锯齿。多源策略：每个区段按其"观感瓶颈"选最擅长的来源，不必"全家都用一个"。

## 12. TTGlyphPen 分解组合字形——glyphSet 协议 + `__contains__`

**场景**：Ubuntu 的 ░▒▓（U+2591–2593）是**组合字形**（每个由 36 个 GlyphComponent 拼成），`glyf.Glyph.numberOfContours = -1`。最初加 `if numberOfContours <= 0: continue` 跳过——3 个字符落到程序化兜底（与上一轮 Unifont 时代一致），但点阵密度观感有差。改用 TTGlyphPen 分解时，`T2CharString.draw()` 风格的 `draw(pen, glyphSet=...)` 报错。

**原因**：
1. `TTGlyphPen(glyphSet=...)` 的 glyphSet 协议要求元素有**单参 `draw(pen)`**——但 `fontTools.ttLib.tables._g_l_y_f.Glyph.draw(pen, glyfTable)` 需要显式 glyfTable 参数（fontTools 4.63 强制）；
2. 内部 `_buildComponents` 用 `if glyphName not in self.glyphSet` 判断，需要 **`__contains__`**——默认 dict 子类支持，但自己实现的包装类要显式实现；
3. `__getitem__` 返回的元素必须有 `.draw(pen)` 单参数签名。

**解决**：
```python
class _GlyfGlyph:
    def __init__(self, glyph, glyf):
        self._glyph, self._glyf = glyph, glyf
    def draw(self, pen):
        self._glyph.draw(pen, self._glyf)

class _GlyfGlyphSet:
    def __init__(self, glyf):
        self._glyf = glyf
    def __getitem__(self, name):
        return _GlyfGlyph(self._glyf[name], self._glyf)
    def __contains__(self, name):
        return name in self._glyf

pen = TTGlyphPen(_GlyfGlyphSet(glyf))  # 组合字形自动分解
```

**教训**：fontTools 高层 API（FontBuilder、TTGlyphPen）对 glyf 协议有版本敏感要求；包 wrapper 时要把"协议表"（`__getitem__`、`__contains__`、元素 `.draw`）一次满足，缺一就报错而非告警。

## 13. 缩放目标 ≠ advance 比例——MS 框线是满格绘制

**场景**：Ubuntu 等宽 advance=0.56em、MS 半角 advance=0.5em。第一版 `scale_x = target_adv * src_upm / (src_adv * UPM) = 0.893`（按 em 比例缩放），结果 ═╱█░ 提取后 x[258..766]@2048（视觉 0.13..0.37em，居中留白，宽度只有 MS 的 50%）。

**原因**：MS Gothic 的 ═╱█░ 是**满格绘制**（x 从格子左缘到右缘 = 0..0.5em），按 em 比例缩放后字形居中放回格子就两侧留白。Ubuntu 源字形在 0.56em 格子里就接近满格，按比例缩放后字形宽 0.49em，但放在 0.5em 格子中间——视觉上"半宽"。

**解决**：水平改为"字形视觉宽度 → 目标格子宽度"拉伸：
```python
xw = max(xs) - min(xs)             # 源字形视觉宽
sx = target_adv / xw if xw > 0 else 1
sy = UPM / src_upm                 # 垂直 em 保持
cx = (min(xs) + max(xs)) / 2
dx = target_adv / 2 - cx * sx
```
结果 ═╱█░ 都 0..1024 满格，与 MS 一致。

**教训**：跨字体提取前先**比较源字体与目标字体在格子内的"字形填充策略"**——满格绘制（如终端框线）vs 居中留白（如部分 UI 符号）的差异，按 advance 比例缩放完全压不住；要按"目标字形应有的视觉宽度"决定缩放目标。

## 14. glyf numberOfContours 判定——空字形 vs 组合字形

**场景**：Ubuntu 提取时遇到 ░▒▓（combining）和 space-like glyph（`numberOfContours=0`），需要决定哪些"提取、哪些跳过"。

**判定规则**：
- `numberOfContours > 0`：简单字形，直接 `getCoordinates` 取轮廓 ✓
- `numberOfContours == -1`（且有 `components`）：组合字形，TTGlyphPen + glyphSet 递归分解 ✓
- `numberOfContours == 0`：空字形（如 space），无轮廓也无组件，跳过 ✓

**坑**：最初 `if numberOfContours <= 0: continue` 一刀切，把组合字形（░▒▓）也跳过了。

**解决**：
```python
if glyph.numberOfContours == 0 and not getattr(glyph, 'components', None):
    continue  # 空字形跳过
# 组合字形继续往下走，由 TTGlyphPen(glyphSet) 自动分解
```

**教训**：`numberOfContours` 三个值域对应三种字形类型，提取前按值分流而不是统一过滤；组合字形专门处理（glyphSet 协议，见 #12）。

## 15. MS 几何符号非满格——"小一圈"是设计不是 Bug

**场景**：EmueraBlock 的 ◢◣◤◥ 换成程序化直边三角后，真机测试发现"比 MS Gothic 大一圈"。

**测量**（`msgothic.ttc` fontNumber=0，em 归一）：
- ◢◣◤◥ 四者完全对称：x[0.027..0.473]em、y[0.137..0.582]em → **宽 0.445em、高 0.445em**（等边三角）
- 半角格子是 0..0.5em：宽占 89%，**高只占格子 44.5%**——MS 的 ◢◣ 是居中放置的小三角，四周有大量留白
- 水平/垂直中心都在格子正中（0.25em / 0.359em）

**原因**：MS Gothic 的几何符号（◢◣◤◥）设计为"格子内居中、留白边距"的视觉风格，不是填满格子。程序化三角第一版画满半角格子（0..1024 × -246..1802），高是 MS 的 2.25 倍。

**解决**：按实测 bounds 复刻到 2048 UPM 坐标系：
```python
# x[0.027..0.473]em → 55..969(中心 512)
# y[0.137..0.582]em → 322..1234(映射到本字体格子 -246..1802,中心 778)
GX0, GX1, GY0, GY1 = 55, 969, 322, 1234
```

**教训**：程序化绘制"复刻某个字体观感"时，先测量目标字体的**实际字形 bounds**（bbox），不要假设字符画满格子；符号类字符在等宽字体里往往居中留白（尤其 ◢◣◤◥ 这类装饰符号）。advance（格子）与字形视觉大小（bbox）是两层，都要实测。

## 16. DejaVu Sans 全接管——按 MS bbox 仿射缩放

**场景**：Ubuntu Sans Mono（Box）+ Unifont（其余）双来源仍有维护成本，且 Ubuntu 的"满格拉伸"策略对 ┄ 等留白字符不够精确。

**方案**：换 **DejaVu Sans 2.37**（Bitstream Vera 衍生，自由许可）——四区全覆盖（Box 128/128、Block 32/32、Geometric 96/96、Misc 189/256），平滑矢量轮廓。提取策略从"满格拉伸"升级为 **按 MS Gothic 实测 bbox 仿射缩放**：

```python
# analyze 脚本为每个字符测量 MS bbox(em)，换算到 2048 坐标存 target:[x0,y0,x1,y1]
# build 提取时：源字形 bbox → 目标 bbox 的仿射变换
sx = (tx1 - tx0) / (sx1 - sx0)
sy = (ty1 - ty0) / (sy1 - sy0)
dx = tx0 - sx0 * sx
dy = ty0 - sy0 * sy
```

**效果**（与 MS Gothic 实测逐项对齐）：
- `═` 线宽 168 units ≈ MS 实测 0.082em（此前 Ubuntu 版 0.23em 粗 3 倍）
- `┄` 保留 MS 的 x[0.062..0.434]em 留白（此前满格）
- `╭` x[0.238..0.5]em 半格绘制（此前满格）
- `☢☯☺` 保留 37/36/40 个 off-curve 平滑曲线点

**最终字体**：265 字形 = **DejaVu 265（100%）**，Unifont/Ubuntu/程序化全部退出。◢◣◤◥ 三角不再程序化——DejaVu 的三角也是 3 点直线（bbox 0.763×0.766em 比 MS 大 72%），按 MS bbox 仿射缩放后顶点精确落在 MS bbox 角上，与程序化结果**完全等价**（实测差 ≤1 unit）；Block 象限（MS 缺失）从 DejaVu 取自身尺寸。

**教训**：字体观感复刻的最高精度是"逐字符按目标字体 bbox 缩放"而非"按区段/满格"；比例字体（DejaVu advance 各异）比等宽字体（Ubuntu 0.56em 统一）更能覆盖全区符号，前提是提取时强制 advance = MS 实测值。程序化兜底可被"同构字形 + bbox 缩放"完全替代——程序化能画的直线多边形，矢量字体里往往有对应字形，取来按 MS bbox 缩放即可。

## 17. 手工构建 Glyph 缺字段——编译为空字形

**场景**：用 `getCoordinates(glyf)` 解析组合字形轮廓后，手工 `Glyph()` 构建新字形并写入 FontBuilder，保存重读后所有字形 `contours=0`（空），文件大小异常小（265 字形仅 1.5KB）。

**原因**：`glyf.compile()` 的 `compileCoordinates` 需要 `self.program`（TrueType 指令程序），且 header 打包需要 `self.numberOfContours`。手工构建的 Glyph 没设这两个字段 → 编译产物为空。

**解决**：
```python
new_glyph = Glyph()
new_glyph.coordinates = GlyphCoordinates([(round(x), round(y)) for ...])
new_glyph.endPtsOfContours = list(endpts)
new_glyph.flags = array('B', flags)
new_glyph.numberOfContours = len(endpts)     # 必须
new_glyph.program = ttProgram.Program()      # 必须
```

**教训**：跨字体字形重写（不经过 draw/pen 而是直接操作轮廓数据）时，Glyph 对象必须补齐编译所需全部字段；验证"保存→重读"而非只看内存对象——内存有轮廓、编译后空字形是最隐蔽的失败模式（文件大小异常小是信号）。
