"""Test: JSONL protocol v2 turn structure (ops[] model) via server mode.

Verifies:
1. Initial turn has protocolVersion: 2 and ops[] with print/newline ops.
2. v1 fields (text, buttons) are NOT present.
3. print ops carry segments[] with per-segment style.
4. print ops optionally carry button with value/isInteger.
5. newline ops optionally carry align.
6. Step turns have no protocolVersion field.
7. Final turn has no protocolVersion.

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

from emuera_server import TEST_GAME_DIR, ops_text, start_server


VALID_OP_TYPES = {"print", "newline", "clearline", "clear", "set_bg"}
VALID_ALIGN = {"left", "center", "right"}


def check_ops(turn, passed, failed, turn_name):
    """Validate ops[] structure for a turn."""
    ops = turn.get("ops", [])
    check(isinstance(ops, list), f"{turn_name} ops is a list", passed, failed)

    for i, op in enumerate(ops):
        check("type" in op, f"{turn_name} op[{i}] has type field", passed, failed)
        check(op["type"] in VALID_OP_TYPES, f"{turn_name} op[{i}] type is valid: {op['type']}", passed, failed)

        if op["type"] == "print":
            check("segments" in op, f"{turn_name} print op[{i}] has segments", passed, failed)
            check(isinstance(op["segments"], list) and len(op["segments"]) > 0,
                  f"{turn_name} print op[{i}] segments is non-empty list", passed, failed)
            for j, seg in enumerate(op["segments"]):
                check("text" in seg and isinstance(seg["text"], str),
                      f"{turn_name} print op[{i}] segment[{j}] has text", passed, failed)
                if "color" in seg:
                    check(isinstance(seg["color"], str) and seg["color"].startswith("#"),
                          f"{turn_name} print op[{i}] segment[{j}] color is #RRGGBB", passed, failed)
                if "bold" in seg:
                    check(isinstance(seg["bold"], bool),
                          f"{turn_name} print op[{i}] segment[{j}] bold is bool", passed, failed)
                if "italic" in seg:
                    check(isinstance(seg["italic"], bool),
                          f"{turn_name} print op[{i}] segment[{j}] italic is bool", passed, failed)
                if "fontname" in seg:
                    check(isinstance(seg["fontname"], str),
                          f"{turn_name} print op[{i}] segment[{j}] fontname is string", passed, failed)
            if "button" in op:
                btn = op["button"]
                check("value" in btn, f"{turn_name} print op[{i}] button has value", passed, failed)
                check("isInteger" in btn, f"{turn_name} print op[{i}] button has isInteger", passed, failed)

        elif op["type"] == "newline":
            if "align" in op:
                check(op["align"] in VALID_ALIGN,
                      f"{turn_name} newline op[{i}] align is valid: {op['align']}", passed, failed)

        elif op["type"] == "clearline":
            check("n" in op, f"{turn_name} clearline op[{i}] has n", passed, failed)
            check(isinstance(op["n"], int) and op["n"] > 0,
                  f"{turn_name} clearline op[{i}] n is positive int", passed, failed)

        elif op["type"] == "set_bg":
            check("color" in op, f"{turn_name} set_bg op[{i}] has color", passed, failed)
            check(isinstance(op["color"], str) and op["color"].startswith("#"),
                  f"{turn_name} set_bg op[{i}] color is #RRGGBB", passed, failed)


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
    parser.add_argument("--suite-name", default="JSONL v2 ops[]", help="Test suite name shown in the summary")
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
        print(f"Turn 1: state={turn1.get('state')} inputType={turn1.get('inputType')} ops={len(turn1.get('ops', []))}")
        check(turn1.get("state") == "WaitInput", "Turn 1 is WaitInput", passed, failed)
        check("text" not in turn1, "Turn 1 has no text field (v2)", passed, failed)
        check("buttons" not in turn1, "Turn 1 has no buttons field (v2)", passed, failed)
        check(turn1.get("protocolVersion") == 2, "Turn 1 has protocolVersion == 2", passed, failed)
        check_ops(turn1, passed, failed, "Turn 1")

        # Verify initial turn ops contain expected content
        check("Agent Test Start" in ops_text(turn1), "Turn 1 ops contain 'Agent Test Start'", passed, failed)
        check("You entered" not in ops_text(turn1), "Turn 1 ops have not processed an input yet", passed, failed)

        # Turn 2: select [0] Hello → shows second menu.
        s, _ = server.post_input("0")
        check(s == 200, f"POST /input returns 200, got {s}", passed, failed)
        s, body = server.get_turn(timeout=15)
        check(s == 200, f"GET /turn after input returns 200, got {s}", passed, failed)
        turn2 = json.loads(body)
        print(f"\nTurn 2: state={turn2.get('state')} inputType={turn2.get('inputType')} ops={len(turn2.get('ops', []))}")
        check(turn2.get("state") == "WaitInput", "Turn 2 is WaitInput", passed, failed)
        check("text" not in turn2, "Turn 2 has no text field (v2)", passed, failed)
        check("buttons" not in turn2, "Turn 2 has no buttons field (v2)", passed, failed)
        check("protocolVersion" not in turn2, "Turn 2 has no protocolVersion field", passed, failed)
        check_ops(turn2, passed, failed, "Turn 2")

        check("You entered: 0" in ops_text(turn2), "Turn 2 ops show first input result", passed, failed)

        # Turn 3: select [0] World → game ends.
        s, _ = server.post_input("0")
        check(s == 200, f"POST /input returns 200, got {s}", passed, failed)
        s, body = server.get_turn(timeout=15)
        check(s == 200, f"GET /turn after input returns 200, got {s}", passed, failed)
        turn3 = json.loads(body)
        print(f"\nTurn 3: state={turn3.get('state')} inputType={turn3.get('inputType')} ops={len(turn3.get('ops', []))}")
        check("text" not in turn3, "Turn 3 has no text field (v2)", passed, failed)
        check("buttons" not in turn3, "Turn 3 has no buttons field (v2)", passed, failed)
        check("protocolVersion" not in turn3, "Turn 3 has no protocolVersion field", passed, failed)
        check_ops(turn3, passed, failed, "Turn 3")

        check("You entered: 0" in ops_text(turn3), "Turn 3 ops show second input result", passed, failed)
        check("Agent Test End" in ops_text(turn3), "Turn 3 ops show end message", passed, failed)
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
