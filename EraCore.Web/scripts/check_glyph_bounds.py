#!/usr/bin/env python3
"""
check_glyph_bounds.py — 检查 EmueraBlock 字形轮廓尺寸。

用途：验证 Unifont 提取的字形是否正确缩放到 2048 UPM 并居中到格子
（曾出现漏 scale 的 bug：advance 正确但字形只有 ~3% 大小，视觉上"字符消失"）。

预期（半角字符 x 跨度 ≈1024、全角 ≈2048；y 跨度 ≈2048，居中于 -246..1802）：
  U+25E3 ◣: x[0..1024] y[-246..1802]   ← 半角，滑动条三角
  U+2504 ┄: x[0..1024] y[-246..1802]   ← 半角，Unifont 虚线框
  U+2550 ═: x[0..1024] y[-246..1802]   ← 半角（MS Gothic 实测）
  U+2622 ☢: x[0..2048] y[-246..1802]   ← 全角
  U+2588 █: x[0..1024] y[-246..1802]   ← 半角，程序化兜底

若 y 跨度只有几十 units（如 y[0..64]），说明缩放缺失，需重新生成。

用法：python scripts/check_glyph_bounds.py
"""
from __future__ import annotations

import os

from fontTools.ttLib import TTFont

FONT = os.path.join(os.path.dirname(__file__), '..', 'src', 'assets', 'fonts', 'EmueraBlock.woff2')
CHECKS = [(0x25E3, '◣'), (0x25E2, '◢'), (0x2504, '┄'), (0x2550, '═'),
          (0x2588, '█'), (0x2622, '☢'), (0x2596, '▖')]


def main() -> None:
    font = TTFont(FONT)
    glyf = font['glyf']
    cmap = font.getBestCmap()
    print(f'{FONT}')
    for cp, ch in CHECKS:
        name = cmap.get(cp)
        if name is None:
            print(f'U+{cp:04X} {ch}: 缺失')
            continue
        coords, _, _ = glyf[name].getCoordinates(glyf)
        if coords and isinstance(coords[0], tuple):     # 元组列表 [(x,y),...]
            xs = [p[0] for p in coords]
            ys = [p[1] for p in coords]
        elif coords and hasattr(coords[0], 'real'):     # complex 数组
            xs = [p.real for p in coords]
            ys = [p.imag for p in coords]
        else:                                           # 扁平 array [x0,y0,x1,y1,...]
            xs, ys = coords[0::2], coords[1::2]
        print(f'U+{cp:04X} {ch}: x[{min(xs)}..{max(xs)}] y[{min(ys)}..{max(ys)}]')


if __name__ == '__main__':
    main()
