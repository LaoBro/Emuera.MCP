import type { DisplayState, DisplayLine, DisplayEntry, TurnOp, DisplayDiff } from '../types/protocol';

/**
 * 应用增量 ops 更新状态（issue 02）。
 *
 * 与 C# `TestAdapter.ApplyOps`（Emuera.Headless.Tests/TestAdapter.cs:97）对称：
 * - `print` → 向"当前行"（最后一行且 isLineEnd=false）追加 entry；无当前行则新建
 * - `newline` → 终止当前行（设 align + isLineEnd=true）；当前行已终止或空则产生空行
 * - `clearline` (n) → 从末尾删除 min(n, length) 行
 * - `clear` → 清空全部行 + 重置 bgColor=null
 * - `set_bg` → 更新 bgColor
 *
 * 关键状态机：PrintOp 总是追加到"当前行"——最后一行且 isLineEnd=false。
 * NewLineOp 终止当前行。这与 ConsolePrintManager 的 EmitPrintOps + NewLine 行为对齐。
 *
 * 函数式纯度：返回新对象，不修改入参 `state` 与 `ops`。内部用 `lines.slice()` 做可变
 * 影子数组提升 perf——但任何要修改的行都会先 `{...line}` 浅拷贝。entry 中的 segments
 * 与 button 也深拷贝（与 `applySnapshot` 的 `copyEntry` 一致——TS 对象默认可变，深拷贝
 * 保证 caller 后续 mutate 不影响内部状态）。
 *
 * state/inputType/needValue 不被 applyOps 修改——它们由 TurnRecord 顶层字段携带，
 * 不是 ops。与 C# TestAdapter 行为对称（T_full_roundtrip_preserves_state_inputType_needValue_from_snapshot）。
 *
 * @throws {Error} 未知 op 类型（与 C# `InvalidOperationException` 对称）
 */
export function applyOps(state: DisplayState, ops: TurnOp[]): DisplayState {
  // 可变影子数组——只在本函数内修改，不影响外部 state.lines
  const lines: DisplayLine[] = state.lines.slice();
  let bgColor: string | null = state.bgColor;

  for (const op of ops) {
    switch (op.type) {
      case 'print': {
        ensureCurrentLine(lines);
        const last = lines[lines.length - 1];
        // 深拷贝 segments/button（与 applySnapshot.copyEntry 一致——TS 对象默认可变）
        const newEntry: DisplayEntry = {
          segments: op.segments.map((s) => ({ ...s })),
          button: op.button ? { ...op.button } : null,
        };
        // 浅拷贝当前行 + 追加 entry，避免修改原 state 中的 line 对象
        lines[lines.length - 1] = {
          ...last,
          entries: [...last.entries, newEntry],
        };
        break;
      }
      case 'newline': {
        const align = op.align ?? null;
        if (lines.length === 0 || lines[lines.length - 1].isLineEnd) {
          // 空行后立即 newline，或连续 newline——产生一个空行
          lines.push({ entries: [], align, isLineEnd: true });
        } else {
          // 终止当前行——直接覆盖 align（与 C# Lines[^1].Align = newline.align 对称）
          const last = lines[lines.length - 1];
          lines[lines.length - 1] = { ...last, align, isLineEnd: true };
        }
        break;
      }
      case 'clearline': {
        const n = Math.min(op.n, lines.length);
        if (n > 0) lines.length = lines.length - n; // 截尾
        break;
      }
      case 'clear':
        lines.length = 0;
        bgColor = null;
        break;
      case 'set_bg':
        bgColor = op.color;
        break;
      default:
        throw new Error(`Unknown op type: ${(op as { type: string }).type}`);
    }
  }

  return {
    lines,
    bgColor,
    state: state.state,
    inputType: state.inputType,
    needValue: state.needValue,
  };
}

/**
 * 应用 `DisplayDiff`（v5 协议增量）更新状态（issue 02）。
 *
 * 与 C# `TestAdapter.ApplyDiff`（Emuera.Headless.Tests/TestAdapter.cs:58）对称：
 * - `append` → 追加 newLines（diff 已按行结构化，逐条转为内部 DisplayLine）
 * - `clear_line_diff` (clearCount) → 从末尾删除 min(clearCount, length) 行
 * - `clear_screen` → 清空全部行
 * - diff.bgColor 非空 → 更新 bgColor
 *
 * 与 `applyOps` 的差别：applyOps 消费引擎内部 `TurnOp[]`（print/newline/...），
 * applyDiff 消费对外 `LineOp[]`（append/clear_line_diff/clear_screen，plan C v5 显式清空信号）。
 * **WS 帧的 `TurnRecord.diff.lineOps` 即 `LineOp[]`——前端实际消费 WS 流靠此函数**，
 * applyOps 主要为与 C# TestAdapter 的 ApplyOps 测试对称（内部 op 流）。
 *
 * 函数式纯度：与 applyOps 一致——返回新对象，深拷贝 segments/button。
 *
 * state/inputType/needValue 不被 applyDiff 修改——它们由 TurnRecord 顶层字段携带。
 *
 * @throws {Error} 未知 LineOp 类型
 */
export function applyDiff(state: DisplayState, diff: DisplayDiff): DisplayState {
  const lines: DisplayLine[] = state.lines.slice();
  let bgColor: string | null = state.bgColor;

  for (const op of diff.lineOps) {
    switch (op.type) {
      case 'append':
        // 深拷贝每行 + 每个 entry（与 applySnapshot.copyLine/copyEntry 一致）
        for (const line of op.newLines) {
          lines.push({
            entries: line.entries.map((e) => ({
              segments: e.segments.map((s) => ({ ...s })),
              button: e.button ? { ...e.button } : null,
            })),
            align: line.align ?? null,
            isLineEnd: line.isLineEnd,
          });
        }
        break;
      case 'clear_line_diff': {
        const n = Math.min(op.clearCount, lines.length);
        if (n > 0) lines.length = lines.length - n;
        break;
      }
      case 'clear_screen':
        lines.length = 0;
        break;
      default:
        throw new Error(`Unknown LineOp type: ${(op as { type: string }).type}`);
    }
  }

  // diff.bgColor 非空时更新（null 表示未变，保留原 bgColor）
  if (diff.bgColor != null) {
    bgColor = diff.bgColor;
  }

  return {
    lines,
    bgColor,
    state: state.state,
    inputType: state.inputType,
    needValue: state.needValue,
  };
}

/**
 * 确保存在一个"当前行"——最后一行未终止（isLineEnd=false）；若不存在则追加新行。
 * 与 C# `TestAdapter.EnsureCurrentLine`（Emuera.Headless.Tests/TestAdapter.cs:140）对称。
 *
 * 直接 push 到 lines 数组（调用方负责保证 lines 是影子拷贝）。
 */
function ensureCurrentLine(lines: DisplayLine[]): void {
  if (lines.length === 0 || lines[lines.length - 1].isLineEnd) {
    lines.push({ entries: [], align: null, isLineEnd: false });
  }
}
