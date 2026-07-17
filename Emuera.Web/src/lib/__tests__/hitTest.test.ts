import { describe, it, expect } from 'vitest';
import { hitTestButton } from '../hitTest';
import type { ButtonRegion } from '../hitTest';

/**
 * hitTestButton 单测——与 C# `ButtonRegionTracker.HitTest`
 * （Emuera.Headless/Agent/ButtonRegionTracker.cs:85）对称。
 *
 * 测试矩阵（issue 02）：
 * - 命中按钮内（落点在 [col, col+width-1] 闭区间）
 * - 命中按钮外（落点在按钮范围之外）
 * - 空列表（regions=[] → 永远 null）
 * - 多按钮同行不重叠（不同 col 区分）
 * - 多按钮重叠（后加入的胜出——C# 从末尾向前遍历）
 * - row 不匹配（col 在范围内但 row 错位）
 * - 边界点：col=col（左边界）/ col=col+width-1（右边界）命中
 */

// ---------- 测试夹具 ----------

function region(
  row: number,
  col: number,
  width: number,
  value: number | string,
  isInteger = true,
): ButtonRegion {
  return { row, col, width, value, isInteger };
}

// ---------- 测试 ----------

describe('hitTestButton', () => {
  it('命中按钮内：落点在 [col, col+width-1] 闭区间', () => {
    const regions = [region(0, 0, 4, 1)];
    expect(hitTestButton(regions, { row: 0, col: 0 })).toBe(1);
    expect(hitTestButton(regions, { row: 0, col: 1 })).toBe(1);
    expect(hitTestButton(regions, { row: 0, col: 3 })).toBe(1);
  });

  it('命中按钮外：col 在范围外返回 null', () => {
    const regions = [region(0, 0, 4, 1)];
    expect(hitTestButton(regions, { row: 0, col: -1 })).toBeNull();
    expect(hitTestButton(regions, { row: 0, col: 4 })).toBeNull();
    expect(hitTestButton(regions, { row: 0, col: 100 })).toBeNull();
  });

  it('空列表：永远返回 null', () => {
    expect(hitTestButton([], { row: 0, col: 0 })).toBeNull();
    expect(hitTestButton([], { row: 5, col: 10 })).toBeNull();
  });

  it('row 不匹配：col 在范围内但 row 错位返回 null', () => {
    const regions = [region(0, 0, 4, 1)];
    expect(hitTestButton(regions, { row: 1, col: 0 })).toBeNull();
    expect(hitTestButton(regions, { row: -1, col: 2 })).toBeNull();
  });

  it('多按钮同行不重叠：按 col 区分命中', () => {
    // "[0]"(col=0, width=3) + "[1]"(col=3, width=3) + "[2]"(col=6, width=3) + "[Cancel]"(col=9, width=8)
    const regions = [
      region(0, 0, 3, 0),
      region(0, 3, 3, 1),
      region(0, 6, 3, 2),
      region(0, 9, 8, 'cancel', false),
    ];
    expect(hitTestButton(regions, { row: 0, col: 0 })).toBe(0);
    expect(hitTestButton(regions, { row: 0, col: 2 })).toBe(0);
    expect(hitTestButton(regions, { row: 0, col: 3 })).toBe(1);
    expect(hitTestButton(regions, { row: 0, col: 5 })).toBe(1);
    expect(hitTestButton(regions, { row: 0, col: 6 })).toBe(2);
    expect(hitTestButton(regions, { row: 0, col: 8 })).toBe(2);
    expect(hitTestButton(regions, { row: 0, col: 9 })).toBe('cancel');
    expect(hitTestButton(regions, { row: 0, col: 16 })).toBe('cancel');
    expect(hitTestButton(regions, { row: 0, col: 17 })).toBeNull();
  });

  it('多按钮同行多 row：按 row 区分命中', () => {
    const regions = [
      region(0, 0, 4, 1),
      region(1, 0, 4, 2),
      region(2, 0, 4, 3),
    ];
    expect(hitTestButton(regions, { row: 0, col: 0 })).toBe(1);
    expect(hitTestButton(regions, { row: 1, col: 0 })).toBe(2);
    expect(hitTestButton(regions, { row: 2, col: 0 })).toBe(3);
    expect(hitTestButton(regions, { row: 3, col: 0 })).toBeNull();
  });

  it('多按钮重叠：后加入的胜出（与 C# 从末尾向前遍历对称）', () => {
    // 两个按钮完全重叠在 row=0, col=0, width=4
    // C# ButtonRegionTracker.HitTest 从 _regions.Count-1 向前遍历——返回第一个命中
    // 即数组中靠后的（后加入的）胜出
    const regions = [
      region(0, 0, 4, 'first', false),
      region(0, 0, 4, 'second', false),
      region(0, 0, 4, 'third', false),
    ];
    expect(hitTestButton(regions, { row: 0, col: 0 })).toBe('third');
    expect(hitTestButton(regions, { row: 0, col: 2 })).toBe('third');
  });

  it('部分重叠：后加入的覆盖部分区域', () => {
    // [A: 0-4] + [B: 2-5] 重叠在 [2,4]
    const regions = [
      region(0, 0, 5, 'A', false),
      region(0, 2, 4, 'B', false),
    ];
    // A 独占区
    expect(hitTestButton(regions, { row: 0, col: 0 })).toBe('A');
    expect(hitTestButton(regions, { row: 0, col: 1 })).toBe('A');
    // 重叠区——B 胜出
    expect(hitTestButton(regions, { row: 0, col: 2 })).toBe('B');
    expect(hitTestButton(regions, { row: 0, col: 4 })).toBe('B');
    // B 独占区
    expect(hitTestButton(regions, { row: 0, col: 5 })).toBe('B');
    expect(hitTestButton(regions, { row: 0, col: 6 })).toBeNull();
  });

  it('边界点：col=col（左边界）+ col=col+width-1（右边界）都命中', () => {
    const regions = [region(0, 5, 4, 1)]; // [5, 6, 7, 8]
    expect(hitTestButton(regions, { row: 0, col: 4 })).toBeNull(); // 左边界外
    expect(hitTestButton(regions, { row: 0, col: 5 })).toBe(1); // 左边界
    expect(hitTestButton(regions, { row: 0, col: 8 })).toBe(1); // 右边界
    expect(hitTestButton(regions, { row: 0, col: 9 })).toBeNull(); // 右边界外
  });

  it('value 是 number 与 string 两种类型都正确返回', () => {
    const regions = [
      region(0, 0, 3, 42, true),
      region(1, 0, 3, 'click', false),
    ];
    expect(hitTestButton(regions, { row: 0, col: 1 })).toBe(42);
    expect(hitTestButton(regions, { row: 1, col: 1 })).toBe('click');
  });
});
