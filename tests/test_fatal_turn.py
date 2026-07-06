"""Test: Fatal turn structure on script runtime exception.

Verifies that when an ERB script throws (THROW), the server returns a turn
with a proper error structure. The THROW is caught by Process.DoScript()
which sets console state to Error, so the resulting turn is an Error-state
turn (not the StepAsync catch fatal turn, which is a defense-in-depth that
only triggers on unexpected C# exceptions).

The test verifies:
- Initial turn has protocolVersion == 1
- After THROW, the turn has state=Error with error text in the buffer
- protocolVersion is absent from non-initial turns

Usage:
    python test_fatal_turn.py
    python test_fatal_turn.py --binary path/to/Emuera.Headless.exe
    python test_fatal_turn.py --game-dir test_game
"""
import argparse
import json
import shutil
import sys
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "tests"))

from emuera_server import copy_test_game_with_erb, start_server


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--binary", help="Path to Emuera binary (exe or dll)")
    parser.add_argument("--game-dir", default="test_game", help="Path to game directory")
    args = parser.parse_args()

    erb_script = """@SYSTEM_TITLE
PRINTL Fatal Turn Test
PRINTL [0] Trigger fatal
PRINTL [1] Quit
INPUT
IF RESULT == 0
    THROW "fatal-test-marker"
ELSEIF RESULT == 1
    QUIT
ENDIF
"""

    passed = [0]
    failed = [0]

    def check(condition, message):
        if condition:
            passed[0] += 1
            print(f"  PASS: {message}")
        else:
            failed[0] += 1
            print(f"  FAIL: {message}")

    server = None
    temp_dir = None
    try:
        temp_dir, game_dir = copy_test_game_with_erb(erb_script)
        server = start_server(game_dir, binary=args.binary)

        create_status, create_body = server.create_session()
        check(create_status == 201, f"POST /session returns 201, got {create_status}")

        initial_status, initial_body = server.get_turn(timeout=15)
        check(initial_status == 200, f"initial GET /turn returns 200, got {initial_status}")
        initial = json.loads(initial_body)
        check(initial.get("state") == "WaitInput", f"initial state is WaitInput, got {initial.get('state')}")
        check("Fatal Turn Test" in initial.get("text", ""), "initial turn contains Fatal Turn Test")
        check(initial.get("protocolVersion") == 1, "initial turn has protocolVersion == 1")

        input_status, _ = server.post_input("0")
        check(input_status == 200, f"POST /input 0 returns 200, got {input_status}")

        deadline = time.time() + 30
        error_turn = None
        while time.time() < deadline:
            turn_status, turn_body = server.get_turn(timeout=10)
            check(turn_status == 200, f"GET /turn for error returns 200, got {turn_status}")
            turn = json.loads(turn_body)
            if turn.get("state") == "Error" or ("fatal-test-marker" in turn.get("text", "") and "THROW" in turn.get("text", "")):
                error_turn = turn
                break
            time.sleep(0.5)
        else:
            check(False, "error turn received within 30s")

        if error_turn is not None:
            check("fatal-test-marker" in error_turn.get("text", ""), "error turn text contains 'fatal-test-marker'")
            check("THROW" in error_turn.get("text", ""), "error turn text contains THROW error info")
            check(error_turn.get("state") != "WaitInput", f"error turn state is not WaitInput, got {error_turn.get('state')}")
            check("protocolVersion" not in error_turn, "error turn has no protocolVersion field")

    finally:
        if server is not None:
            server.delete_session()
        if server is not None:
            server.close()
        if temp_dir is not None:
            shutil.rmtree(temp_dir, ignore_errors=True)

    print(f"\n=== Fatal turn test: {passed[0]} passed, {failed[0]} failed ===")
    return 0 if failed[0] == 0 else 1


if __name__ == "__main__":
    sys.exit(main())
