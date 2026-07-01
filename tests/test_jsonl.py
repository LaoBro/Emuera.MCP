"""Test: JSONL protocol flow and buttons schema (via server mode).

T-024 后 stdin 管道 JSONL 模式已废弃，本测试改用 server 模式驱动。
AgentJsonlProtocol 的 turn 结构（text/state/inputType/needValue/buttons）在
server 模式下与原 stdin 管道完全一致，断言逻辑保持不变。

1. The initial turn is emitted automatically after session creation.
2. Each turn exposes text/state/inputType/needValue/buttons fields.
3. Button entries contain label + value fields.
4. Integer button values are preserved.
5. Input advances the game to the expected next turn.

Usage:
    python test_jsonl.py
    python test_jsonl.py --binary path/to/Emuera.Headless.exe
    python test_jsonl.py --game-dir test_game
"""
import argparse
import json
import os
import sys
from pathlib import Path

_project_dir = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, _project_dir)
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from emuera_server import TEST_GAME_DIR, start_server


def _labels(turn):
    return [button["label"] for button in turn.get("buttons", [])]


def _has_label(turn, expected):
    return any(expected in label for label in _labels(turn))


def check_buttons(turn, passed, failed, turn_name, expected_buttons=None):
    """Validate the buttons schema for the current turn.

    expected_buttons: list of (label_substring, expected_value) tuples.
    If provided, also verifies exact button count, values, and absence of stale buttons.
    """
    buttons = turn.get("buttons", [])
    if expected_buttons is not None:
        check(len(buttons) == len(expected_buttons), f"{turn_name} has {len(expected_buttons)} buttons (no stale)", passed, failed)
    else:
        check(len(buttons) >= 0, f"{turn_name} has buttons field", passed, failed)
    for i, button in enumerate(buttons):
        check("label" in button and "value" in button, f"{turn_name} button {i} has label and value", passed, failed)
        check(isinstance(button["value"], int), f"{turn_name} button {i} value is integer", passed, failed)
    check(all("label" in button and "value" in button for button in buttons), f"{turn_name} all buttons have label and value", passed, failed)
    check(all(isinstance(button["value"], int) for button in buttons), f"{turn_name} all button values are integers", passed, failed)
    if expected_buttons is not None:
        for label_sub, expected_val in expected_buttons:
            matching = [b for b in buttons if label_sub in b["label"]]
            check(len(matching) >= 1, f"{turn_name} contains {label_sub}", passed, failed)
            if matching:
                check(matching[0]["value"] == expected_val, f"{turn_name} button '{label_sub}' value is {expected_val}", passed, failed)
    return _labels(turn)


def check(condition, message, passed, failed):
    if condition:
        passed[0] += 1
        print(f"  PASS: {message}")
    else:
        failed[0] += 1
        print(f"  FAIL: {message}")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--binary", help="Path to Emuera binary (exe or dll)")
    parser.add_argument("--game-dir", default="test_game", help="Path to game directory")
    parser.add_argument("--suite-name", default="JSONL + buttons", help="Test suite name shown in the summary")
    args = parser.parse_args()

    passed = [0]
    failed = [0]

    game_dir = Path(args.game_dir)
    if not game_dir.is_absolute():
        game_dir = _project_dir / game_dir

    server = None
    try:
        server = start_server(str(game_dir), binary=args.binary)

        # Create session — server uses AgentJsonlProtocol internally.
        s, body = server.create_session()
        check(s == 201, f"POST /session returns 201, got {s}", passed, failed)
        check(json.loads(body).get("state") is not None, "create response includes state", passed, failed)

        # Turn 1: @SYSTEM_TITLE shows initial menu.
        s, body = server.get_turn(timeout=15)
        check(s == 200, f"initial GET /turn returns 200, got {s}", passed, failed)
        turn1 = json.loads(body)
        print(f"Turn 1: state={turn1.get('state')} inputType={turn1.get('inputType')} buttons={len(turn1.get('buttons', []))}")
        check(turn1.get("state") == "WaitInput", "Turn 1 is WaitInput", passed, failed)
        check("Agent Test Start" in turn1.get("text", ""), "Turn 1 shows test start", passed, failed)
        check("You entered" not in turn1.get("text", ""), "Turn 1 has not processed an input yet", passed, failed)
        check_buttons(turn1, passed, failed, "Turn 1", expected_buttons=[("[0] Hello", 0), ("[1] Quit", 1)])

        # Turn 2: select [0] Hello → shows second menu.
        s, _ = server.post_input("0")
        check(s == 200, f"POST /input returns 200, got {s}", passed, failed)
        s, body = server.get_turn(timeout=15)
        check(s == 200, f"GET /turn after input returns 200, got {s}", passed, failed)
        turn2 = json.loads(body)
        print(f"\nTurn 2: state={turn2.get('state')} inputType={turn2.get('inputType')} buttons={len(turn2.get('buttons', []))}")
        check(turn2.get("state") == "WaitInput", "Turn 2 is WaitInput", passed, failed)
        check("You entered: 0" in turn2.get("text", ""), "Turn 2 shows first input result", passed, failed)
        # Turn 2 should only have current-generation buttons, not stale Turn 1 buttons
        check_buttons(turn2, passed, failed, "Turn 2", expected_buttons=[("[0] World", 0), ("[1] Exit", 1)])

        # Turn 3: select [0] World → game ends.
        s, _ = server.post_input("0")
        check(s == 200, f"POST /input returns 200, got {s}", passed, failed)
        s, body = server.get_turn(timeout=15)
        check(s == 200, f"GET /turn after input returns 200, got {s}", passed, failed)
        turn3 = json.loads(body)
        print(f"\nTurn 3: state={turn3.get('state')} inputType={turn3.get('inputType')} buttons={len(turn3.get('buttons', []))}")
        check("You entered: 0" in turn3.get("text", ""), "Turn 3 shows second input result", passed, failed)
        check("Agent Test End" in turn3.get("text", ""), "Turn 3 shows end message", passed, failed)
        check(turn3.get("state") == "Quit", "Turn 3 state is Quit", passed, failed)
    finally:
        if server is not None:
            try:
                server.delete_session()
            except Exception:
                pass
            server.close()

    print(f"\n=== {args.suite_name}: {passed[0]} passed, {failed[0]} failed ===")
    return 0 if failed[0] == 0 else 1


if __name__ == "__main__":
    sys.exit(main())
