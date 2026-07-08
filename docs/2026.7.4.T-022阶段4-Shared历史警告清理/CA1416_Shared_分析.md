# Shared 代码 CA1416 警告消除 —— 执行记录（2026-07-08 已完成）

## 结论

Shared 段 CA1416 **已彻底消除**：5 处 `Microsoft.VisualBasic.Strings.StrConv` 调用全部替换为跨平台自实现
`StringConverter`，真实配置全量构建 **0 警告 0 错误（CA1416 = 0）**。`.editorconfig` 的 `[Emuera.Headless/Shared/**]`
段 CA1416 抑制行已删除。

## 技术根因（复盘）

- Headless 目标框架 `net10.0`（跨平台），不引用 `System.Drawing.Common`（I-14 已移除 GDI）。
- Shared 内 5 处 `Strings.StrConv`（日文假名/全半角转换）不在 `#if !HEADLESS` 分支，Headless 编译路径真实参与编译 → 触发 CA1416（真实，非死配置）。
- 自有代码 `WindowsTerminalSetup.cs` 另有 4 处 `Console.Buffer*/Window*` 仍触发 CA1416，属**范围外**，保留抑制。

## 实现

`Emuera.Headless/Shared/Runtime/Utils/StringConverter.cs`（无 Windows / `Microsoft.VisualBasic` 依赖）：
- `Convert(str, StrConvFlags, locale)`：调用形态与原 `Strings.StrConv(str, flags, locale)` 完全一致，行为差异完全由实现决定。
- 平片假名偏移 0x60（扩展假名 30F4–30F6、长音记号 30FC 不转换，与 VB 一致）。
- ASCII `U+0021–U+007E ↔ U+FF01–U+FF5E`、半角片假名 `U+FF61–U+FF9F` 经 NFKC 合成预成字。
- 反斜杠 `U+005C`/`U+FF3C` 不做全半角互换；基础字+半角浊点/半浊点组合合成预成字；日元 `U+00A5` 全局→`U+005C`，全角日元 `U+FFE5` 仅 HalfWidth 归一化（均与 VB 行为一致）。
- 方法名使用 `ToFullWidth`/`ToHalfWidth`（匹配 §9.3 规范命名），而非早期实现时的 `ToWide`/`ToNarrow`。

## 行为一致性验证（关键）

用 Windows 上真实的 `Microsoft.VisualBasic.Strings.StrConv` 作 oracle，对 **2455 组**输入（标准假名单字符全扫描 +
ASCII/半角片假名 + 浊点/半浊点组合 + 现实短语 + 反斜杠/日元变体，5 种标志组合 × 日语 locale 0x0411）逐项比对：
**MISMATCH=0, ORACLE_ERR=0**。

> 验证教训：早期曾用 `-p:AnalysisMode=All` 覆盖 + 无关探针 API，得到"0 CA1416"的**假阴性**，险些误删抑制使构建变红。
> 正确做法是用**项目真实配置** + **真实触发点** + oracle 比对。

## 与 VB 的有意差异（改进而非回归）

VB6 `LCMapString` 对扩展兼容假名 `U+3095/U+3096/U+30F4–U+30FA` 输出字面 `?`（VB bug，原 Emuera 亦有）。
`StringConverter` 保留这些字符原样（如 `関ヶ原` 的 `ヶ` 不再变成 `?`）。标准假名与 ASCII 全半角 100% 一致。

## 遗留

- 自有代码段 `WindowsTerminalSetup.cs` 4 处 `Console.Buffer*/Window*`：仍为 CA1416 保留项，需另立任务（方案 A：平台守卫 + 跨平台终端尺寸回退）。
- `Shared` 内已无 `Microsoft.VisualBasic` 代码引用；项目级包引用保留（移除超出本专项范围）。
