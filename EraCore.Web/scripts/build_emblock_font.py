#!/usr/bin/env python3
"""
build_emblock_font.py — 生成 EmueraBlock 补充字体（woff2）。

EmueraBlock 是 IPAGothic（EmueraMonoJP）缺失字形的补充字体，回退链：
游戏字体 → EmueraMonoJP → EmueraBlock → 系统字体。

字形来源单一（DejaVu Sans 全接管，无程序化）：
  DejaVu Sans 提取（需先下载并分析）：
  从 DejaVu Sans（Bitstream Vera 衍生，自由许可可嵌入，平滑矢量轮廓）
  提取 IPAGothic 缺失的四区符号（Box/Block/Geometric/Misc，265 字符，
  含 ◢◣◤◥ 三角与 Block 象限 U+2596–259F）。
  DejaVu 是比例字体（advance 各异），提取时以 analyze 脚本测得的
  **MS Gothic 实测 bbox** 为目标矩形做仿射缩放（源 bbox → 目标 bbox,
  居中），使字形视觉大小与 MS Gothic 完全一致；advance 强制 MS 实测值。
  MS 缺失的字符（Block 象限）以 DejaVu 自身 bbox 居中到半角格子。
  - 下载：https://github.com/dejavu-fonts/dejavu-fonts/releases/download/version_2_37/dejavu-fonts-ttf-2.37.zip
  - 分析：python scripts/analyze_dejavu_widths.py scripts/dejavu/DejaVuSans.ttf --json scripts/dejavu_width_match.json
  - 许可：Bitstream Vera Fonts 版权 + 自由许可（可嵌入、可再分发）

用法：python scripts/build_emblock_font.py [--dejavu <ttf>] [--dejavu-match <json>]
输出：src/assets/fonts/EmueraBlock.woff2
"""
from __future__ import annotations

import argparse
import json
import os
from array import array

from fontTools.fontBuilder import FontBuilder
from fontTools.pens.transformPen import TransformPen
from fontTools.pens.ttGlyphPen import TTGlyphPen
from fontTools.ttLib import TTFont
from fontTools.ttLib.tables._g_l_y_f import Glyph, GlyphCoordinates, ttProgram

UPM = 2048
# 单元竖跨：与 IPAGothic 框线一致（-246 下过冲 / 1802 上界，跨度正好 2048）
Y_LO, Y_HI, YC = -246, 1802, 778
XC = 1024
# 双线框几何：线宽 t、线间 gap g
T, G = 82, 82

SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(SCRIPT_DIR, '..', 'src', 'assets', 'fonts', 'EmueraBlock.woff2')


def rects_glyph(rects):
    """把一组 (x0,y0,x1,y1) 矩形画成一个 glyph（每个矩形一个闭合 contour）。"""
    pen = TTGlyphPen(None)
    for (x0, y0, x1, y1) in rects:
        pen.moveTo((x0, y0))
        pen.lineTo((x1, y0))
        pen.lineTo((x1, y1))
        pen.lineTo((x0, y1))
        pen.closePath()
    return pen.glyph()


def extract_dejavu(dejavu_path, match_path, glyphs, cmap, metrics, order):
    """从 DejaVu Sans 提取字形（平滑矢量轮廓），按 MS Gothic 实测 bbox 仿射缩放。

    DejaVu 是比例字体（advance 各异），字形画在各自宽度的格子里。提取时以
    analyze 脚本测得的 **MS Gothic 实测 bbox**（target 字段，2048 UPM 坐标）
    为目标矩形，把 DejaVu 源字形 bbox 仿射映射过去（缩放 + 平移居中），
    使字形视觉大小与 MS Gothic 完全一致；advance 强制 MS 实测值。
    ◢◣◤◥ 由程序化直边三角接管（排除，避免 DejaVu 自带三角尺寸不一致）。"""
    if not (os.path.exists(dejavu_path) and os.path.exists(match_path)):
        print('未找到 DejaVu Sans ttf / 匹配清单，跳过 DejaVu 提取（仅程序化兜底）')
        return 0

    dv = TTFont(dejavu_path)
    dv_cmap = dv.getBestCmap()
    glyf = dv['glyf']

    with open(match_path, encoding='utf-8') as f:
        items = json.load(f)['matched']

    count = 0
    for item in items:
        cp, em = item['cp'], item['em']
        if cp in cmap:
            continue
        gname = dv_cmap.get(cp)
        if gname is None or gname not in glyf:
            continue
        glyph = glyf[gname]
        # 空字形跳过（组合字形 contours=-1 由 getCoordinates 递归解析）
        if glyph.numberOfContours == 0 and not getattr(glyph, 'components', None):
            continue
        try:
            # getCoordinates 递归解析组合字形（▣☰☷ 等）为轮廓点
            coords, endpts, flags = glyph.getCoordinates(glyf)
            if len(coords) == 0:
                continue
            xs = [p.real if hasattr(p, 'real') else p[0] for p in coords]
            ys = [p.imag if hasattr(p, 'real') else p[1] for p in coords]
            # 目标 bbox（analyze 已按 MS 实测换算到 2048 UPM 坐标）
            tx0, ty0, tx1, ty1 = item['target']
            sx0, sx1 = min(xs), max(xs)
            sy0, sy1 = min(ys), max(ys)
            if sx1 - sx0 <= 0 or sy1 - sy0 <= 0 or tx1 - tx0 <= 0 or ty1 - ty0 <= 0:
                continue
            # 仿射：源 bbox → 目标 bbox（缩放 + 平移，中心自然对齐）
            sx = (tx1 - tx0) / (sx1 - sx0)
            sy = (ty1 - ty0) / (sy1 - sy0)
            dx = tx0 - sx0 * sx
            dy = ty0 - sy0 * sy
            # 直接用解析出的轮廓点构建简单字形（绕过 TTGlyphPen 组件问题）
            new_coords = GlyphCoordinates(
                (round(p.real * sx + dx) if hasattr(p, 'real') else round(p[0] * sx + dx),
                 round(p.imag * sy + dy) if hasattr(p, 'real') else round(p[1] * sy + dy))
                for p in coords
            )
            new_glyph = Glyph()
            new_glyph.coordinates = new_coords
            new_glyph.endPtsOfContours = list(endpts)
            new_glyph.flags = array('B', flags)
            new_glyph.numberOfContours = len(endpts)
            new_glyph.program = ttProgram.Program()
        except Exception as e:  # noqa: BLE001
            print(f'  跳过 U+{cp:04X}（{e}）')
            continue
        name = f'dv_{cp:04X}'
        glyphs[name] = new_glyph
        order.append(name)
        cmap[cp] = name
        metrics[name] = (round(em * UPM), round(dx))
        count += 1
    print(f'DejaVu Sans 提取：{count} 字符（按 MS Gothic 实测 bbox 仿射缩放）')
    return count


def main():
    ap = argparse.ArgumentParser(description='生成 EmueraBlock 补充字体')
    ap.add_argument('--dejavu', default=os.path.join(SCRIPT_DIR, 'dejavu', 'DejaVuSans.ttf'),
                    help='DejaVu Sans ttf 路径（默认 scripts/dejavu/DejaVuSans.ttf）')
    ap.add_argument('--dejavu-match', default=os.path.join(SCRIPT_DIR, 'dejavu_width_match.json'),
                    help='提取清单 json（默认 scripts/dejavu_width_match.json，由 analyze_dejavu_widths.py 生成）')
    args = ap.parse_args()

    # DejaVu 全接管：提取全部四区符号（含 ◢◣◤◥ 三角与 Block 象限），无程序化字形。
    glyphs, cmap, metrics, order = {}, {}, {}, ['.notdef']
    metrics['.notdef'] = (UPM, 0)
    glyphs['.notdef'] = rects_glyph([(0, 0, 2048, 1802)])
    n_dv = extract_dejavu(args.dejavu, args.dejavu_match, glyphs, cmap, metrics, order)

    fb = FontBuilder(UPM)
    fb.setupGlyphOrder(order)
    fb.setupCharacterMap(cmap)
    fb.setupGlyf(glyphs)
    fb.setupHorizontalMetrics(metrics)
    fb.setupHorizontalHeader(ascent=Y_HI, descent=Y_LO, lineGap=0)
    fb.setupHead(unitsPerEm=UPM, created=0, modified=0)
    fb.setupOS2(sTypoAscender=Y_HI, sTypoDescender=Y_LO, usWinAscent=1802, usWinDescent=401)
    fb.setupNameTable({
        'familyName': 'EmueraBlock',
        'styleName': 'Regular',
        'uniqueFontIdentifier': 'EmueraBlock 1.9',
        'fullName': 'EmueraBlock',
        'psName': 'EmueraBlock',
        'version': 'Version 1.9',
    })
    fb.setupPost()
    fb.setupMaxp()

    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    fb.font.flavor = 'woff2'
    fb.save(OUT)
    print(f'wrote {OUT} ({os.path.getsize(OUT)} bytes)')
    print(f'glyphs: {len(order) - 1}（DejaVu {n_dv}）')


if __name__ == '__main__':
    main()
