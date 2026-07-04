"""Basic CLI mode test: start agent in ConPTY, verify game text, send input, confirm advancement and exit.

Requires Windows 10 18309+ and pywinpty (pip install pywinpty).
Skipped automatically on non-Windows or when pywinpty is unavailable.
"""
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

from emuera_server import find_binary

passed = 0
failed = 0


def check(condition, message):
    global passed, failed
    if condition:
        passed += 1
        print(f"  PASS: {message}")
    else:
        failed += 1
        print(f"  FAIL: {message}")


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

    print(f"\n=== CLI basic test: {passed} passed, {failed} failed ===")
    sys.exit(1 if failed else 0)


if __name__ == "__main__":
    main()
