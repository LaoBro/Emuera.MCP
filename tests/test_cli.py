"""Test: CLI protocol pipe mode.

This test drives Emuera.Headless with redirected stdin/stdout, similar to the
JSONL integration test, but asserts plain CLI text output instead of JSONL.

Usage:
    python test_cli.py
    python test_cli.py --binary path/to/Emuera.Headless.exe
    python test_cli.py --game-dir test_game
"""
import argparse
import os
import shutil
import subprocess
import sys
import threading
import time
from pathlib import Path


_PROJECT_DIR = Path(__file__).resolve().parents[1]


def _binary_command(binary_path):
    path = Path(binary_path)
    if path.suffix.lower() == ".dll":
        dotnet = shutil.which("dotnet")
        if dotnet is None:
            raise RuntimeError("dotnet was not found in PATH")
        return [dotnet, "exec", str(path)]
    return [str(path)]


class CliProcess:
    def __init__(self, binary_path, game_dir):
        self.binary_path = binary_path
        self.game_dir = str(game_dir)
        self.proc = None
        self.output = ""

    def start(self):
        cmd = _binary_command(self.binary_path) + [
            "--ExeDir",
            self.game_dir,
            "--protocol",
            "cli",
        ]
        print(f"[CliProtocol] Starting: {' '.join(cmd)}")
        self.proc = subprocess.Popen(
            cmd,
            cwd=str(_PROJECT_DIR),
            stdin=subprocess.PIPE,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
            encoding="utf-8",
            errors="replace",
        )

    def send(self, value):
        if self.proc is None or self.proc.stdin is None:
            raise RuntimeError("CLI process is not started")
        self.proc.stdin.write(value + "\n")
        self.proc.stdin.flush()

    def _readline_with_timeout(self, timeout):
        result = [None]
        def read():
            try:
                result[0] = self.proc.stdout.readline()
            except Exception:
                result[0] = ""
        t = threading.Thread(target=read, daemon=True)
        t.start()
        t.join(timeout)
        if t.is_alive():
            return None
        return result[0]

    def read_until(self, expected, timeout=30):
        if self.proc is None or self.proc.stdout is None:
            raise RuntimeError("CLI process is not started")

        deadline = time.time() + timeout
        while time.time() < deadline:
            if self.proc.poll() is not None:
                stderr = ""
                if self.proc.stderr is not None:
                    stderr = self.proc.stderr.read()
                raise AssertionError(
                    f"CLI process exited early with code {self.proc.returncode}\n"
                    f"stdout:\n{self.output}\nstderr:\n{stderr}"
                )

            remaining = deadline - time.time()
            if remaining <= 0:
                break
            line = self._readline_with_timeout(min(remaining, 5))
            if line is None:
                continue
            if line:
                self.output += line
                if expected in self.output:
                    return
            else:
                time.sleep(0.05)

        raise AssertionError(
            f"Timed out waiting for CLI output containing: {expected!r}\n"
            f"stdout:\n{self.output}"
        )

    def close(self):
        if self.proc is None:
            return
        try:
            if self.proc.stdin is not None:
                self.proc.stdin.close()
            self.proc.wait(timeout=10)
        except subprocess.TimeoutExpired:
            self.proc.kill()
            self.proc.wait(timeout=5)
        finally:
            self.proc = None


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
    parser.add_argument("--suite-name", default="CLI protocol", help="Test suite name shown in the summary")
    args = parser.parse_args()

    binary_path = args.binary
    if binary_path is None:
        candidates = [
            _PROJECT_DIR / "Emuera.Headless" / "bin" / "Debug" / "net10.0" / "Emuera.Headless.exe",
            _PROJECT_DIR / "Emuera.Headless" / "bin" / "Release" / "net10.0" / "Emuera.Headless.exe",
        ]
        for candidate in candidates:
            if candidate.exists():
                binary_path = str(candidate)
                break
        if binary_path is None:
            raise FileNotFoundError("Cannot find Emuera.Headless binary. Pass --binary or build first.")

    game_dir = Path(args.game_dir)
    if not game_dir.is_absolute():
        game_dir = _PROJECT_DIR / game_dir

    passed = [0]
    failed = [0]
    cli = CliProcess(binary_path, game_dir)

    try:
        cli.start()

        cli.read_until("Agent Test Start")
        cli.read_until("[1] Quit")
        check("Agent Test Start" in cli.output, "Initial CLI output contains test start", passed, failed)
        check("[0] Hello" in cli.output, "Initial CLI output contains Hello button", passed, failed)
        check("[1] Quit" in cli.output, "Initial CLI output contains Quit button", passed, failed)

        cli.send("0")
        cli.read_until("You entered: 0")
        cli.read_until("[1] Exit")
        check("You entered: 0" in cli.output, "First input is echoed", passed, failed)
        check("[0] World" in cli.output, "Second menu contains World button", passed, failed)
        check("[1] Exit" in cli.output, "Second menu contains Exit button", passed, failed)

        cli.send("0")
        cli.read_until("Agent Test End")
        check("Agent Test End" in cli.output, "Second input reaches end message", passed, failed)
        check("[1] Quit" not in cli.output.split("Agent Test End", 1)[-1], "End output does not keep previous menu", passed, failed)
    finally:
        cli.close()

    if failed[0]:
        print("\n--- Captured CLI output ---")
        sys.stdout.buffer.write(cli.output.encode(sys.stdout.encoding or "utf-8", errors="replace"))
        print()

    print(f"\n=== {args.suite_name}: {passed[0]} passed, {failed[0]} failed ===")
    return 0 if failed[0] == 0 else 1


if __name__ == "__main__":
    sys.exit(main())
