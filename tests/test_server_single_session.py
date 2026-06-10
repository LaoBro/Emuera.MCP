import json
import shutil
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "tests"))

from emuera_server import TEST_GAME_DIR, start_server

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
try:
    server = start_server(TEST_GAME_DIR)

    create1_status, create1_body = server.create_session()
    check(create1_status == 201, f"first POST /sessions returns 201, got {create1_status}")
    session1 = json.loads(create1_body)["sessionId"]

    create2_status, create2_body = server.create_session()
    check(create2_status == 409, f"second POST /sessions returns 409 while active, got {create2_status}")
    check("already active" in create2_body.lower(), "second create response explains active session")

    input_status, input_body = server.request(
        "POST",
        f"/sessions/{session1}/input",
        {"type": "input", "value": "0"},
    )
    check(input_status == 200, f"POST /sessions/{{id}}/input returns 200, got {input_status}")
    check(json.loads(input_body).get("received") is True, "input response marks received=true")

    turn_status, turn_body = server.request("GET", f"/sessions/{session1}/turn", timeout=10)
    check(turn_status == 200, f"GET /sessions/{{id}}/turn returns 200, got {turn_status}")
    turn = json.loads(turn_body)
    check(turn.get("state") == "WaitInput", f"turn state is WaitInput, got {turn.get('state')}")
    check("Agent Test Start" in turn.get("text", ""), "turn text contains Agent Test Start")
    check("buttons" in turn, "turn contains buttons field")

    delete_status, delete_body = server.delete_session(session1)
    check(delete_status == 200, f"DELETE /sessions/{{id}} returns 200, got {delete_status}")
    check(json.loads(delete_body).get("removed") is True, "delete response marks removed=true")

    create_after_delete_status, _ = server.create_session()
    check(create_after_delete_status == 201, f"POST /sessions after DELETE returns 201, got {create_after_delete_status}")

finally:
    if server is not None:
        server.close()

print(f"\n=== Server single-session test: {passed} passed, {failed} failed ===")
sys.exit(1 if failed else 0)
