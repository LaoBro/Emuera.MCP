"""Regression tests for CLEARLINE + reprint and PRINTN merge cursor bugs.

Tests two bugs introduced by commit 56c2044 (PRD-T6 delta-based FlushBuffer):

1. CLEARLINE N + reprint N lines: FlushBuffer's delta tracking compared by
   LineNo, which loops back after CLEARLINE (lineNo -= N then reprint).
   Only the last line was rewritten, earlier new lines were lost from
   the terminal display while ButtonRegionTracker recorded regions based
   on displayLineList → mouse clicks hit wrong rows.

2. PRINTN (IsLineEnd=false) as last line of a turn, then next turn PRINTL
   merges via AddDisplayLine. WriteDisplayLine uses Console.Write (no
   newline) for IsLineEnd=false lines, leaving cursor mid-line.
   EraseTerminalRows(1) assumed cursor was on the next line, so it erased
   the wrong row. WriteNewLinesSince's startIdx-- also reprinted unchanged
   prior lines.

Uses pywinpty (ConPTY) for real VT terminal interaction.
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

# CLEARLINE N then reprint N lines with buttons.
# Bug: after CLEARLINE 3, reprinted NewMapLine1/NewMapLine2 were lost from
# terminal (only last line NewButton was rewritten via delta branch).
ERB_CLEARLINE_REPRINT = """@SYSTEM_TITLE
PRINTL OldMapLine1
PRINTL OldMapLine2
PRINTL [0] OldButton
INPUT
CLEARLINE 3
PRINTL NewMapLine1
PRINTL NewMapLine2
PRINTL [0] NewButton
INPUT
PRINTL ClickedNewButton
QUIT
"""

# PRINTN (IsLineEnd=false, triggers ReadAnyKey) then PRINTL merges.
# Bug: PRINTN B left cursor at end of "B" row (IsLineEnd=false).
# When PRINTL C merged into "BC", WriteNewLinesSince used EraseTerminalRows(1)
# which erased the row ABOVE cursor (wrong row), and startIdx-- reprinted
# unchanged prior lines. Result: "A" was wiped, stray "B" remained, "BC"
# written on wrong row.
ERB_PRINTN_MERGE = """@SYSTEM_TITLE
PRINTL A
PRINTN B
PRINTL C
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


# --- Minimal VT100 screen simulator ---


class AnsiScreen:
    """Minimal VT100 screen simulator. Parses ANSI escape sequences and
    maintains screen buffer to extract final visible content."""

    def __init__(self, width=120, height=40):
        self.width = width
        self.height = height
        self.rows = [[" "] * width for _ in range(height)]
        self.cursor_row = 0
        self.cursor_col = 0

    def feed(self, data):
        i = 0
        while i < len(data):
            ch = data[i]
            if ch == ESC:
                i = self._handle_escape(data, i)
            elif ch == "\r":
                self.cursor_col = 0
                i += 1
            elif ch == "\n":
                self.cursor_row = min(self.cursor_row + 1, self.height - 1)
                self.cursor_col = 0
                i += 1
            elif ch == "\b":
                if self.cursor_col > 0:
                    self.cursor_col -= 1
                i += 1
            else:
                self._put_char(ch)
                i += 1

    def _put_char(self, ch):
        if 0 <= self.cursor_row < self.height and 0 <= self.cursor_col < self.width:
            self.rows[self.cursor_row][self.cursor_col] = ch
        self.cursor_col += 1
        if self.cursor_col >= self.width:
            self.cursor_col = 0
            self.cursor_row = min(self.cursor_row + 1, self.height - 1)

    def _handle_escape(self, data, i):
        if i + 1 >= len(data):
            return i + 1
        nxt = data[i + 1]
        if nxt == "[":
            return self._handle_csi(data, i)
        elif nxt == "]":
            # OSC: skip to BEL or ST (ESC \)
            j = i + 2
            while j < len(data):
                if data[j] == "\x07":
                    return j + 1
                if data[j] == ESC and j + 1 < len(data) and data[j + 1] == "\\":
                    return j + 2
                j += 1
            return j
        else:
            # ESC + single char (e.g., ESC 7, ESC 8), skip
            return i + 2

    def _handle_csi(self, data, i):
        j = i + 2  # skip ESC [
        params_start = j
        while j < len(data) and data[j] not in "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghlm":
            j += 1
        if j >= len(data):
            return j
        params = data[params_start:j]
        cmd = data[j]
        nums = []
        if params:
            for p in params.split(";"):
                if p == "":
                    nums.append(0)
                else:
                    try:
                        nums.append(int(p))
                    except ValueError:
                        nums.append(0)

        if cmd in ("H", "f"):
            row = (nums[0] - 1) if nums and nums[0] > 0 else 0
            col = (nums[1] - 1) if len(nums) > 1 and nums[1] > 0 else 0
            self.cursor_row = max(0, min(row, self.height - 1))
            self.cursor_col = max(0, min(col, self.width - 1))
        elif cmd == "A":
            n = nums[0] if nums and nums[0] > 0 else 1
            self.cursor_row = max(0, self.cursor_row - n)
        elif cmd == "B":
            n = nums[0] if nums and nums[0] > 0 else 1
            self.cursor_row = min(self.height - 1, self.cursor_row + n)
        elif cmd == "C":
            n = nums[0] if nums and nums[0] > 0 else 1
            self.cursor_col = min(self.width - 1, self.cursor_col + n)
        elif cmd == "D":
            n = nums[0] if nums and nums[0] > 0 else 1
            self.cursor_col = max(0, self.cursor_col - n)
        elif cmd == "J":
            mode = nums[0] if nums else 0
            if mode == 2:
                self.rows = [[" "] * self.width for _ in range(self.height)]
                self.cursor_row = 0
                self.cursor_col = 0
            elif mode == 0:
                for c in range(self.cursor_col, self.width):
                    self.rows[self.cursor_row][c] = " "
                for r in range(self.cursor_row + 1, self.height):
                    self.rows[r] = [" "] * self.width
            elif mode == 1:
                for r in range(0, self.cursor_row):
                    self.rows[r] = [" "] * self.width
                for c in range(0, min(self.cursor_col + 1, self.width)):
                    self.rows[self.cursor_row][c] = " "
        elif cmd == "K":
            mode = nums[0] if nums else 0
            if mode == 2:
                self.rows[self.cursor_row] = [" "] * self.width
            elif mode == 0:
                for c in range(self.cursor_col, self.width):
                    self.rows[self.cursor_row][c] = " "
            elif mode == 1:
                for c in range(0, min(self.cursor_col + 1, self.width)):
                    self.rows[self.cursor_row][c] = " "
        # SGR (m), h, l etc. are ignored
        return j + 1

    def get_visible_lines(self):
        """Return non-empty lines from screen, stripped of trailing spaces."""
        result = []
        for row in self.rows:
            line = "".join(row).rstrip()
            if line:
                result.append(line)
        return result


# --- Helpers ---


def _run_cli_wait_for(binary, erb_text, send_after_text, wait_after_text, extra_wait=1.0, initial_timeout=15.0):
    """Copy test_game, inject ERB, run CLI, wait for send_after_text, send input,
    then wait for wait_after_text and capture output.

    Only one input is sent — avoids QUIT/cleanup clearing the screen before
    we can inspect the rendered state.
    """
    temp_dir, game_dir = copy_test_game_with_erb(erb_text)
    try:
        proc = PtyProcess.spawn(
            [str(binary), "--ExeDir", str(game_dir), "--protocol", "cli"]
        )
        output = []
        stop = False

        def reader():
            while not stop:
                try:
                    data = proc.read()
                    if data:
                        output.append(data)
                    elif not proc.isalive():
                        break
                except Exception:
                    break

        t = threading.Thread(target=reader, daemon=True)
        t.start()

        try:
            deadline = time.time() + initial_timeout
            while time.time() < deadline:
                if send_after_text in "".join(output):
                    break
                if not proc.isalive():
                    break
                time.sleep(0.2)

            proc.write("0\r")

            deadline = time.time() + initial_timeout
            while time.time() < deadline:
                if wait_after_text in "".join(output):
                    break
                if not proc.isalive():
                    break
                time.sleep(0.2)
            time.sleep(extra_wait)

            return "".join(output)
        finally:
            stop = True
            if proc.isalive():
                proc.terminate()
    finally:
        shutil.rmtree(temp_dir, ignore_errors=True)


def _final_screen(text):
    """Feed raw output through AnsiScreen, return final visible lines."""
    screen = AnsiScreen()
    screen.feed(text)
    return screen.get_visible_lines()


# --- Tests ---


def test_clearline_reprint(binary, game_dir):
    """CLEARLINE 3 + reprint 3 lines: all new lines visible on terminal.

    Bug: delta tracking by LineNo caused only last line to be rewritten.
    NewMapLine1/NewMapLine2 were lost from terminal display.
    """
    text = _run_cli_wait_for(
        binary, ERB_CLEARLINE_REPRINT,
        send_after_text="OldButton",
        wait_after_text="NewButton",
    )
    lines = _final_screen(text)

    check(
        any("NewMapLine1" in l for l in lines),
        "NewMapLine1 visible after CLEARLINE + reprint (was lost before fix)",
    )
    check(
        any("NewMapLine2" in l for l in lines),
        "NewMapLine2 visible after CLEARLINE + reprint",
    )
    check(
        any("NewButton" in l for l in lines),
        "NewButton visible after reprint",
    )


def test_printn_merge(binary, game_dir):
    """PRINTN B (IsLineEnd=false) then PRINTL C: merged 'BC' displayed correctly.

    Bug: PRINTN leaves cursor at end of "B" row. When PRINTL C merges,
    EraseTerminalRows(1) erased the wrong row (above cursor instead of
    cursor row), wiping "A" and leaving stray "B".
    """
    temp_dir, game_dir_local = copy_test_game_with_erb(ERB_PRINTN_MERGE)
    try:
        proc = PtyProcess.spawn(
            [str(binary), "--ExeDir", str(game_dir_local), "--protocol", "cli"]
        )
        output = []
        stop = False

        def reader():
            while not stop:
                try:
                    data = proc.read()
                    if data:
                        output.append(data)
                    elif not proc.isalive():
                        break
                except Exception:
                    break

        t = threading.Thread(target=reader, daemon=True)
        t.start()

        try:
            # Wait for PRINTN B output (ReadAnyKey waits for Enter)
            deadline = time.time() + 15.0
            while time.time() < deadline:
                if "B" in "".join(output):
                    time.sleep(0.5)
                    break
                if not proc.isalive():
                    break
                time.sleep(0.2)

            proc.write("\r")  # Enter for ReadAnyKey -> PRINTL C merges

            # Wait for merged "BC" output (INPUT waits)
            deadline = time.time() + 15.0
            while time.time() < deadline:
                if "C" in "".join(output):
                    time.sleep(0.5)
                    break
                if not proc.isalive():
                    break
                time.sleep(0.2)

            text = "".join(output)
        finally:
            stop = True
            if proc.isalive():
                proc.terminate()
    finally:
        shutil.rmtree(temp_dir, ignore_errors=True)

    lines = _final_screen(text)

    check(
        any("BC" in l for l in lines),
        "Merged 'BC' visible after PRINTN + PRINTL merge",
    )
    check(
        any("A" in l for l in lines),
        "'A' line preserved (was erased by wrong-row erase before fix)",
    )
    stray_b = [l for l in lines if l.strip() == "B"]
    check(
        not stray_b,
        f"No stray 'B' residual line (found: {stray_b})",
    )


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--binary", help="Path to Emuera.Headless binary")
    parser.add_argument("--game-dir", default="test_game", help="Path to game directory")
    args = parser.parse_args()

    binary_path = Path(args.binary) if args.binary else find_binary(str(ROOT))[0]
    game_dir = Path(args.game_dir)
    if not game_dir.is_absolute():
        game_dir = (ROOT / game_dir).resolve()

    print("--- test_clearline_reprint ---")
    test_clearline_reprint(binary_path, game_dir)
    print("--- test_printn_merge ---")
    test_printn_merge(binary_path, game_dir)

    print(f"\n=== Regression test: {passed} passed, {failed} failed ===")
    sys.exit(1 if failed else 0)


if __name__ == "__main__":
    main()
