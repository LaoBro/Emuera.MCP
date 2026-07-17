import type { ButtonValue } from '../types/protocol';

/**
 * 按钮命中区（front-end 内部模型，对应 C# `ButtonRegionTracker.Region`）。
 *
 * 与 `ButtonRef` 的差别：补充了 `row`（行号）—— `ButtonRef` 只携带 col/width，
 * row 由调用方从 DisplaySnapshot.lines 索引推出。`ButtonRegion` 是行级布局已展开的形态。
 *
 * `col` 是行内起始列（0-based，绝对列——已包含 alignOffset，与 C# Region.Left 对称）。
 * `width` 是按钮显示宽度（含全角字符占 2 列）。命中范围：`[col, col + width - 1]`（闭区间）。
 */
export interface ButtonRegion {
  row: number;
  col: number;
  width: number;
  value: ButtonValue;
  isInteger: boolean;
}

/**
 * 按钮几何 hit-test（issue 02）。
 *
 * 与 C# `ButtonRegionTracker.HitTest`（Emuera.Headless/Agent/ButtonRegionTracker.cs:85）对称：
 * 从末尾向前遍历 regions，返回第一个 row 匹配且 col 落在 [col, col+width-1] 闭区间的按钮 value。
 *
 * "从末尾向前"语义：多按钮重叠时后加入的 region 胜出（与 C# `for (int i = _regions.Count - 1; i >= 0; i--)` 对称）。
 * 这对应 Emuera 的渲染顺序——后渲染的按钮覆盖在先渲染的按钮之上，点击应命中最上层。
 *
 * @param regions 按钮命中区列表（已展开 row + col + width）
 * @param point 命中坐标 {row, col}
 * @returns 命中按钮的 value，或 null（无命中）
 */
export function hitTestButton(
  regions: ButtonRegion[],
  point: { row: number; col: number },
): ButtonValue | null {
  for (let i = regions.length - 1; i >= 0; i--) {
    const r = regions[i];
    if (r.row === point.row && point.col >= r.col && point.col <= r.col + r.width - 1) {
      return r.value;
    }
  }
  return null;
}
