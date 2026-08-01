#!/usr/bin/env python3
"""
build_emblock_font.py — 生成 EmueraBlock 补充字体（woff2）。

IPAGothic（EmueraMonoJP）缺失两类字符，本脚本补齐并入库：
  1. Block Elements（U+2580–U+259F，░▒▓█▀▄▌▐ 等）：游戏设计为半角（0.5em），
     与 C# TerminalDisplayWidth.IsWideChar 的"Block Elements 半角"判定一致。
  2. 双线框（U+2550–U+256C，═║╔╗╚╝╠╣╦╩╬）：全角（1.0em）。

字形全部程序化绘制（矩形 / 阴影点阵 / 双线框条对），无外部字体源依赖，可复现。
线宽 82（对齐 IPAGothic 单线框 ─│ 的实测厚度），横条对以 778（IPAGothic 单线中心）
为中心、竖条对以 1024 为中心——与主字体单线框混排时位置一致。

用法：python scripts/build_emblock_font.py
输出：src/assets/fonts/EmueraBlock.woff2
"""
from __future__ import annotations

import os
from fontTools.fontBuilder import FontBuilder
from fontTools.pens.ttGlyphPen import TTGlyphPen

UPM = 2048
# 单元竖跨：与 IPAGothic 框线一致（-246 下过冲 / 1802 上界，跨度正好 2048）
Y_LO, Y_HI, YC = -246, 1802, 778
XC = 1024
# 双线框几何：线宽 t、线间 gap g
T, G = 82, 82

OUT = os.path.join(os.path.dirname(__file__), '..', 'src', 'assets', 'fonts', 'EmueraBlock.woff2')


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


# ---------- Block Elements（半角，宽 1024） ----------
BLOCK_ADV = UPM // 2  # 1024

# 下 n/8 块（▁..▇█），U+2581–U+2588，n=1..8
block_glyphs = {}
for n in range(1, 9):
    cp = 0x2580 + n  # 2581..2588
    block_glyphs[cp] = (BLOCK_ADV, [(0, Y_LO, BLOCK_ADV, Y_LO + 256 * n)])
# 上 1/8 ▔ U+2594
block_glyphs[0x2594] = (BLOCK_ADV, [(0, Y_HI - 256, BLOCK_ADV, Y_HI)])
# 上/下半块 ▀ U+2580 / ▄ U+2584
block_glyphs[0x2580] = (BLOCK_ADV, [(0, YC, BLOCK_ADV, Y_HI)])
block_glyphs[0x2584] = (BLOCK_ADV, [(0, Y_LO, BLOCK_ADV, YC)])
# 左 n/8 块（▏..▉），U+258F..U+2589，n=1..7
for n in range(1, 8):
    cp = 0x258F - (n - 1)  # 258F..2589
    block_glyphs[cp] = (BLOCK_ADV, [(0, Y_LO, 128 * n, Y_HI)])
# 右半 ▐ U+2590 / 右 1/8 ▕ U+2595
block_glyphs[0x2590] = (BLOCK_ADV, [(BLOCK_ADV // 2, Y_LO, BLOCK_ADV, Y_HI)])
block_glyphs[0x2595] = (BLOCK_ADV, [(BLOCK_ADV - 128, Y_LO, BLOCK_ADV, Y_HI)])


def shade_rects(keep):
    """8 列 × 16 行的 128×128 小方块网格，按 keep(r,c) 决定是否填充。"""
    rects = []
    for r in range(16):
        for c in range(8):
            if keep(r, c):
                x0 = 128 * c
                y0 = Y_LO + 128 * r
                rects.append((x0, y0, x0 + 128, y0 + 128))
    return rects


# ░ 25%（偶数行偶数列点阵） / ▒ 50%（棋盘格） / ▓ 75%（░ 的补）
block_glyphs[0x2591] = (BLOCK_ADV, shade_rects(lambda r, c: r % 2 == 0 and c % 2 == 0))
block_glyphs[0x2592] = (BLOCK_ADV, shade_rects(lambda r, c: (r + c) % 2 == 0))
block_glyphs[0x2593] = (BLOCK_ADV, shade_rects(lambda r, c: not (r % 2 == 0 and c % 2 == 0)))

# 象限（U+2596–U+259F）：UL / UR / LL / LR
UL, UR = (0, YC, BLOCK_ADV // 2, Y_HI), (BLOCK_ADV // 2, YC, BLOCK_ADV, Y_HI)
LL, LR = (0, Y_LO, BLOCK_ADV // 2, YC), (BLOCK_ADV // 2, Y_LO, BLOCK_ADV, YC)
quads = {
    0x2596: [LL],
    0x2597: [LR],
    0x2598: [UL],
    0x2599: [UL, LL, LR],
    0x259A: [UL, LR],
    0x259B: [UL, UR, LL],
    0x259C: [UL, UR, LR],
    0x259D: [UR],
    0x259E: [UR, LL],
    0x259F: [LR, LL, UL],
}
for cp, rects in quads.items():
    block_glyphs[cp] = (BLOCK_ADV, rects)

# ---------- 双线框（全角，宽 2048） ----------
BOX_ADV = UPM  # 2048
# 横条对（上 / 下），竖条对（左 / 右）——以单线中心（778 / 1024）对称
H1 = (0, YC - (T + G), BOX_ADV, YC - G)
H2 = (0, YC + G, BOX_ADV, YC + G + T)
V1 = (XC - (T + G), Y_LO, XC - G, Y_HI)
V2 = (XC + G, Y_LO, XC + G + T, Y_HI)
V_PAIR = [V1, V2]
H_FULL = [H1, H2]
H_RIGHT = [(XC, H1[1], BOX_ADV, H1[3]), (XC, H2[1], BOX_ADV, H2[3])]
H_LEFT = [(0, H1[1], XC, H1[3]), (0, H2[1], XC, H2[3])]

box_glyphs = {
    0x2550: H_FULL,            # ═ 双横
    0x2551: V_PAIR,            # ║ 双竖
    0x2554: V_PAIR + H_RIGHT,  # ╔
    0x2557: V_PAIR + H_LEFT,   # ╗
    0x255A: V_PAIR + H_RIGHT,  # ╚
    0x255D: V_PAIR + H_LEFT,   # ╝
    0x2560: V_PAIR + H_RIGHT,  # ╠
    0x2563: V_PAIR + H_LEFT,   # ╣
    0x2566: V_PAIR + H_FULL,   # ╦
    0x2569: V_PAIR + H_FULL,   # ╩
    0x256C: V_PAIR + H_FULL,   # ╬
}

# ---------- 组装 ----------
all_glyphs = {}
glyph_order = ['.notdef']
cmap = {}
metrics = {}
for cp, (adv, rects) in block_glyphs.items():
    name = f'block_{cp:04X}'
    all_glyphs[name] = rects_glyph(rects)
    glyph_order.append(name)
    cmap[cp] = name
    xs = [r[0] for r in rects]
    metrics[name] = (adv, min(xs))
for cp, rects in box_glyphs.items():
    name = f'box_{cp:04X}'
    all_glyphs[name] = rects_glyph(rects)
    glyph_order.append(name)
    cmap[cp] = name
    xs = [r[0] for r in rects]
    metrics[name] = (BOX_ADV, min(xs))
metrics['.notdef'] = (UPM, 0)
all_glyphs['.notdef'] = rects_glyph([(0, 0, 2048, 1802)])

fb = FontBuilder(UPM)
fb.setupGlyphOrder(glyph_order)
fb.setupCharacterMap(cmap)
fb.setupGlyf(all_glyphs)
fb.setupHorizontalMetrics(metrics)
fb.setupHorizontalHeader(ascent=Y_HI, descent=Y_LO, lineGap=0)
fb.setupHead(unitsPerEm=UPM, created=0, modified=0)
fb.setupOS2(sTypoAscender=Y_HI, sTypoDescender=Y_LO, usWinAscent=1802, usWinDescent=401)
fb.setupNameTable({
    'familyName': 'EmueraBlock',
    'styleName': 'Regular',
    'uniqueFontIdentifier': 'EmueraBlock 1.0',
    'fullName': 'EmueraBlock',
    'psName': 'EmueraBlock',
})
fb.setupPost()
fb.setupMaxp()

os.makedirs(os.path.dirname(OUT), exist_ok=True)
fb.font.flavor = 'woff2'
fb.save(OUT)
print(f'wrote {OUT} ({os.path.getsize(OUT)} bytes)')
print(f'glyphs: {len(glyph_order) - 1}')
