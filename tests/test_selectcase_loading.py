"""Test that ERB functions containing SELECTCASE / CASE / CASEELSE load without NRE.

This guards against the regression introduced in T-022 CS8602 cleanup
(commit d1db3a2), where the runtime guard `IfCaseList.Count > 0 &&` was
mistakenly removed and replaced with bare null-forgiving operators `!`.
The `!` operator does not prevent `LinkedList<>.Last` from returning null
on an empty list, causing a NullReferenceException at parse time.

See docs/2026.6.30.架构健壮性重构/T-022阶段3-nullable警告清理.md §4 ("! 不能替代运行时守卫").
"""
import json
import shutil
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "tests"))

from emuera_server import copy_test_game_with_erb, ops_text, start_server

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


# Minimal ERB that exercises SELECTCASE / CASE / CASEELSE / ENDSELECT
# during loading AND at runtime, so both the parse phase and execution work.
ERB_TEXT = """@SYSTEM_TITLE
PRINTL SelectCase Test
INPUT
SELECTCASE RESULT
CASE 0
	PRINTFORML You chose: {RESULT}
CASE 1
	PRINTFORML You chose: {RESULT}
CASEELSE
	PRINTFORML You chose: {RESULT}, fallback
ENDSELECT
PRINTL Done
QUIT
"""

server = None
temp_dir = None
try:
    temp_dir, game_dir = copy_test_game_with_erb(ERB_TEXT)
    server = start_server(game_dir)

    # Create session — this triggers ERB loading.
    # If the SELECTCASE / CASE handler has the NRE bug, the game won't
    # reach WaitInput (the server may hang, crash, or return error).
    create_status, create_body = server.create_session()
    check(create_status == 201, f"POST /session returns 201, got {create_status}")
    session_id = json.loads(create_body)["sessionId"]

    # First turn: should show the PRINTL and reach INPUT
    initial_status, initial_body = server.get_turn(timeout=10)
    check(initial_status == 200, f"initial GET /turn returns 200, got {initial_status}")
    initial = json.loads(initial_body)
    check(initial.get("state") == "WaitInput", f"initial state is WaitInput, got {initial.get('state')}")
    check("SelectCase Test" in ops_text(initial), "initial turn contains test start")
    check(initial.get("inputType") == "IntValue", f"initial inputType is IntValue, got {initial.get('inputType')}")

    # Submit 0 → triggers CASE 0 branch
    input_status, input_body = server.post_input("0")
    check(input_status == 200, f"CASE 0 POST /input returns 200, got {input_status}")

    turn_status, turn_body = server.get_turn(timeout=10)
    check(turn_status == 200, f"GET /turn after CASE 0 returns 200, got {turn_status}")
    turn = json.loads(turn_body)
    check("You chose: 0" in ops_text(turn), "CASE 0 branch executed")
    check("Done" in ops_text(turn), "execution continued past ENDSELECT")
    check(turn.get("state") == "Quit", f"final state is Quit, got {turn.get('state')}")

finally:
    if server is not None:
        server.delete_session()
    if server is not None:
        server.close()
    if temp_dir is not None:
        shutil.rmtree(temp_dir, ignore_errors=True)

print(f"\n=== SELECTCASE loading test: {passed} passed, {failed} failed ===")
sys.exit(1 if failed else 0)
