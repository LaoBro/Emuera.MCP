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

ESC = "\x1b"

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
    """Startup ClearOp: verify clear escape sequence present + game text after it."""
    text = _capture_cli_with_erb(binary, ERB_STARTUP)
    check(f"{ESC}[2J" in text, f"Clear escape {ESC}[2J emitted")
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
    """SETBGCOLOR: VT escape sequence for background color emitted."""
    text = _capture_cli_with_erb(binary, ERB_SETBG)
    check(f"{ESC}[48;2;255;0;0m" in text, "VT set_bg escape for red (0xFF0000) emitted")
    check("RedBackground" in text, "Text after SETBGCOLOR visible")


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

    print(f"\n=== CLI basic test: {passed} passed, {failed} failed ===")
    sys.exit(1 if failed else 0)


if __name__ == "__main__":
    main()
