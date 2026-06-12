"""Test: JSONL protocol flow and buttons schema.

This script combines the previous JSONL flow test and buttons regression test:
1. The initial turn is emitted automatically.
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
import os
import sys

_project_dir = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, _project_dir)
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from emuera_agent import EmueraAgent


def _labels(game):
    return [button["label"] for button in game.buttons]


def _has_label(game, expected):
    return any(expected in label for label in _labels(game))


def check_buttons(game, passed, failed, turn_name, expected_buttons=None):
    """Validate the buttons schema for the current turn.
    
    expected_buttons: list of (label_substring, expected_value) tuples.
    If provided, also verifies exact button count, values, and absence of stale buttons.
    """
    if expected_buttons is not None:
        check(len(game.buttons) == len(expected_buttons), f"{turn_name} has {len(expected_buttons)} buttons (no stale)", passed, failed)
    else:
        check(len(game.buttons) >= 0, f"{turn_name} has buttons field", passed, failed)
    for i, button in enumerate(game.buttons):
        check("label" in button and "value" in button, f"{turn_name} button {i} has label and value", passed, failed)
        check(isinstance(button["value"], int), f"{turn_name} button {i} value is integer", passed, failed)
    check(all("label" in button and "value" in button for button in game.buttons), f"{turn_name} all buttons have label and value", passed, failed)
    check(all(isinstance(button["value"], int) for button in game.buttons), f"{turn_name} all button values are integers", passed, failed)
    if expected_buttons is not None:
        for label_sub, expected_val in expected_buttons:
            matching = [b for b in game.buttons if label_sub in b["label"]]
            check(len(matching) >= 1, f"{turn_name} contains {label_sub}", passed, failed)
            if matching:
                check(matching[0]["value"] == expected_val, f"{turn_name} button '{label_sub}' value is {expected_val}", passed, failed)
    labels = _labels(game)
    return labels


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

    with EmueraAgent(args.game_dir, binary=args.binary) as game:
        # Turn 1: @SYSTEM_TITLE shows initial menu.
        print(f"Turn 1: state={game.state} inputType={game.input_type} buttons={len(game.buttons)}")
        check(game.state == "WaitInput", "Turn 1 is WaitInput", passed, failed)
        check("Agent Test Start" in game.text, "Turn 1 shows test start", passed, failed)
        check("You entered" not in game.text, "Turn 1 has not processed an input yet", passed, failed)
        check_buttons(game, passed, failed, "Turn 1", expected_buttons=[("[0] Hello", 0), ("[1] Quit", 1)])

        # Turn 2: select [0] Hello → shows second menu.
        game.step("0")
        print(f"\nTurn 2: state={game.state} inputType={game.input_type} buttons={len(game.buttons)}")
        check(game.state == "WaitInput", "Turn 2 is WaitInput", passed, failed)
        check("You entered: 0" in game.text, "Turn 2 shows first input result", passed, failed)
        # Turn 2 should only have current-generation buttons, not stale Turn 1 buttons
        check_buttons(game, passed, failed, "Turn 2", expected_buttons=[("[0] World", 0), ("[1] Exit", 1)])

        # Turn 3: select [0] World → game ends.
        game.step("0")
        print(f"\nTurn 3: state={game.state} inputType={game.input_type} buttons={len(game.buttons)}")
        check("You entered: 0" in game.text, "Turn 3 shows second input result", passed, failed)
        check("Agent Test End" in game.text, "Turn 3 shows end message", passed, failed)
        check(game.state == "Quit", "Turn 3 state is Quit", passed, failed)

    print(f"\n=== {args.suite_name}: {passed[0]} passed, {failed[0]} failed ===")
    return 0 if failed[0] == 0 else 1


if __name__ == "__main__":
    sys.exit(main())
