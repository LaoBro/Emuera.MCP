#!/usr/bin/env python3
"""
build_emblock_font.py — 生成 EmueraBlock 补充字体（woff2）。

EmueraBlock 是 IPAGothic（EmueraMonoJP）缺失字形的补充字体，回退链：
游戏字体 → EmueraMonoJP → EmueraBlock → 系统字体。

字形来源分两部分（Unifont 优先，程序化仅兜底）：
  1. Unifont 提取（可选，需先下载并分析）：
     从 GNU Unifont 提取"MS Gothic 实测 advance 一致"的字符，逐字形复刻
     MS Gothic 宽度（0.5em / 1.0em），覆盖 Box/Block/Geometric/Misc 四区中
     IPAGothic 缺失且宽度匹配的字符。
     - 下载：https://unifoundry.com/pub/unifont/unifont-17.0.05/font-builds/unifont-17.0.05.otf
     - 分析：python scripts/analyze_unifont_widths.py scripts/unifont-17.0.05.otf --json scripts/width_match.json
     - 许可：GNU Unifont 双许可（GPLv2+ 字体嵌入例外 / SIL OFL 1.1），可嵌入
     - 对齐：Unifont 字形按轮廓中心垂直平移到与 IPAGothic 一致的格子中心（778/2048）
  2. 程序化绘制（仅 Unifont 未覆盖的字符，无外部依赖，可复现）：
     - Block Elements（U+2580–U+259F）：半角 0.5em
     - 双线框（U+2550–U+256C 常用 11 个）：全角 1.0em
     - Geometric 三角（U+25E2–U+25E5）：半角 0.5em（MS Gothic 实测）

用法：python scripts/build_emblock_font.py [--unifont <otf>] [--match <json>]
输出：src/assets/fonts/EmueraBlock.woff2
"""
from __future__ import annotations

import argparse
import json
import os

from fontTools.fontBuilder import FontBuilder
from fontTools.pens.boundsPen import BoundsPen
from fontTools.pens.transformPen import TransformPen
from fontTools.pens.ttGlyphPen import TTGlyphPen
from fontTools.ttLib import TTFont

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


def poly_glyph(points):
    """按顶点序列 (x,y) 画一个闭合多边形（单个 contour）。"""
    pen = TTGlyphPen(None)
    pen.moveTo(points[0])
    for p in points[1:]:
        pen.lineTo(p)
    pen.closePath()
    return pen.glyph()


def build_fallback(glyphs, cmap, metrics, order):
    """程序化兜底字形：仅绘制 Unifont 未覆盖的字符（cp 已在 cmap 则跳过）。
    覆盖：Block Elements（半角）+ 双线框（全角）+ Geometric 三角（半角）。
    返回实际生成的程序化字形数。"""
    count = 0

    # ---------- Block Elements（半角，宽 1024） ----------
    adv = UPM // 2
    block = {}
    for n in range(1, 9):                      # ▁..▇█ U+2581–2588
        block[0x2580 + n] = [(0, Y_LO, adv, Y_LO + 256 * n)]
    block[0x2594] = [(0, Y_HI - 256, adv, Y_HI)]   # ▔
    block[0x2580] = [(0, YC, adv, Y_HI)]           # ▀
    block[0x2584] = [(0, Y_LO, adv, YC)]           # ▄
    for n in range(1, 8):                      # ▏..▉ U+258F–2589
        block[0x258F - (n - 1)] = [(0, Y_LO, 128 * n, Y_HI)]
    block[0x2590] = [(adv // 2, Y_LO, adv, Y_HI)]  # ▐
    block[0x2595] = [(adv - 128, Y_LO, adv, Y_HI)] # ▕

    def shade(keep):
        return [(128 * c, Y_LO + 128 * r, 128 * c + 128, Y_LO + 128 * r + 128)
                for r in range(16) for c in range(8) if keep(r, c)]

    block[0x2591] = shade(lambda r, c: r % 2 == 0 and c % 2 == 0)   # ░
    block[0x2592] = shade(lambda r, c: (r + c) % 2 == 0)            # ▒
    block[0x2593] = shade(lambda r, c: not (r % 2 == 0 and c % 2 == 0))  # ▓

    UL, UR = (0, YC, adv // 2, Y_HI), (adv // 2, YC, adv, Y_HI)
    LL, LR = (0, Y_LO, adv // 2, YC), (adv // 2, Y_LO, adv, YC)
    quads = {
        0x2596: [LL], 0x2597: [LR], 0x2598: [UL], 0x2599: [UL, LL, LR],
        0x259A: [UL, LR], 0x259B: [UL, UR, LL], 0x259C: [UL, UR, LR],
        0x259D: [UR], 0x259E: [UR, LL], 0x259F: [LR, LL, UL],
    }
    for cp, rects in quads.items():
        block[cp] = rects

    for cp, rects in block.items():
        if cp in cmap:
            continue
        name = f'block_{cp:04X}'
        glyphs[name] = rects_glyph(rects)
        order.append(name)
        cmap[cp] = name
        metrics[name] = (adv, 0)
        count += 1

    # ---------- 双线框（全角，宽 2048） ----------
    H1 = (0, YC - (T + G), UPM, YC - G)
    H2 = (0, YC + G, UPM, YC + G + T)
    V1 = (XC - (T + G), Y_LO, XC - G, Y_HI)
    V2 = (XC + G, Y_LO, XC + G + T, Y_HI)
    V_PAIR, H_FULL = [V1, V2], [H1, H2]
    H_RIGHT = [(XC, H1[1], UPM, H1[3]), (XC, H2[1], UPM, H2[3])]
    H_LEFT = [(0, H1[1], XC, H1[3]), (0, H2[1], XC, H2[3])]
    box = {
        0x2550: H_FULL, 0x2551: V_PAIR,
        0x2554: V_PAIR + H_RIGHT, 0x2557: V_PAIR + H_LEFT,
        0x255A: V_PAIR + H_RIGHT, 0x255D: V_PAIR + H_LEFT,
        0x2560: V_PAIR + H_RIGHT, 0x2563: V_PAIR + H_LEFT,
        0x2566: V_PAIR + H_FULL, 0x2569: V_PAIR + H_FULL, 0x256C: V_PAIR + H_FULL,
    }
    for cp, rects in box.items():
        if cp in cmap:
            continue
        name = f'box_{cp:04X}'
        glyphs[name] = rects_glyph(rects)
        order.append(name)
        cmap[cp] = name
        metrics[name] = (UPM, 0)
        count += 1

    # ---------- Geometric 三角（半角，宽 1024，MS Gothic 实测 0.5em） ----------
    geo_adv = UPM // 2
    BL, BR = (0, Y_LO), (geo_adv, Y_LO)
    TL, TR = (0, Y_HI), (geo_adv, Y_HI)
    geo = {
        0x25E3: [BL, TL, BR],  # ◣
        0x25E2: [BR, TR, BL],  # ◢
        0x25E4: [TL, BL, TR],  # ◤
        0x25E5: [TR, BR, TL],  # ◥
    }
    for cp, pts in geo.items():
        if cp in cmap:
            continue
        name = f'geo_{cp:04X}'
        glyphs[name] = poly_glyph(pts)
        order.append(name)
        cmap[cp] = name
        metrics[name] = (geo_adv, 0)
        count += 1

    return count


def extract_unifont(unifont_path, match_path, glyphs, cmap, metrics, order):
    """从 Unifont 提取宽度匹配清单中的字形（CFF → TrueType，垂直居中到格子中心）。"""
    if not (os.path.exists(unifont_path) and os.path.exists(match_path)):
        print('未找到 Unifont otf / 匹配清单，跳过 Unifont 提取（仅程序化兜底字形）')
        return 0

    uni = TTFont(unifont_path)
    uni_upm = uni['head'].unitsPerEm
    scale = UPM / uni_upm
    uni_cmap = uni.getBestCmap()
    hmtx = uni['hmtx'].metrics
    cff = uni['CFF '].cff
    top = cff[cff.fontNames[0]]
    charstrings = top.CharStrings

    with open(match_path, encoding='utf-8') as f:
        items = json.load(f)['matched']

    count = 0
    for item in items:
        cp, em = item['cp'], item['em']
        gname = uni_cmap.get(cp)
        if gname is None or gname not in charstrings:
            continue
        cs = charstrings[gname]
        try:
            # 注意：draw() 的 glyphSet 关键字参数在旧版 fontTools 不存在。
            # Unifont 符号区为简单轮廓（无 seac 组合），直接 draw(pen) 即可；
            # 组件由 TTGlyphPen/BoundsPen 构造时的 glyphSet 处理。
            bp = BoundsPen(charstrings)
            cs.draw(bp)
            if bp.bounds is None:
                continue
            _, ymin, _, ymax = bp.bounds
            # 关键：Unifont UPM=64，坐标必须放大到 2048（×scale）再垂直居中到
            # 格子中心 778。旧实现漏了 scale，字形只有原始尺寸（~3%），
            # advance 正确但字形几乎不可见（表现为"字符消失、宽度正确"）。
            dy = round(YC - (ymin + ymax) / 2 * scale)
            pen = TTGlyphPen(charstrings)
            tpen = TransformPen(pen, (scale, 0, 0, scale, 0, dy))
            cs.draw(tpen)
        except Exception as e:  # noqa: BLE001
            print(f'  跳过 U+{cp:04X}（{e}）')
            continue
        name = f'uni_{cp:04X}'
        glyphs[name] = pen.glyph()
        order.append(name)
        cmap[cp] = name
        adv = round(em * UPM)
        _, uni_lsb = hmtx.get(gname, (0, 0))
        metrics[name] = (adv, round(uni_lsb * scale))
        count += 1
    print(f'Unifont 提取：{count} 字符（advance 按 MS Gothic 实测 em 换算到 2048 UPM）')
    return count


def main():
    ap = argparse.ArgumentParser(description='生成 EmueraBlock 补充字体')
    ap.add_argument('--unifont', default=os.path.join(SCRIPT_DIR, 'unifont-17.0.05.otf'),
                    help='Unifont otf 路径（默认 scripts/unifont-17.0.05.otf）')
    ap.add_argument('--match', default=os.path.join(SCRIPT_DIR, 'width_match.json'),
                    help='宽度匹配清单 json（默认 scripts/width_match.json，由 analyze 脚本生成）')
    args = ap.parse_args()

    # Unifont 优先：先提取（宽度与 MS Gothic 一致的字符全部用 Unifont），
    # 程序化仅在 Unifont 未覆盖时兜底。
    glyphs, cmap, metrics, order = {}, {}, {}, ['.notdef']
    metrics['.notdef'] = (UPM, 0)
    glyphs['.notdef'] = rects_glyph([(0, 0, 2048, 1802)])
    n = extract_unifont(args.unifont, args.match, glyphs, cmap, metrics, order)
    p = build_fallback(glyphs, cmap, metrics, order)

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
        'uniqueFontIdentifier': 'EmueraBlock 1.5',
        'fullName': 'EmueraBlock',
        'psName': 'EmueraBlock',
        'version': 'Version 1.5',
    })
    fb.setupPost()
    fb.setupMaxp()

    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    fb.font.flavor = 'woff2'
    fb.save(OUT)
    print(f'wrote {OUT} ({os.path.getsize(OUT)} bytes)')
    print(f'glyphs: {len(order) - 1}（Unifont {n} + 程序化兜底 {p}）')


if __name__ == '__main__':
    main()
