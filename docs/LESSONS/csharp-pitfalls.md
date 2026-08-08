# C# 语言陷阱 失败教训

> **TL;DR**：C# 静态字段按声明顺序初始化（有依赖必须按依赖序声明）；诊断代码插入单行 `if` 必须加大括号，否则改变控制流。

## 教训清单

| # | 标题 | 一句话 | 日期 |
|---|---|---|---|
| 1 | 静态字段按声明顺序初始化 | 被依赖的字段必须先声明，或用静态构造器 | — |
| 2 | 诊断日志插入 if 无大括号改控制流 | 单行 `if` 体插日志必须写成 `if (...) { log; break; }` | — |

---

## 1. C# 静态字段按声明顺序初始化

**场景**：`s_logEnabled` 在 `s_logPath` 之后声明，`s_logPath` 的初始化调用 `ResolveLogPath()` 读取 `s_logEnabled`。

**结果**：`s_logEnabled` 还是默认值 `false`，`ResolveLogPath()` 直接返回 `null`，日志功能不生效。

**原因**：C# 静态字段按声明顺序初始化。`s_logPath` 先初始化，此时 `s_logEnabled` 尚未被赋值，仍是 `false`。

**解决**：交换声明顺序，`s_logEnabled` 在 `s_logPath` 之前。

**教训**：有依赖关系的静态字段必须按依赖顺序声明。被依赖的字段必须先声明。或者改用静态构造函数/方法避免初始化顺序问题。

## 2. C# 诊断日志插入 if 无大括号会改控制流

**场景**：`ErhLoader.LoadHeaderFiles` 原代码：
```csharp
if (!noError)
    break;
```
诊断时在 `if` 与 `break` 之间插入两行日志且**未加大括号**：
```csharp
if (!noError)
    Console.WriteLine(...);  // 仅此行受 if 控制
    Console.Out.Flush();     // 总是执行
    break;                   // 总是执行
```

**结果**：142 个 ERH **只加载第 1 个**就退出；`noError` 仍可为 True（那一个文件成功）。日志显示 `files=142` 但实际只处理了 KOJO 类头文件 → `#DIM DVAR` / `#DEFINE カラー_*` 未注册 → ERB 阶段海量「解释できない識別子」。修好括号后：`ERH loaded=142/142, dimlines=1692`。

**教训**：
- C# 只认大括号，不认缩进；往单行 `if` 体插日志必须写成 `if (...) { log; break; }`
- 「列表长度正确」≠「循环真的跑完」——验收要打 **实际 loaded 计数**（如 `loaded=142/142`）
- 诊断代码与生产控制流绑在一起时，宁可用大括号块，也不要依赖「暂时只加一行」
