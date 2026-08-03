using System.Collections.Generic;
using MinorShift.Emuera.UI.Game;

namespace MinorShift.Emuera.GameView;

/// <summary>
/// 当前代可见按钮索引（2.2：按钮匹配全表扫描改索引）。
///
/// displayLineList 中 Generation == 当前代（lastButtonGeneration）的按钮按
/// int 值 / 字符串值建字典，输入匹配（ConsoleInputHandler.DoInputToEmueraProgram
/// 的 IntButton/StrButton 分支）O(1) 命中，取代旧的
/// <c>Enumerable.Reverse(displayLineList)</c> 全表扫描 + 每匹配 ToList 分配。
///
/// 失效双信号（任一变化 → EnsureSynced 重建，其余时间命中零扫描）：
/// - 行结构变化：ConsolePrintManager 的 AddDisplayLine / DeleteLine / ClearDisplay
///   调 <see cref="Invalidate"/> 递增版本号；
/// - 代切换：lastButtonGeneration 变化（NewGeneration / ForceUpdateGeneration 等，
///   ADR-0018 既有失效机制）。
///
/// 等价性说明（对照旧倒序扫描）：
/// - 旧逻辑遇旧代按钮（Gen != 0 && Gen != last）即停止主扫描——因 Generation 单调
///   递增，旧代行之后不可能再有当前代按钮，主扫描等价于「扫全部当前代按钮」；
/// - 同值多按钮（同一代多行同 Input/Inputs）旧逻辑命中最新行，本索引按行顺序覆盖
///   写入同样保留最后一行（最新）；
/// - int 按钮按两种拼写进字符串索引：Input.ToString() 与 Inputs——旧 StrButton 条件
///   <c>(IsInteger &amp;&amp; Input.ToString() == str) || Inputs == str</c> 两种都命中，
///   4 参构造（HtmlManager HTML 按钮）允许 Input=5 而 Inputs="05"；
/// - 混合代行（ChangeStr 合并跨代按钮到同一行）时旧逻辑数组正序遍历遇数组内旧代
///   按钮即停、连该行当前代按钮也拒绝；本索引按 Gen 过滤后仍接受当前代按钮——
///   差异方向为「新更宽容」，语义上更符合「当前代按钮可点」；
/// - div 逃逸按钮（escapedParts，不在 displayLineList）不在此索引，匹配回退路径保留
///   原扫描（见 ConsoleInputHandler.FindIntegerButton / FindStringButton）。
/// </summary>
internal sealed class ButtonIndex
{
    private readonly Dictionary<long, ConsoleButtonString> _byInt = new();
    private readonly Dictionary<string, ConsoleButtonString> _byStr = new();
    private long _version;              // 行结构版本，Invalidate() 递增
    private long _syncedVersion = -1;   // 上次同步时的 _version
    private long _syncedGeneration = -1; // 上次同步时的 lastButtonGeneration

    /// <summary>行结构变更（add/remove/clear）后调用，使索引在下一次匹配时重建。</summary>
    internal void Invalidate() => _version++;

    /// <summary>
    /// 与当前 displayLineList + 代同步。版本或代未变时零成本（两次 long 比较）。
    /// </summary>
    internal void EnsureSynced(List<ConsoleDisplayLine> lines, long generation)
    {
        if (_syncedVersion == _version && _syncedGeneration == generation)
            return;
        _byInt.Clear();
        _byStr.Clear();
        for (int i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var buttons = line.Buttons;
            if (buttons == null || buttons.Length == 0)
                continue;
            for (int j = 0; j < buttons.Length; j++)
            {
                var b = buttons[j];
                if (b == null || !b.IsButton || b.Generation != generation)
                    continue;
                if (b.IsInteger)
                {
                    _byInt[b.Input] = b;
                    // StrButton 分支对 int 按钮两种拼写都匹配（旧条件见类注释）：
                    // Input.ToString()（常规 int 按钮与 Inputs 相同，覆盖无副作用）
                    // 与 Inputs（4 参构造 HTML 按钮可自定义，如 Input=5 / Inputs="05"）。
                    _byStr[b.Input.ToString()] = b;
                    _byStr[b.Inputs] = b;
                }
                else
                {
                    _byStr[b.Inputs] = b;
                }
            }
        }
        _syncedVersion = _version;
        _syncedGeneration = generation;
    }

    /// <summary>IntButton 匹配：当前代是否有 int 值为 <paramref name="input"/> 的按钮。</summary>
    internal bool TryGetInteger(long input, out ConsoleButtonString? button) =>
        _byInt.TryGetValue(input, out button);

    /// <summary>StrButton 匹配：当前代是否有字符串（int 按钮按 Input.ToString()）为 <paramref name="s"/> 的按钮。</summary>
    internal bool TryGetString(string s, out ConsoleButtonString? button) =>
        _byStr.TryGetValue(s, out button);
}
