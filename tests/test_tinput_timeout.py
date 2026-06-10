import json
import shutil
import sys
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "tests"))

from emuera_server import copy_test_game_with_erb, start_server

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


server = None
temp_dir = None
try:
    temp_dir, game_dir = copy_test_game_with_erb(
        """@SYSTEM_TITLE
PRINTL TINPUT Timeout Test
TINPUT 300, 7, 0, "TIME UP", 0
PRINTFORML RESULT={RESULT}
INPUT
PRINTFORML INPUT RESULT={RESULT}
QUIT
"""
    )
    server = start_server(game_dir)

    create_status, create_body = server.create_session()
    check(create_status == 201, f"POST /sessions returns 201, got {create_status}")
    session_id = json.loads(create_body)["sessionId"]

    initial_status, initial_body = server.request("GET", f"/sessions/{session_id}/turn", timeout=10)
    check(initial_status == 200, f"initial GET /turn returns 200, got {initial_status}")
    initial = json.loads(initial_body)
    check(initial.get("state") == "WaitInput", f"initial state is WaitInput, got {initial.get('state')}")
    check("TINPUT Timeout Test" in initial.get("text", ""), "initial turn contains TINPUT test start")
    check(initial.get("inputType") == "IntValue", f"initial inputType is IntValue, got {initial.get('inputType')}")
    check(initial.get("needValue") is True, "initial request needs a value")

    time.sleep(1.0)

    timeout_status, timeout_body = server.request("GET", f"/sessions/{session_id}/turn", timeout=10)
    check(timeout_status == 200, f"timeout GET /turn returns 200, got {timeout_status}")
    timeout_turn = json.loads(timeout_body)
    check("TIME UP" in timeout_turn.get("text", ""), "timeout turn contains TimeUpMes")
    check("RESULT=7" in timeout_turn.get("text", ""), "timeout turn executes default value 7")
    check(timeout_turn.get("state") == "WaitInput", f"timeout advances to next WaitInput, got {timeout_turn.get('state')}")

    input_status, input_body = server.request(
        "POST",
        f"/sessions/{session_id}/input",
        {"type": "input", "value": "1"},
    )
    check(input_status == 200, f"input after timeout returns 200, got {input_status}")

    final_status, final_body = server.request("GET", f"/sessions/{session_id}/turn", timeout=10)
    check(final_status == 200, f"final GET /turn returns 200, got {final_status}")
    final_turn = json.loads(final_body)
    check("INPUT RESULT=1" in final_turn.get("text", ""), "later input is not swallowed or shifted")
    check(final_turn.get("state") == "Quit", f"final state is Quit, got {final_turn.get('state')}")

finally:
    if server is not None and "session_id" in locals():
        server.delete_session(session_id)
    if server is not None:
        server.close()
    if temp_dir is not None:
        shutil.rmtree(temp_dir, ignore_errors=True)

print(f"\n=== TINPUT timeout test: {passed} passed, {failed} failed ===")
sys.exit(1 if failed else 0)
