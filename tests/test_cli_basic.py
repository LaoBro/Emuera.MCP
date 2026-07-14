"""Basic CLI mode test + ConPTY regression smoke tests for PRD-T6.

Tests: happy path, clearline, clear, merge, alignment, setbg.
Requires Windows 10 18309+ and pywinpty (pip install pywinpty).
Skipped automatically on non-Windows or when pywinpty is unavailable.
"""
import re
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
    print("Skipped: CLI test requires Windows + pywinpty")
    sys.exit(0)

import argparse
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "tests"))

from emuera_server import copy_test_game_with_erb, find_binary

passed = 0
failed = 0
warned = 0

ESC = "\x1b"

# 终端路径标志：从产品侧启动日志读取（见 AgentCliProtocol.RunCliLoop）
VT_PATH_MARKER = "[headless] 终端路径: VT"
FALLBACK_PATH_MARKER = "[headless] 终端路径: 降级"

ERB_HAPPY_PATH = """@SYSTEM_TITLE
PRINTL Agent Test Start
PRINTL [0] Hello
PRINTL [1] Quit
INPUT
IF RESULT == 0
	PRINTFORML You entered: {RESULT}
	PRINTL Select again:
	PRINTL [0] World
	PRINTL [1] Exit
	INPUT
	IF RESULT == 0
		PRINTL You entered: 0
	ELSE
		PRINTL You entered: 1
	ENDIF
ELSE
	PRINTFORML You entered: {RESULT}
ENDIF
PRINTL Agent Test End
QUIT
"""

ERB_CLEARLINE = """@SYSTEM_TITLE
PRINTL LINETOCLEAR_MARKER
CLEARLINE 1
PRINTL AfterClearedLine
PRINTL [0] Done
INPUT
QUIT
"""

ERB_STARTUP = """@SYSTEM_TITLE
PRINTL GameStartMarker
PRINTL [0] Done
INPUT
QUIT
"""

ERB_MERGE = """@SYSTEM_TITLE
PRINT Hello
PRINT World
INPUT
QUIT
"""

ERB_ALIGNMENT = """@SYSTEM_TITLE
PRINTC AlignCheck
INPUT
QUIT
"""

ERB_SETBG = """@SYSTEM_TITLE
SETBGCOLOR 0xFF0000
PRINTL RedBackground
PRINTL [0] Done
INPUT
QUIT
"""


def check(condition, message):
    global passed, failed
    if condition:
        passed += 1
        print(f"  PASS: {message}")
    else:
        failed += 1
        print(f"  FAIL: {message}")


def warn(message):
    """记录非致命警告：测试仍可继续，但提示环境可能不符预期。"""
    global warned
    warned += 1
    print(f"  WARN: {message}")


def detect_vt_path(text):
    """从捕获的 CLI 输出中探测终端路径。
    返回 (vt_active: bool, detected: bool)：
      - vt_active: True=VT 路径, False=降级路径
      - detected: False=未识别到路径标志（日志缺失或被截断）
    """
    if VT_PATH_MARKER in text:
        return (True, True)
    if FALLBACK_PATH_MARKER in text:
        return (False, True)
    return (False, False)


# --- Helpers ---


def _capture_cli_with_erb(binary, erb_text, timeout=12):
    """Copy test_game, inject ERB, run CLI, return captured text. Cleans up temp dir."""
    temp_dir, game_dir = copy_test_game_with_erb(erb_text)
    try:
        return _run_cli_and_capture(binary, Path(game_dir), timeout)
    finally:
        shutil.rmtree(temp_dir, ignore_errors=True)


def _run_cli_and_capture(binary, game_dir, timeout=12):
    """Spawn CLI, send '0\\r' to advance past INPUT, return all captured output text."""
    proc = PtyProcess.spawn([str(binary), "--ExeDir", str(game_dir), "--protocol", "cli"])
    output = []
    stop = False

    def reader():
        while not stop:
            try:
                data = proc.read()
                if data:
                    output.append(data)
            except Exception:
                break

    t = threading.Thread(target=reader, daemon=True)
    t.start()

    try:
        time.sleep(2.0)
        proc.write("0\r")
        deadline = time.time() + timeout
        while time.time() < deadline:
            if not proc.isalive():
                break
            time.sleep(0.3)
        return "".join(output)
    finally:
        stop = True
        if proc.isalive():
            proc.terminate()


# --- Existing happy path ---


def run_cli(binary, game_dir, timeout_per_phase=8):
    proc = PtyProcess.spawn([str(binary), "--ExeDir", str(game_dir), "--protocol", "cli"])

    output = []
    stop = False

    def reader():
        while not stop:
            try:
                data = proc.read()
                if data:
                    output.append(data)
            except Exception:
                break

    t = threading.Thread(target=reader, daemon=True)
    t.start()

    try:
        deadline = time.time() + timeout_per_phase
        while time.time() < deadline:
            if "[0] Hello" in "".join(output):
                break
            if not proc.isalive():
                break
            time.sleep(0.3)

        initial = "".join(output)
        check("Agent Test Start" in initial, "Turn 1 shows Agent Test Start")
        check("[0] Hello" in initial, "Turn 1 shows [0] Hello")
        check("[1] Quit" in initial, "Turn 1 shows [1] Quit")

        output.clear()
        proc.write("0\r")
        time.sleep(2)

        after0 = "".join(output)
        check("World" in after0, "After input 0: turn 2 shows World")
        check("Exit" in after0, "After input 0: turn 2 shows Exit")

        output.clear()
        proc.write("1\r")
        time.sleep(2)
        check(not proc.isalive(), "Process exits after selecting Exit")
    finally:
        stop = True
        if proc.isalive():
            proc.terminate()


# --- New tests ---


def test_cli_clearline(binary, game_dir):
    """CLEARLINE 1: line removed from displayLineList before flush — marker not rendered."""
    text = _capture_cli_with_erb(binary, ERB_CLEARLINE)
    check("LINETOCLEAR_MARKER" not in text, "Cleared line marker absent from output")
    check("AfterClearedLine" in text, "Replacement text visible after clearline")


def test_cli_clear(binary, game_dir):
    """Startup ClearOp: VT 路径下发 ESC[2J。

    ADR-0005 Issue 4：降级路径已删除（VT-only fatal），探测到降级标志或未识别到路径标志均视为 FAIL。
    """
    text = _capture_cli_with_erb(binary, ERB_STARTUP)
    vt_active, detected = detect_vt_path(text)
    if not detected:
        check(False, "未识别到终端路径标志（日志缺失，视为回归）")
    elif vt_active:
        check(f"{ESC}[2J" in text, f"VT 路径: 清屏转义 {ESC}[2J 发出")
    else:
        check(False, "降级路径不应再出现（ADR-0005 已改为 VT-only fatal）")
    check("GameStartMarker" in text, "Game text visible after clear")


def _strip_ansi(text):
    return re.sub(r"\x1b\[[0-9;]*[a-zA-Z]", "", text)


def test_cli_merge(binary, game_dir):
    """Two PRINTs without newline: merged content appears as single line, not separate lines."""
    text = _capture_cli_with_erb(binary, ERB_MERGE)
    check("HelloWorld" in text, "Merged text 'HelloWorld' present")
    clean = _strip_ansi(text)
    check("HelloWorld" in clean, "Merged text present after ANSI strip")


def test_cli_alignment(binary, game_dir):
    """PRINTC: centered text padded with leading spaces."""
    text = _capture_cli_with_erb(binary, ERB_ALIGNMENT)
    clean = _strip_ansi(text)
    m = re.search(r" +AlignCheck", clean)
    if m:
        leading = len(m.group(0)) - len("AlignCheck")
        check(leading >= 10, f"Leading spaces from PrintC ({leading})")
    else:
        check(False, "No leading spaces before centered text")


def test_cli_setbg(binary, game_dir):
    """SETBGCOLOR: VT 路径下发 ESC[48;2;255;0;0m 转义。

    ConPTY 限制：ConPTY 会消费 24-bit color SGR 序列（ESC[48;2;R;G;Bm），
    不传递给捕获端。因此自动化测试中即使 VT 路径正确发出转义也无法检测到。
    此处改为：VT 路径下若未检测到转义则发 WARN（非 FAIL），提示需手动验证。

    ADR-0005 Issue 4：降级路径已删除（VT-only fatal），探测到降级标志或未识别到路径标志均视为 FAIL。
    warn 机制仅保留用于 ConPTY 24-bit color SGR 限制场景。
    """
    text = _capture_cli_with_erb(binary, ERB_SETBG)
    vt_active, detected = detect_vt_path(text)
    bg_escape = f"{ESC}[48;2;255;0;0m"
    if not detected:
        check(False, "未识别到终端路径标志（日志缺失，视为回归）")
    elif vt_active:
        if bg_escape in text:
            check(True, "VT 路径: SETBGCOLOR 发出 ESC[48;2;255;0;0m 转义")
        else:
            warn("VT 路径: SETBGCOLOR 转义未捕获（ConPTY 消费 24-bit color SGR，需手动验证）")
    else:
        check(False, "降级路径不应再出现（ADR-0005 已改为 VT-only fatal）")
    check("RedBackground" in text, "Text after SETBGCOLOR visible")


# Phase 4 回归：INPUT 等待期间末行被反复重写到窗口顶部。
# 根因：Phase 4 删除 ReferenceEquals 检查后，no-op 帧每帧进入 else 分支无条件
# EraseTerminalRows + WriteDisplayLine。EraseTerminalRows 内 SetCursor 参数顺序错误
# 把光标定位到 row=0（窗口顶部），末行被反复擦写。
ERB_NOOP_LOOP = """@SYSTEM_TITLE
PRINTL BeforeInput
PRINTL [0] Done
INPUT
QUIT
"""


def test_cli_noop_frame_no_rewrite(binary, game_dir):
    """INPUT 等待期间 no-op 帧不应重写末行。

    Phase 4 引入的回归：删除 ReferenceEquals 检查后，每帧 FlushBuffer 都进入
    "LineNo 不变" else 分支无条件 EraseTerminalRows(1) + WriteDisplayLine(lastLine)。
    EraseTerminalRows 内 SetCursor 参数顺序错误把光标定位到 row=0（窗口顶部），
    导致末行被反复擦写到顶部——表现为"最后一行不断在窗口顶部刷新"。

    修复：恢复 SourceLine 引用相等检查（游戏线程未替换行时跳过擦写）；
    同时修 EraseTerminalRows SetCursor 参数顺序（row/col 修正）。
    """
    temp_dir, game_dir_local = copy_test_game_with_erb(ERB_NOOP_LOOP)
    try:
        proc = PtyProcess.spawn(
            [str(binary), "--ExeDir", str(game_dir_local), "--protocol", "cli"]
        )
        output = []
        stop = False

        def reader():
            while not stop:
                try:
                    data = proc.read()
                    if data:
                        output.append(data)
                    elif not proc.isalive():
                        break
                except Exception:
                    break

        t = threading.Thread(target=reader, daemon=True)
        t.start()

        try:
            # 等到末行出现（INPUT 状态）
            deadline = time.time() + 10.0
            while time.time() < deadline:
                if "[0] Done" in "".join(output):
                    break
                if not proc.isalive():
                    break
                time.sleep(0.2)

            # 不输入任何东西，让主循环空转 3 秒（约 60 帧）
            time.sleep(3.0)

            text = "".join(output)
        finally:
            stop = True
            if proc.isalive():
                proc.terminate()
    finally:
        shutil.rmtree(temp_dir, ignore_errors=True)

    clean = re.sub(r"\x1b\[[0-9;]*[a-zA-Z]", "", text)
    count = clean.count("[0] Done")
    check(
        count == 1,
        f"INPUT 等待期间末行只渲染一次（实际 {count} 次，期望 1）",
    )


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--binary", help="Path to Emuera.Headless binary")
    parser.add_argument("--game-dir", default="test_game", help="Path to game directory")
    args = parser.parse_args()

    binary_path = Path(args.binary) if args.binary else find_binary(str(ROOT))[0]
    game_dir = Path(args.game_dir)
    if not game_dir.is_absolute():
        game_dir = (ROOT / game_dir).resolve()

    run_cli(binary_path, game_dir)
    test_cli_clearline(binary_path, game_dir)
    test_cli_clear(binary_path, game_dir)
    test_cli_merge(binary_path, game_dir)
    test_cli_alignment(binary_path, game_dir)
    test_cli_setbg(binary_path, game_dir)
    test_cli_noop_frame_no_rewrite(binary_path, game_dir)

    print(f"\n=== CLI basic test: {passed} passed, {failed} failed, {warned} warned ===")
    sys.exit(1 if failed else 0)


if __name__ == "__main__":
    main()
