#!/usr/bin/env python3
"""
check_font_coverage.py — 检查内置字体在游戏常用字符区的覆盖情况。

用途：排查"安卓端字符画 / 滑动条错位"类字体问题——确认 IPAGothic（EmueraMonoJP）
哪些字符缺失、会逐字形回退到系统字体（宽度不可控），需要由 EmueraBlock 补齐；
并对照本机 MS Gothic（如有）的 advance width，验证宽度预期（滑动条 ◢◣ 场景）。

检查区段（与 C# TerminalDisplayWidth.IsWideChar 的分组一致）：
  Box Drawing     U+2500–257F  游戏设计全角
  Block Elements  U+2580–259F  游戏设计半角
  Geometric       U+25A0–25FF  游戏设计全角（◢◣◤◥ U+25E2–25E5 在此区）
  Misc Symbols    U+2600–26FF  游戏设计全角

用法：python scripts/check_font_coverage.py
依赖：fontTools（读 woff2 还需 brotli）——缺失时按提示安装即可。

输出：
  1. IPAGothic 缺失字符列表（这些字符是"遗漏"候选，EmueraBlock 应补齐）
  2. MS Gothic（本机 C:\\Windows\\Fonts\\msgothic.ttc，可选）中 ◢◣◤◥ 与 ● 的
     advance width——实测 ◢◣◤◥ = 0.5em（半角）而 ● = 1.0em（全角），
     EmueraBlock 应逐字符复刻该实测 advance（滑动条 ◢◣ 按 1024 补齐）。
"""
from __future__ import annotations

import os

from fontTools.ttLib import TTFont

FONT = os.path.join(os.path.dirname(__file__), '..', 'src', 'assets', 'fonts', 'IPAGothic.woff2')
MSGOTHIC = r'C:\Windows\Fonts\msgothic.ttc'

# (组名, 起始码点, 结束码点)——与 IsWideChar 分组一致
GROUPS = [
    ('Box Drawing', 0x2500, 0x257F),
    ('Block Elements', 0x2580, 0x259F),
    ('Geometric', 0x25A0, 0x25FF),
    ('Misc Symbols', 0x2600, 0x26FF),
]

# 滑动条场景代表字符：(码点, 字符)
TRI = [(0x25E2, '\u25E2'), (0x25E3, '\u25E3'), (0x25E4, '\u25E4'), (0x25E5, '\u25E5')]
DOT = (0x25CF, '\u25CF')  # ● 参照（MS Gothic 中已知全角）


def report_coverage(font_path: str, label: str) -> None:
    try:
        font = TTFont(font_path)
    except Exception as e:  # noqa: BLE001
        print(f'== {label}: 读取失败——{e}')
        print('   提示：读 woff2 需要 fontTools + brotli，可先执行 '
              '`pip install fonttools brotli` 后重试。')
        return
    cmap = font.getBestCmap()
    upm = font['head'].unitsPerEm
    print(f'== {label}: {font_path} (UPM={upm}) ==')
    for name, lo, hi in GROUPS:
        missing = [cp for cp in range(lo, hi + 1) if cp not in cmap]
        total = hi - lo + 1
        print(f'- {name} U+{lo:04X}..U+{hi:04X}: 有 {total - len(missing)}/{total}，缺失 {len(missing)}')
        if missing:
            shown = ' '.join(f'U+{cp:04X}' for cp in missing[:30])
            print(f'    缺失: {shown}' + (' ...' if len(missing) > 30 else ''))


def report_advance(font_path: str, label: str, font_number: int | None = None) -> None:
    try:
        font = TTFont(font_path, fontNumber=font_number) if font_number is not None else TTFont(font_path)
    except Exception as e:  # noqa: BLE001
        print(f'== {label}: 读取失败 {e}')
        return
    upm = font['head'].unitsPerEm
    cmap = font.getBestCmap()
    hmtx = font['hmtx'].metrics
    print(f'== {label}: {font_path} (fontNumber={font_number}, UPM={upm}) ==')
    for cp, ch in TRI + [DOT]:
        gname = cmap.get(cp)
        if gname is None or gname not in hmtx:
            print(f'  U+{cp:04X} {ch}: 缺失')
            continue
        adv = hmtx[gname][0]
        print(f'  U+{cp:04X} {ch}: advance={adv} = {adv / upm:.3f}em'
              f'（{"全角 1.0em" if abs(adv - upm) < 1 else "半角 0.5em" if abs(adv * 2 - upm) < 1 else "其他"}）')


def main() -> None:
    if not os.path.exists(FONT):
        print(f'未找到 IPAGothic: {FONT}')
        return
    report_coverage(FONT, 'IPAGothic（EmueraMonoJP）')
    if os.path.exists(MSGOTHIC):
        print()
        report_advance(MSGOTHIC, 'MS Gothic', font_number=0)
    else:
        print('\n未找到本机 MS Gothic（msgothic.ttc），跳过 advance 对照。')


if __name__ == '__main__':
    main()
