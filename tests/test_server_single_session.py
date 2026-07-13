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

    # 无 session 时 GET /state 返回 Idle
    idle_status, idle_body = server.get_state()
    check(idle_status == 200, f"GET /state with no session returns 200, got {idle_status}")
    check(json.loads(idle_body).get("state") == "Idle", "no-session state is Idle")

    # 无 session 时 GET /turn 返回 404
    no_turn_status, _ = server.get_turn(timeout=5)
    check(no_turn_status == 404, f"GET /turn with no session returns 404, got {no_turn_status}")

    create1_status, create1_body = server.create_session()
    check(create1_status == 201, f"first POST /session returns 201, got {create1_status}")
    session1 = json.loads(create1_body)["sessionId"]
    check(json.loads(create1_body).get("state") is not None, "create response includes state")

    create2_status, create2_body = server.create_session()
    check(create2_status == 409, f"second POST /session returns 409 while active, got {create2_status}")
    check("already active" in create2_body.lower(), "second create response explains active session")

    initial_turn_status, initial_turn_body = server.get_turn(timeout=10)
    check(initial_turn_status == 200, f"initial GET /turn returns 200, got {initial_turn_status}")
    initial_turn = json.loads(initial_turn_body)
    check(initial_turn.get("state") == "WaitInput", f"initial turn state is WaitInput, got {initial_turn.get('state')}")
    check("text" not in initial_turn, "initial turn has no text field (v2)")
    check("ops" in initial_turn, "initial turn contains ops field")

    input_status, input_body = server.post_input("0")
    check(input_status == 200, f"POST /input returns 200, got {input_status}")
    check(json.loads(input_body).get("received") is True, "input response marks received=true")

    turn_status, turn_body = server.get_turn(timeout=10)
    check(turn_status == 200, f"GET /turn after input returns 200, got {turn_status}")
    turn = json.loads(turn_body)
    check(turn.get("state") == "WaitInput", f"turn state is WaitInput, got {turn.get('state')}")
    check("text" not in turn, "turn has no text field (v2)")
    check("ops" in turn, "turn contains ops field")

    # Phase 0-1b: GET /snapshot 端点接线薄护栏——确保端点 wiring 未断，
    # JSON 形状（state / lines / button.col+width）与 C# golden 一致。
    snap_status, snap_body = server.get_snapshot(timeout=10)
    check(snap_status == 200, f"GET /snapshot with active session returns 200, got {snap_status}")
    snap = json.loads(snap_body)
    check("state" in snap and isinstance(snap["state"], str), "snapshot has state string")
    check("lines" in snap and isinstance(snap["lines"], list), "snapshot has lines[] list")
    check(snap.get("protocolVersion") == 3, f"snapshot protocolVersion == 3, got {snap.get('protocolVersion')}")
    # 验证按钮几何存在（至少一个按钮有 col + width 整数字段）
    has_button_geometry = False
    for line in snap.get("lines", []):
        for entry in line.get("entries", []):
            btn = entry.get("button")
            if btn is not None and isinstance(btn.get("col"), int) and isinstance(btn.get("width"), int):
                has_button_geometry = True
                break
        if has_button_geometry:
            break
    check(has_button_geometry, "snapshot has at least one button with int col+width geometry")

    # GET /state 有 session 时返回完整字段
    state_status, state_body = server.get_state()
    check(state_status == 200, f"GET /state with session returns 200, got {state_status}")
    state_obj = json.loads(state_body)
    check(state_obj.get("sessionId") == session1, "GET /state returns matching sessionId")
    check(state_obj.get("isRunning") is True, "GET /state shows isRunning=true")
    check(state_obj.get("state") == "WaitInput", f"GET /state state is WaitInput, got {state_obj.get('state')}")

    delete_status, delete_body = server.delete_session()
    check(delete_status == 200, f"DELETE /session returns 200, got {delete_status}")
    check(json.loads(delete_body).get("removed") is True, "delete response marks removed=true")

    create_after_delete_status, _ = server.create_session()
    check(create_after_delete_status == 201, f"POST /session after DELETE returns 201, got {create_after_delete_status}")

finally:
    if server is not None:
        server.close()

print(f"\n=== Server single-session test: {passed} passed, {failed} failed ===")
sys.exit(1 if failed else 0)
