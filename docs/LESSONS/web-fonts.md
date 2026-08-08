# Web 字体跨平台网格 失败教训（2026-08-09）

> **TL;DR**：Emuera 终端网格依赖「半角 0.5em / 全角 1.0em」的固定宽度，Windows 靠 MS Gothic、Android 靠内置 IPA ゴシック（EmueraMonoJP）+ 补充字体（EmueraBlock）。本次踩坑链：**MS Gothic 符号区宽度并非均匀**（◢◣/框线半角、● 全角）→ 回退链缺口导致逐字形落到系统字体宽度随机 → 用 GNU Unifont 按"MS Gothic 实测 advance 一致"选字提取补齐 → 构建/发布链路三个暗坑（dist-maui 目录、SkipVueBuild、Vite 内联、WebView 缓存）。

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
