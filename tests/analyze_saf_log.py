#!/usr/bin/env python3
"""A1 SAF 取证日志分析——从 agent.log 提取 [saf] 行并聚合统计。

用法:
    python tests/analyze_saf_log.py <log_file>
 或:
    python tests/analyze_saf_log.py < log.txt      # stdin

输出: 总览判定 + 按操作/路径聚合 + 全局慢行 TOP + FAIL 行 + N+1 模式提示。
关键问题: 5s 卡顿是「单次慢(Provider 实现)」还是「次数多(N+1 调用模式)」——
  平均 ms 大 → 单次慢; 次数多 → 调用模式问题。
"""
import collections
import re
import sys

# AgentLog 每行带 "HH:mm:ss.fff " 时间戳前缀——用 .*? 让出前缀
# 注意：path 字段用 (\S+)——docId 含空格（文件名带空格）时解析会错位；存档文件名
# 常规无空格（global.sav/save00.sav），已知限制。
LINE_RE = re.compile(r"^.*?\[saf\] (\S+) (\S+) (.*?) ms=(\d+)( FAIL .*)?$")
# 行首时间戳（HH:mm:ss）——时间窗口过滤用
TS_RE = re.compile(r"^(\d{2}:\d{2}:\d{2})")


def parse_text(text: str, since: str | None = None, until: str | None = None):
    stats: dict = collections.defaultdict(
        lambda: {"n": 0, "sum_ms": 0, "max_ms": 0, "max_line": ""})
    per_path: dict = collections.defaultdict(
        lambda: {"n": 0, "sum_ms": 0})
    top: list = []
    fails: list = []
    total_n = total_ms = 0
    skipped = 0

    for raw in text.splitlines():
        line = raw.strip()
        # 时间窗口过滤（字符串比较，HH:MM:SS 字典序 = 时间序）
        if since or until:
            ts_m = TS_RE.match(line)
            if ts_m:
                ts = ts_m.group(1)
                if since and ts < since:
                    skipped += 1
                    continue
                if until and ts > until:
                    skipped += 1
                    continue
            else:
                skipped += 1  # 无时间戳的非 AgentLog 行（如程序输出），窗口模式下跳过
        m = LINE_RE.match(line)
        if not m:
            continue
        op, path_, detail, ms_s, fail = m.groups()
        ms = int(ms_s)
        s = stats[op]
        s["n"] += 1
        s["sum_ms"] += ms
        if ms > s["max_ms"]:  # 严格大于——记录「首个」达到最大耗时的行
            s["max_ms"] = ms
            s["max_line"] = line
        pp = per_path[path_]
        pp["n"] += 1
        pp["sum_ms"] += ms
        top.append((ms, line))
        total_n += 1
        total_ms += ms
        if fail:
            fails.append(line)
    return stats, per_path, top, fails, total_n, total_ms, skipped


def report(stats, per_path, top, fails, total_n, total_ms, since=None, until=None, skipped=0):
    out = []
    avg = total_ms / total_n if total_n else 0
    out.append("=== 总览 ===")
    if since or until:
        out.append(f"时间窗口: {since or '开头'} ~ {until or '结尾'}（窗口外跳过 {skipped} 行）")
    out.append(f"IPC 总数: {total_n}  总耗时: {total_ms} ms  平均: {avg:.1f} ms")
    if total_n:
        if avg >= 50:
            verdict = "单次慢主导: 平均 {:.0f}ms >= 50ms —— Provider 实现问题，适合 O1 缓存 + 观察 O4 预取".format(avg)
        elif total_n >= 30:
            verdict = f"次数多主导: {total_n} 次但平均 {avg:.1f}ms —— N+1 调用模式，O1 缓存直接吸收".format()
        else:
            verdict = "数量与单次都低 —— 卡顿可能不在 SAF 读路径，另查"
        out.append("判定: " + verdict)
    out.append("")

    out.append("=== 按操作聚合 (次数 / 总ms / 平均ms / 最大ms) ===")
    out.append(f"{'op':<20}{'次数':>6}{'总ms':>9}{'平均ms':>9}{'最大ms':>8}")
    for op, s in sorted(stats.items(), key=lambda kv: -kv[1]["sum_ms"]):
        n = s["n"]
        out.append(f"{op:<20}{n:>6}{s['sum_ms']:>9}{s['sum_ms']/n:>9.1f}{s['max_ms']:>8}")
    out.append("")

    out.append("=== 按路径聚合 TOP 12 (次数 / 总ms / 平均ms) ===")
    out.append(f"{'path':<45}{'次数':>6}{'总ms':>9}{'平均ms':>9}")
    for path_, p in sorted(per_path.items(), key=lambda kv: -kv[1]["sum_ms"])[:12]:
        out.append(f"{path_:<45}{p['n']:>6}{p['sum_ms']:>9}{p['sum_ms']/p['n']:>9.1f}")
    out.append("")

    out.append("=== 全局 TOP 15 慢行 ===")
    for ms, line in sorted(top, reverse=True)[:15]:
        out.append(f"{ms:>7d}ms  {line}")
    out.append("")

    out.append("=== N+1 检测: 同路径重复命中 TOP 10 ===")
    for path_, p in sorted(per_path.items(), key=lambda kv: -kv[1]["n"])[:10]:
        if p["n"] >= 3:
            out.append(f"  {path_}  x{p['n']} 次  ({p['sum_ms']}ms 总, 平均 {p['sum_ms']/p['n']:.1f}ms)")
    out.append("")

    if fails:
        out.append(f"=== FAIL 行 ({len(fails)}) ===")
        for line in fails[:20]:
            out.append("  " + line)
    else:
        out.append("FAIL 行: 无")
    return "\n".join(out)


def main():
    import argparse
    ap = argparse.ArgumentParser(description="A1 SAF 取证日志分析（聚合统计）")
    ap.add_argument("log_file", nargs="?", help="agent.log 文本文件路径；缺省从 stdin 读")
    ap.add_argument("--since", metavar="HH:MM:SS", help="只分析该时间之后的 [saf] 行（含）")
    ap.add_argument("--until", metavar="HH:MM:SS", help="只分析该时间之前的 [saf] 行（含）")
    args = ap.parse_args()

    if args.log_file:
        with open(args.log_file, encoding="utf-8", errors="replace") as f:
            text = f.read()
    else:
        text = sys.stdin.read()
    stats, per_path, top, fails, total_n, total_ms, skipped = parse_text(
        text, since=args.since, until=args.until)
    if total_n == 0:
        if args.since or args.until:
            print("窗口内未找到 [saf] 日志行——检查时间窗口（日志时间戳 HH:MM:SS）是否正确。")
        else:
            print("未找到 [saf] 日志行——确认已开启文件日志并执行过 SAF 操作（如进存档界面）。")
        return
    print(report(stats, per_path, top, fails, total_n, total_ms,
                 since=args.since, until=args.until, skipped=skipped))


if __name__ == "__main__":
    main()
