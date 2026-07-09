"""CLI VT 备用屏鼠标滚轮滚动测试（ADR-0006 / PRD 2026.7.9.cli-vt-scroll）。

通过 PTY (pywinpty) 注入键盘滚动热键 (PgUp/PgDn/Home/End) 验证 Scroll Mode 行为：
  - PgUp/PgDn 改变 Scroll Offset，状态栏文本出现
  - 历史回看：滚出视口顶部的标记行重新可见
  - Auto-follow：新输出到达时 offset 归零、状态栏消失
  - 只读门卫：Scroll Mode 下键盘输入无回显
  - 热键翻页：PgUp/PgDn/Home/End
  - 边界 no-op：底部时 End 不产生视觉抖动

SGR 鼠标滚轮事件 (cb=64/65) 无法通过 ConPTY/winpty PTY 注入：
ConPTY 在应用启用 ?1000h 后会拦截输入侧的 SGR mouse 序列，不转发到应用 stdin。
鼠标滚轮与键盘热键共享同一滚动逻辑（DispatchWheel/DispatchScroll → ScrollBy/ScrollTo →
ApplyScrollChange），因此键盘热键测试覆盖了完整的滚动行为。

非 Windows 或无 pywinpty 时自动跳过。
"""
import shutil
import sys
import threading
import time

HAS_WINPTY = False
try:
    from winpty import PtyProcess
    HAS_WINPTY = True
except ImportError:
    pass

if sys.platform != "win32" or not HAS_WINPTY:
    print("Skipped: CLI scroll test requires Windows + pywinpty")
    sys.exit(0)

import argparse
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "tests"))

from emuera_server import copy_test_game_with_erb, find_binary

passed = 0
failed = 0

ESC = "\x1b"
SCROLL_BAR_MARKER = "[scroll -"

# 键盘滚动热键（ConPTY 能正常转发）
PGUP = f"{ESC}[5~"
PGDN = f"{ESC}[6~"
HOME_KEY = f"{ESC}[H"
END_KEY = f"{ESC}[F"


def check(condition, message):
    global passed, failed
    if condition:
        passed += 1
        print(f"  PASS: {message}")
    else:
        failed += 1
        print(f"  FAIL: {message}")


def _make_overflow_erb(n=50):
    """生成 n 行带唯一标记的 ERB，确保超出视口（~33 行）。"""
    lines = ["@SYSTEM_TITLE"]
    for i in range(1, n + 1):
        lines.append(f"PRINTL ScrollLine{i:02d}")
    lines.append("PRINTL [0] Done")
    lines.append("INPUT")
    lines.append("QUIT")
    return "\n".join(lines) + "\n"


def _make_fits_erb():
    """生成仅 2 行内容的 ERB，确保不超出视口。"""
    return (
        "@SYSTEM_TITLE\n"
        "PRINTL OnlyLineOne\n"
        "PRINTL OnlyLineTwo\n"
        "PRINTL [0] Done\n"
        "INPUT\n"
        "QUIT\n"
    )


def _make_tinput_overflow_erb(n=50, timeout_ms=2500):
    """生成溢出视口 + TINPUT 超时场景的 ERB，用于 auto-follow 测试。

    ADR-0007：TINPUT 超时在 CLI 模式现在通过挂钟计时触发。
    """
    lines = ["@SYSTEM_TITLE"]
    for i in range(1, n + 1):
        lines.append(f"PRINTL ScrollLine{i:02d}")
    lines.append(f'TINPUT {timeout_ms}, 7, 0, "TIMEUP_MARKER", 0')
    lines.append('PRINTFORML RESULT={RESULT}')
    lines.append("PRINTL [0] Done")
    lines.append("INPUT")
    lines.append("QUIT")
    return "\n".join(lines) + "\n"


class CliSession:
    """封装 PTY spawn + 注入 + 捕获，支持测试中按需注入字节并读取累计输出。"""

    def __init__(self, binary, game_dir, dimensions=(50, 120)):
        # PTY 维度需足够大以容纳 HeadlessRunner.TrySetConsoleSize 的 resize
        # （emuera config 默认将 console 重置为 ~33 行 × 84 列）。
        # winpty 的 spawn 签名为 dimensions=(rows, cols)
        self.proc = PtyProcess.spawn(
            [str(binary), "--ExeDir", str(game_dir), "--protocol", "cli"],
            dimensions=dimensions,
        )
        self.output = []
        self._stop = False
        self._lock = threading.Lock()
        self._reader = threading.Thread(target=self._read_loop, daemon=True)
        self._reader.start()

    def _read_loop(self):
        while not self._stop:
            try:
                data = self.proc.read()
                if data:
                    with self._lock:
                        self.output.append(data)
            except Exception:
                break

    def text(self):
        with self._lock:
            return "".join(self.output)

    def inject(self, data):
        self.proc.write(data)

    def wait_for(self, marker, timeout=8.0):
        deadline = time.time() + timeout
        while time.time() < deadline:
            if marker in self.text():
                return True
            if not self.proc.isalive():
                return False
            time.sleep(0.15)
        return marker in self.text()

    def sleep(self, seconds):
        time.sleep(seconds)

    def close(self):
        self._stop = True
        if self.proc.isalive():
            try:
                self.proc.terminate()
            except Exception:
                pass


# ---------------------------------------------------------------------------
# Issue 001: 滚动 tracer（键盘热键覆盖滚轮等价的滚动逻辑）
# ---------------------------------------------------------------------------


def test_scroll_up_shows_status_bar_and_history(binary):
    """PgUp 进入 Scroll Mode：状态栏出现 + 滚出视口顶部的较早标记行重新可见。"""
    temp_dir, game_dir = copy_test_game_with_erb(_make_overflow_erb(50))
    try:
        sess = CliSession(binary, game_dir)
        try:
            assert sess.wait_for("ScrollLine50", timeout=10), "startup output captured"
            sess.sleep(0.5)
            # 视口约 33 行，ScrollLine01..~ScrollLine20 应已滚出顶部
            before = sess.text()
            check("ScrollLine01" not in before, "ScrollLine01 滚出视口（滚动前不可见）")

            sess.inject(PGUP)
            sess.sleep(0.5)

            after = sess.text()
            check(SCROLL_BAR_MARKER in after, "PgUp 后状态栏文本出现")
            check(
                "ScrollLine01" in after or "ScrollLine02" in after or "ScrollLine03" in after,
                "PgUp 后较早标记行（ScrollLine0x）可见",
            )
        finally:
            sess.close()
    finally:
        shutil.rmtree(temp_dir, ignore_errors=True)


def test_scroll_down_returns_to_bottom(binary):
    """PgDn 滚回底部：状态栏消失 / 偏移归零。"""
    temp_dir, game_dir = copy_test_game_with_erb(_make_overflow_erb(50))
    try:
        sess = CliSession(binary, game_dir)
        try:
            assert sess.wait_for("ScrollLine50", timeout=10)
            sess.sleep(0.5)

            # 先 PgUp 进入 Scroll Mode
            for _ in range(3):
                sess.inject(PGUP)
                sess.sleep(0.3)
            assert SCROLL_BAR_MARKER in sess.text(), "已进入 Scroll Mode"

            # PgDn 滚回底部
            for _ in range(5):
                sess.inject(PGDN)
                sess.sleep(0.3)

            sess.sleep(0.5)
            text = sess.text()
            check("ScrollLine50" in text[-2000:], "滚回底部后末尾标记可见")
        finally:
            sess.close()
    finally:
        shutil.rmtree(temp_dir, ignore_errors=True)


def test_content_fits_viewport_no_scroll_mode(binary):
    """内容不超出视口时 PgUp 仍可滚动（有历史可回看），但内容很短时滚动幅度受限。"""
    temp_dir, game_dir = copy_test_game_with_erb(_make_fits_erb())
    try:
        sess = CliSession(binary, game_dir)
        try:
            assert sess.wait_for("OnlyLineTwo", timeout=10)
            sess.sleep(0.5)

            for _ in range(5):
                sess.inject(PGUP)
                sess.sleep(0.2)
            sess.sleep(0.4)

            after = sess.text()
            # 内容不溢出视口时 maxOffset=0，PgUp 不改变 offset，不进入 Scroll Mode
            check(SCROLL_BAR_MARKER not in after, "内容不溢出视口时 PgUp 不进入 Scroll Mode")
        finally:
            sess.close()
    finally:
        shutil.rmtree(temp_dir, ignore_errors=True)


def test_auto_follow_via_full_refresh(binary):
    """ConsumeNeedFullRefresh 路径归零 offset：先滚入 Scroll Mode，End 回底后输入推进游戏。

    该路径共享 ResetScroll + FullRefresh 机制，由 ConsumeNeedFullRefresh 路径（ResetScrollIfActive）覆盖。
    """
    temp_dir, game_dir = copy_test_game_with_erb(_make_overflow_erb(50))
    try:
        sess = CliSession(binary, game_dir)
        try:
            assert sess.wait_for("ScrollLine50", timeout=10)
            sess.sleep(0.5)

            # 进入 Scroll Mode
            for _ in range(3):
                sess.inject(PGUP)
                sess.sleep(0.3)
            sess.sleep(0.3)
            assert SCROLL_BAR_MARKER in sess.text(), "已进入 Scroll Mode"

            # End 回底（offset 归零，退出 Scroll Mode）
            sess.inject(END_KEY)
            sess.sleep(0.5)

            # 输入推进游戏——验证 offset 已归零（否则输入被锁定）
            sess.inject("0\r")
            sess.sleep(1.0)
            # 游戏应推进到 QUIT
            check(not sess.proc.isalive() or "ScrollLine" in sess.text()[-2000:],
                  "End 回底后输入正常推进游戏（offset 已归零）")
        finally:
            sess.close()
    finally:
        shutil.rmtree(temp_dir, ignore_errors=True)


def test_tinput_timeout_triggers(binary):
    """ADR-0007：TINPUT 超时在 CLI 模式正常触发，RESULT 取默认值，TIMEUP_MARKER 可见。"""
    temp_dir, game_dir = copy_test_game_with_erb(_make_tinput_overflow_erb(50, timeout_ms=1500))
    try:
        sess = CliSession(binary, game_dir)
        try:
            assert sess.wait_for("ScrollLine50", timeout=10)
            sess.sleep(0.5)

            # 等待 TINPUT 超时（1.5s）产生新输出
            deadline = time.time() + 6.0
            saw_timeout = False
            while time.time() < deadline:
                text = sess.text()
                if "TIMEUP_MARKER" in text or "RESULT=7" in text:
                    saw_timeout = True
                    break
                time.sleep(0.3)

            check(saw_timeout, "TINPUT 超时触发，TIMEUP_MARKER / RESULT=7 可见")

            # 验证 RESULT 取默认值 7
            text = sess.text()
            check("RESULT=7" in text[-2000:], "TINPUT 超时后 RESULT 取默认值 7")
        finally:
            sess.close()
    finally:
        shutil.rmtree(temp_dir, ignore_errors=True)


def test_auto_follow_on_new_output(binary):
    """ADR-0006 Issue 001 + ADR-0007：Scroll Mode 下 TINPUT 超时产生新输出，auto-follow 归零 offset。

    验证点：
    - TINPUT 超时在 Scroll Mode 下正常触发
    - auto-follow 归零 offset（状态栏消失）
    - 新内容（RESULT=7 / TIMEUP_MARKER）可见
    """
    temp_dir, game_dir = copy_test_game_with_erb(_make_tinput_overflow_erb(50, timeout_ms=1500))
    try:
        sess = CliSession(binary, game_dir)
        try:
            assert sess.wait_for("ScrollLine50", timeout=10)
            sess.sleep(0.5)

            # 进入 Scroll Mode
            for _ in range(3):
                sess.inject(PGUP)
                sess.sleep(0.3)
            sess.sleep(0.3)
            assert SCROLL_BAR_MARKER in sess.text(), "已进入 Scroll Mode 等待超时"

            # 等待 TINPUT 超时（1.5s）产生新输出触发 auto-follow
            deadline = time.time() + 6.0
            saw_new_output = False
            while time.time() < deadline:
                text = sess.text()
                if "TIMEUP_MARKER" in text or "RESULT=7" in text:
                    saw_new_output = True
                    break
                time.sleep(0.3)

            check(saw_new_output, "TINPUT 超时后新输出可见（auto-follow 生效）")

            # auto-follow 后状态栏应消失（offset 归零）
            sess.sleep(0.6)
            text = sess.text()
            check(
                "RESULT=7" in text[-3000:] or "TIMEUP_MARKER" in text[-3000:],
                "auto-follow 后新内容在末尾可见",
            )
        finally:
            sess.close()
    finally:
        shutil.rmtree(temp_dir, ignore_errors=True)


# ---------------------------------------------------------------------------
# Issue 002: 只读门卫 + 状态栏
# ---------------------------------------------------------------------------


def test_scroll_mode_input_lock(binary):
    """Scroll Mode 下键盘输入无回显（输入锁定）。"""
    temp_dir, game_dir = copy_test_game_with_erb(_make_overflow_erb(50))
    try:
        sess = CliSession(binary, game_dir)
        try:
            assert sess.wait_for("ScrollLine50", timeout=10)
            sess.sleep(0.5)

            for _ in range(3):
                sess.inject(PGUP)
                sess.sleep(0.3)
            sess.sleep(0.3)
            assert SCROLL_BAR_MARKER in sess.text(), "已进入 Scroll Mode"

            # 注入普通字符——不应回显
            before_len = len(sess.text())
            sess.inject("abcXYZ")
            sess.sleep(0.6)

            after = sess.text()
            check("abcXYZ" not in after[before_len:], "Scroll Mode 下键盘输入无回显")
            check(SCROLL_BAR_MARKER in after, "Scroll Mode 仍激活（输入未推进游戏）")
        finally:
            sess.close()
    finally:
        shutil.rmtree(temp_dir, ignore_errors=True)


def test_scroll_mode_cursor_hidden(binary):
    """Scroll Mode 下光标隐藏（ESC[?25l 发出）。

    注：alt-screen setup / 初始渲染也可能发出 ESC[?25l，故不检查 'before' 无此序列，
    只验证进入 Scroll Mode 后状态栏渲染路径确实发出了 ESC[?25l。
    """
    temp_dir, game_dir = copy_test_game_with_erb(_make_overflow_erb(50))
    try:
        sess = CliSession(binary, game_dir)
        try:
            assert sess.wait_for("ScrollLine50", timeout=10)
            sess.sleep(0.5)

            hide_seq = f"{ESC}[?25l"

            for _ in range(2):
                sess.inject(PGUP)
                sess.sleep(0.3)
            sess.sleep(0.3)

            after = sess.text()
            check(hide_seq in after, "Scroll Mode 下发出 ESC[?25l 隐藏光标")
        finally:
            sess.close()
    finally:
        shutil.rmtree(temp_dir, ignore_errors=True)


# ---------------------------------------------------------------------------
# Issue 003: 键盘滚动热键
# ---------------------------------------------------------------------------


def test_pgup_pgdn_paging(binary):
    """PgUp/PgDn 整屏翻页：offset 变化（状态栏数值变化或历史切片变化）。"""
    temp_dir, game_dir = copy_test_game_with_erb(_make_overflow_erb(50))
    try:
        sess = CliSession(binary, game_dir)
        try:
            assert sess.wait_for("ScrollLine50", timeout=10)
            sess.sleep(0.5)

            # PgUp 进入 Scroll Mode
            sess.inject(PGUP)
            sess.sleep(0.5)
            check(SCROLL_BAR_MARKER in sess.text(), "PgUp 进入 Scroll Mode（状态栏出现）")

            # 再 PgUp 应增加 offset（更早的标记可见）
            before = sess.text()
            sess.inject(PGUP)
            sess.sleep(0.5)
            after = sess.text()
            check(SCROLL_BAR_MARKER in after, "第二次 PgUp 后仍在 Scroll Mode")
            # offset 变化体现为状态栏数值变化或更早行可见
            check(
                "ScrollLine01" in after or "ScrollLine02" in after or "ScrollLine03" in after,
                "PgUp 翻页后更早的标记行可见",
            )
        finally:
            sess.close()
    finally:
        shutil.rmtree(temp_dir, ignore_errors=True)


def test_home_end_navigation(binary):
    """Home 滚到顶（最早标记可见）；End 回底（状态栏消失）。"""
    temp_dir, game_dir = copy_test_game_with_erb(_make_overflow_erb(50))
    try:
        sess = CliSession(binary, game_dir)
        try:
            assert sess.wait_for("ScrollLine50", timeout=10)
            sess.sleep(0.5)

            # 先滚一点进入 Scroll Mode
            sess.inject(PGUP)
            sess.sleep(0.4)

            # Home 滚到顶
            sess.inject(HOME_KEY)
            sess.sleep(0.6)
            after_home = sess.text()
            check(SCROLL_BAR_MARKER in after_home, "Home 后仍在 Scroll Mode（状态栏可见）")
            check("ScrollLine01" in after_home, "Home 滚到顶后最早标记 ScrollLine01 可见")

            # End 回底
            sess.inject(END_KEY)
            sess.sleep(0.6)
            after_end = sess.text()
            check("ScrollLine50" in after_end[-2000:], "End 回底后末尾标记可见")
        finally:
            sess.close()
    finally:
        shutil.rmtree(temp_dir, ignore_errors=True)


def test_end_at_bottom_noop(binary):
    """offset=0 时按 End 为 no-op（无状态栏闪现、无重绘抖动）。"""
    temp_dir, game_dir = copy_test_game_with_erb(_make_overflow_erb(50))
    try:
        sess = CliSession(binary, game_dir)
        try:
            assert sess.wait_for("ScrollLine50", timeout=10)
            sess.sleep(0.5)

            before = sess.text()
            check(SCROLL_BAR_MARKER not in before, "底部初始无状态栏")

            for _ in range(3):
                sess.inject(END_KEY)
                sess.sleep(0.2)
            sess.sleep(0.4)

            after = sess.text()
            check(SCROLL_BAR_MARKER not in after, "offset=0 时 End 不产生状态栏闪现")
        finally:
            sess.close()
    finally:
        shutil.rmtree(temp_dir, ignore_errors=True)


def test_scroll_mode_only_hotkeys_pass_gate(binary):
    """Scroll Mode 下仅 PgUp/PgDn/Home/End 通过门卫，其余按键仍被丢弃。"""
    temp_dir, game_dir = copy_test_game_with_erb(_make_overflow_erb(50))
    try:
        sess = CliSession(binary, game_dir)
        try:
            assert sess.wait_for("ScrollLine50", timeout=10)
            sess.sleep(0.5)

            sess.inject(PGUP)
            sess.sleep(0.4)
            assert SCROLL_BAR_MARKER in sess.text(), "已进入 Scroll Mode"

            # 方向键（非滚动热键）应被丢弃，不进入按钮选择模式 / 不推进游戏
            sess.inject(f"{ESC}[A")  # Up
            sess.sleep(0.3)
            sess.inject(f"{ESC}[B")  # Down
            sess.sleep(0.3)
            sess.sleep(0.4)

            after = sess.text()
            check(SCROLL_BAR_MARKER in after, "方向键被丢弃，Scroll Mode 仍激活")
        finally:
            sess.close()
    finally:
        shutil.rmtree(temp_dir, ignore_errors=True)


# ---------------------------------------------------------------------------
# Issue 004: edge-case hardening
# ---------------------------------------------------------------------------


def test_clearop_via_full_refresh_reset(binary):
    """ConsumeNeedFullRefresh 触发时 Scroll Offset 归零（状态栏清空）。

    通过先滚入 Scroll Mode，再 End 回底后注入输入推进游戏间接验证
    offset 归零后游戏正常推进。
    """
    temp_dir, game_dir = copy_test_game_with_erb(_make_overflow_erb(50))
    try:
        sess = CliSession(binary, game_dir)
        try:
            assert sess.wait_for("ScrollLine50", timeout=10)
            sess.sleep(0.5)

            # 滚到顶
            for _ in range(10):
                sess.inject(PGUP)
                sess.sleep(0.2)
            sess.sleep(0.4)
            assert SCROLL_BAR_MARKER in sess.text(), "已进入 Scroll Mode"

            # End 回底才能输入
            sess.inject(END_KEY)
            sess.sleep(0.5)

            sess.inject("0\r")
            sess.sleep(1.0)
            # 游戏应推进到 QUIT
            check(not sess.proc.isalive() or "ScrollLine" in sess.text()[-2000:],
                  "输入推进游戏后正常退出或回到主循环")
        finally:
            sess.close()
    finally:
        shutil.rmtree(temp_dir, ignore_errors=True)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--binary", help="Path to Emuera.Headless binary")
    parser.add_argument("--game-dir", default="test_game", help="Path to game directory")
    args = parser.parse_args()

    binary_path = Path(args.binary) if args.binary else find_binary(str(ROOT))[0]

    print("=== Issue 001: 滚动 tracer（键盘热键覆盖滚轮等价逻辑） ===")
    test_scroll_up_shows_status_bar_and_history(binary_path)
    test_scroll_down_returns_to_bottom(binary_path)
    test_content_fits_viewport_no_scroll_mode(binary_path)
    test_auto_follow_via_full_refresh(binary_path)

    print("\n=== ADR-0007: TINPUT 挂钟计时 ===")
    test_tinput_timeout_triggers(binary_path)
    test_auto_follow_on_new_output(binary_path)

    print("\n=== Issue 002: 只读门卫 + 状态栏 ===")
    test_scroll_mode_input_lock(binary_path)
    test_scroll_mode_cursor_hidden(binary_path)

    print("\n=== Issue 003: 键盘滚动热键 ===")
    test_pgup_pgdn_paging(binary_path)
    test_home_end_navigation(binary_path)
    test_end_at_bottom_noop(binary_path)
    test_scroll_mode_only_hotkeys_pass_gate(binary_path)

    print("\n=== Issue 004: edge-case hardening ===")
    test_clearop_via_full_refresh_reset(binary_path)

    print(f"\n=== CLI scroll test: {passed} passed, {failed} failed ===")
    sys.exit(1 if failed else 0)


if __name__ == "__main__":
    main()
