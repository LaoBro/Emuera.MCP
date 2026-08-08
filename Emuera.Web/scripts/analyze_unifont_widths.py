#!/usr/bin/env python3
"""
analyze_unifont_widths.py — 分析 Unifont 能否补充 IPAGothic 缺失字符。

思路：IPAGothic 缺失的字符在 Web 端会逐字形回退到系统字体（宽度随机），
是"字体遗漏"问题的根源。Unifont 字符覆盖极全（GPLv2+字体嵌入例外 / OFL 双许可，
可嵌入），但其字符宽度与 MS Gothic 并不一致（例如部分 Box Drawing 为半角）。
本脚本对 IPAGothic 缺失的全部字符（Box / Block / Geometric / Misc 四区）比较
MS Gothic 与 Unifont 的 advance width（em 归一化），输出"宽度一致 → 可提取"清单，
供 build 脚本从 Unifont 提取字形（Unifont 优先，程序化仅兜底未覆盖字符）。

用法：
  python scripts/analyze_unifont_widths.py <unifont.otf> [--json width_match.json]
  # unifont.otf 下载：https://unifoundry.com/pub/unifont/unifont-17.0.05/font-builds/unifont-17.0.05.otf

输出：
  1. 各区候选 / 宽度一致 / 不一致统计（一致 = 可从 Unifont 提取）
  2. 一致清单：码点、字符、MS 宽度、Unifont 宽度（em）
  3. 不一致原因分布（Unifont 半 vs MS 全 / Unifont 全 vs MS 半 / 其他）
  --json 时把一致清单写入文件（供 build 脚本读取）。
"""
from __future__ import annotations

import argparse
import json
import os
import sys

from fontTools.ttLib import TTFont

ROOT = os.path.dirname(os.path.dirname(__file__))
IPA = os.path.join(ROOT, 'src', 'assets', 'fonts', 'IPAGothic.woff2')
MSGOTHIC = r'C:\Windows\Fonts\msgothic.ttc'

# 与 IsWideChar 分组一致的候选区段（全部纳入比对：IPAGothic 缺失者候选）
GROUPS = [
    ('Box Drawing', 0x2500, 0x257F),
    ('Block Elements', 0x2580, 0x259F),
    ('Geometric', 0x25A0, 0x25FF),
    ('Misc Symbols', 0x2600, 0x26FF),
]

TOL = 0.02  # em 容差（Unifont UPM=4096，MS Gothic UPM=256，按 em 比较）


def advance_em(font: TTFont, cp: int) -> float | None:
    """返回字符的 advance（em 归一）；缺失返回 None。"""
    gname = font.getBestCmap().get(cp)
    if gname is None:
        return None
    hmtx = font['hmtx'].metrics
    if gname not in hmtx:
        return None
    return hmtx[gname][0] / font['head'].unitsPerEm


def width_label(em: float) -> str:
    if abs(em - 1.0) < TOL:
        return '全角 1.0em'
    if abs(em - 0.5) < TOL:
        return '半角 0.5em'
    return f'{em:.3f}em'


def main() -> None:
    ap = argparse.ArgumentParser(description='分析 Unifont 与 MS Gothic 宽度一致的可提取字符')
    ap.add_argument('unifont', help='Unifont otf 路径')
    ap.add_argument('--json', help='把一致清单写入 JSON 文件')
    args = ap.parse_args()

    if not os.path.exists(IPA):
        print(f'未找到 IPAGothic: {IPA}')
        sys.exit(1)
    if not os.path.exists(MSGOTHIC):
        print(f'未找到本机 MS Gothic: {MSGOTHIC}（需 Windows）')
        sys.exit(1)

    ipa = TTFont(IPA)
    ipa_cmap = ipa.getBestCmap()
    ms = TTFont(MSGOTHIC, fontNumber=0)
    uni = TTFont(args.unifont)
    uni_upm = uni['head'].unitsPerEm
    print(f'Unifont: {args.unifont} (UPM={uni_upm})')

    matched, skipped = [], []
    for name, lo, hi in GROUPS:
        cands = [cp for cp in range(lo, hi + 1) if cp not in ipa_cmap]
        if not cands:
            print(f'\n[{name}] 候选 0（IPAGothic 已覆盖）')
            continue
        group_m, group_s = [], []
        for cp in cands:
            ms_em = advance_em(ms, cp)
            uni_em = advance_em(uni, cp)
            if ms_em is None:
                group_s.append((cp, None, None, 'MS Gothic 缺失'))
            elif uni_em is None:
                group_s.append((cp, ms_em, None, 'Unifont 缺失'))
            elif abs(ms_em - uni_em) < TOL:
                group_m.append((cp, ms_em, uni_em, '一致'))
            else:
                reason = ('Unifont 半角 vs MS 全角' if abs(uni_em - 0.5) < TOL and abs(ms_em - 1.0) < TOL
                          else 'Unifont 全角 vs MS 半角' if abs(uni_em - 1.0) < TOL and abs(ms_em - 0.5) < TOL
                          else f'宽度不同 {width_label(uni_em)} vs {width_label(ms_em)}')
                group_s.append((cp, ms_em, uni_em, reason))
        matched.extend(group_m)
        skipped.extend(group_s)
        print(f'\n[{name}] 候选 {len(cands)}：一致(可提取) {len(group_m)} / 不一致 {len(group_s)}')

    print('\n== 可提取清单（MS 与 Unifont 宽度一致） ==')
    for cp, ms_em, uni_em, _ in sorted(matched):
        ch = chr(cp)
        print(f'  U+{cp:04X} {ch}  {width_label(ms_em)}  (MS {ms_em:.3f} = Uni {uni_em:.3f})')

    print('\n== 不一致原因分布 ==')
    from collections import Counter
    reasons = Counter(r for _, _, _, r in skipped)
    for reason, cnt in reasons.most_common():
        print(f'  {reason}: {cnt}')

    if args.json:
        data = {
            'matched': [{'cp': cp, 'em': round(ms_em, 4)} for cp, ms_em, _, _ in matched],
            'matched_count': len(matched),
            'unifont_upm': uni_upm,
        }
        with open(args.json, 'w', encoding='utf-8') as f:
            json.dump(data, f, ensure_ascii=False, indent=2)
        print(f'\n一致清单已写入 {args.json}（{len(matched)} 字符）')


if __name__ == '__main__':
    main()
