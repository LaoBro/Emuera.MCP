#!/usr/bin/env python3
"""
analyze_dejavu_widths.py — 分析 DejaVu Sans 符号区覆盖，生成提取清单。

背景：EmueraBlock 中 Block/Geometric/Misc 区目前来自 GNU Unifont（位图转
矢量，斜边/曲线字符有阶梯锯齿）。DejaVu Sans（Bitstream Vera 衍生，自由
字体许可可嵌入）是平滑矢量字体，覆盖 Block 32/32、Geometric 96/96、
Misc 189/256。

DejaVu Sans 是**比例字体**（advance 各异，如 █ 0.769em、☺ 1.042em），
与 MS Gothic 的 0.5em/1.0em 混合宽度不同。本脚本以 **MS Gothic 实测
bbox（字形轮廓边界）为基准**输出清单：构建脚本提取字形时做
"DejaVu 源 bbox → MS 目标 bbox"的仿射缩放 + 中心对齐，使字形视觉大小
与 MS Gothic 完全一致（advance 也强制 MS 实测值）。

用法：
  python scripts/analyze_dejavu_widths.py scripts/dejavu/DejaVuSans.ttf [--json dejavu_width_match.json]
"""
from __future__ import annotations

import argparse
import json
import os

from fontTools.ttLib import TTFont

# 四区全覆盖：Box Drawing 也由 DejaVu 接管（平滑矢量框线，统一按 MS bbox 缩放）
GROUPS = [
    ('Box Drawing', 0x2500, 0x257F),
    ('Block Elements', 0x2580, 0x259F),
    ('Geometric Shapes', 0x25A0, 0x25FF),
    ('Misc Symbols', 0x2600, 0x26FF),
]

SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))

# MS Gothic 字体度量（fontNumber=0, UPM=256）:ascent=220, descent=-36
MS_ASCENT, MS_DESCENT = 220, -36


def glyph_bounds_em(font, gname):
    """返回字形 bbox（em 单位, 相对基线）:(x0,y0,x1,y1)；空字形返回 None。
    用 getCoordinates 递归解析（组合字形 ▗▘▚▝▞ 等也能正确取 bounds）。"""
    glyf = font['glyf']
    glyph = glyf[gname]
    if glyph.numberOfContours == 0 and not getattr(glyph, 'components', None):
        return None
    upm = font['head'].unitsPerEm
    try:
        coords, endpts, flags = glyph.getCoordinates(glyf)
        xs = [p.real if hasattr(p, 'real') else p[0] for p in coords]
        ys = [p.imag if hasattr(p, 'real') else p[1] for p in coords]
        if not xs:
            return None
        return (min(xs) / upm, min(ys) / upm, max(xs) / upm, max(ys) / upm)
    except Exception:
        return None


def main():
    ap = argparse.ArgumentParser(description='分析 DejaVu Sans 符号区覆盖（以 MS Gothic 实测 bbox 为基准）')
    ap.add_argument('dejavu', help='DejaVu Sans TTF 路径')
    ap.add_argument('--json', default=os.path.join(SCRIPT_DIR, 'dejavu_width_match.json'),
                    help='提取清单输出路径')
    args = ap.parse_args()

    dv = TTFont(args.dejavu)
    dv_upm = dv['head'].unitsPerEm
    dv_cmap = dv.getBestCmap()
    print(f'DejaVu Sans: {args.dejavu} (UPM={dv_upm}, 覆盖 {len(dv_cmap)} 字符)')

    ms = TTFont('C:/Windows/Fonts/msgothic.ttc', fontNumber=0)
    ms_upm = ms['head'].unitsPerEm
    ms_cmap = ms.getBestCmap()
    ms_hmtx = ms['hmtx'].metrics
    print(f'MS Gothic: msgothic.ttc (fontNumber=0, UPM={ms_upm}, ascent={MS_ASCENT} descent={MS_DESCENT})')

    # IPAGothic（EmueraMonoJP）已覆盖的字符不需要 EmueraBlock 补
    ipa = TTFont(os.path.join(SCRIPT_DIR, '..', 'src', 'assets', 'fonts', 'IPAGothic.woff2'))
    ipa_cmap = ipa.getBestCmap()

    items, ms_missing = [], []
    for name, lo, hi in GROUPS:
        cands = [cp for cp in range(lo, hi + 1)
                 if cp in dv_cmap and cp not in ipa_cmap]
        print(f'\n[{name}] DejaVu 覆盖 ∩ IPAGothic 缺失: {len(cands)}/{hi - lo + 1}')
        for cp in cands:
            gname = ms_cmap.get(cp)
            if gname is None:
                # MS 缺失：仅 Block 象限（U+2596–U+259F）用 DejaVu 自身 bbox 补
                # （无 MS 参考，居中到半角格子 1024，中心 512/778）
                if 0x2596 <= cp <= 0x259F:
                    dv_bbox = glyph_bounds_em(dv, dv_cmap[cp])
                    if dv_bbox is not None:
                        cx = (dv_bbox[0] + dv_bbox[2]) / 2
                        cy = (dv_bbox[1] + dv_bbox[3]) / 2
                        bw = dv_bbox[2] - dv_bbox[0]
                        bh = dv_bbox[3] - dv_bbox[1]
                        items.append({
                            'cp': cp,
                            'em': 0.5,
                            'target': [round((cx - bw / 2) * 2048),
                                       round(778 - bh / 2 * 2048),
                                       round((cx + bw / 2) * 2048),
                                       round(778 + bh / 2 * 2048)],
                        })
                ms_missing.append(cp)
                continue
            ms_adv = ms_hmtx.get(gname, (0,))[0] / ms_upm
            ms_bbox = glyph_bounds_em(ms, gname)
            if ms_bbox is None:
                ms_missing.append(cp)
                continue
            # 目标 bbox（units, 半角/全角格子 0..adv*2048; y 映射到 -246..1802）
            adv = round(ms_adv * 2048)
            x0 = round(ms_bbox[0] * 2048)
            x1 = round(ms_bbox[2] * 2048)
            y0 = round((ms_bbox[1] - MS_DESCENT / ms_upm) * 2048 - 246)
            y1 = round((ms_bbox[3] - MS_DESCENT / ms_upm) * 2048 - 246)
            items.append({
                'cp': cp,
                'em': ms_adv,
                'target': [x0, y0, x1, y1],   # 目标 bbox（2048 UPM 坐标）
            })

    print(f'\n== 提取清单（{len(items)} 字符）==')
    for it in sorted(items, key=lambda x: x['cp']):
        cp, t = it['cp'], it['target']
        kind = '全角' if it['em'] >= 0.999 else '半角'
        print(f'  U+{cp:04X} {chr(cp)}  {kind} adv={it["em"]:.3f}em → bbox x[{t[0]}..{t[2]}] y[{t[1]}..{t[3]}]')

    print(f'\n跳过：MS Gothic 缺失 {len(ms_missing)} 个：'
          + ' '.join(f'U+{cp:04X}' for cp in ms_missing))

    with open(args.json, 'w', encoding='utf-8') as f:
        json.dump({'source': 'DejaVu Sans', 'matched': items}, f, ensure_ascii=False, indent=2)
    print(f'\n提取清单已写入 {args.json}')


if __name__ == '__main__':
    main()
