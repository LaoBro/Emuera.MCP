# Win32 P/Invoke 失败教训

> **TL;DR**：Win32 结构体 marshalling 必须与原始定义逐字节对齐——`BOOL` 用 `int`（4 字节）不用 `bool`、避免不必要的嵌套 union、过滤条件要精确匹配目标特征。

## 教训清单

| # | 标题 | 一句话 | 日期 |
|---|---|---|---|
| 1 | 结构体 BOOL 必须用 int | `bool` marshalling 语义不同导致字段偏移错位、键盘事件全失灵 | — |
| 2 | CHAR_UNION 嵌套结构体偏移错位 | `uChar` 直接映射 `char`，不要用 union 模拟 | — |
| 3 | IME 过滤不能基于 uChar==0 | 控制键 uChar 合法为空；IME 中间态特征是 `VK_PROCESSKEY(0xE5)` | — |

---

## 1. Win32 P/Invoke 结构体中 BOOL 必须用 int 而非 bool

**场景**：`KEY_EVENT_RECORD.bKeyDown` 声明为 `bool`，`FOCUS_EVENT_RECORD.bSetFocus` 同理。

**结果**：所有键盘事件被当作释放事件（`bKeyDown=false`）忽略，键盘输入完全失灵。

**原因**：Win32 `BOOL` 是 4 字节 `int`（0/非0），C# `bool` marshalling 语义不同。`bool` 在结构体中的布局和对齐可能与 Win32 `BOOL` 不一致，导致后续字段偏移错位，读取到错误值。

**解决**：将 `bKeyDown`、`bSetFocus` 等 Win32 BOOL 字段改为 `int`，判断时用 `!= 0`。

**教训**：P/Invoke 结构体中 Win32 `BOOL` 一律用 `int`，不要用 `bool`。`bool` 只适用于 Win32 API 参数（P/Invoke 会自动 marshalling），不适用于嵌套在结构体中的字段。

## 2. CHAR_UNION 嵌套结构体导致 marshalling 偏移错位

**场景**：`KEY_EVENT_RECORD.uChar` 声明为 `CHAR_UNION`（含 `UnicodeChar` 和 `AsciiChar` 两个 `byte` 字段的 union）。

**结果**：`uChar` 读取到错误值，`uChar==0` 的过滤条件把方向键等控制键全部跳过。

**原因**：`CHAR_UNION` 作为显式布局结构体嵌套在 `KEY_EVENT_RECORD` 中，字段偏移计算容易出错。Win32 原始定义中 `uChar` 是一个 `char`（2 字节 Unicode），直接映射更简单可靠。

**解决**：去掉 `CHAR_UNION`，`uChar` 直接声明为 `char`。

**教训**：P/Invoke 结构体尽量与 Win32 原始布局一一对应，避免不必要的嵌套结构体。能用简单类型直接映射的就不要用 union 模拟。

## 3. IME 过滤条件不能基于 uChar==0

**场景**：键盘事件过滤中，`uChar==0 && !IsModifierKeyCode(vk)` 被当作 IME 中间态跳过。

**结果**：方向键、PgUp/PgDn、Home/End、Insert/Delete、F1-F24 等控制键的 `uChar` 都是 `\0`，全部被错误跳过。

**原因**：IME 组合输入的中间态特征是 `wVirtualKeyCode == VK_PROCESSKEY (0xE5)`，不是 `uChar==0`。控制键有合法的 `wVirtualKeyCode` 但 `uChar` 为空。

**解决**：只过滤 `vk == VK_PROCESSKEY`，其他所有按键事件都放行给 `ProcessKey`。

**教训**：过滤条件必须精确匹配目标特征，不能凭直觉扩大范围。`uChar==0` 是控制键的正常属性，不是异常状态。
