"""VT-only path regression test (ADR-0005).

Verifies that CLI mode rejects stdin-redirected environments with a non-zero
exit code and a clear stderr message containing cause + remediation hint.

Per ADR-0005 the CLI protocol layer is VT-only. `TryPrepareVtInput` returns
false when stdin is redirected, which must trigger `HeadlessFatalException`
caught by `HeadlessRunner.RunAsync` → stderr + AgentLog + exit(1).

This test runs on any platform (no ConPTY/pywinpty dependency). It only checks
external behavior (exit code + stderr text), not internal state.
"""
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "tests"))

from emuera_server import find_binary

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


def test_cli_rejects_stdin_redirect():
    """`--protocol cli` with stdin redirected → non-zero exit + stderr hint.

    stdin=PIPE gives the child a redirected stdin handle, so
    `Console.IsInputRedirected` returns true and `TryPrepareVtInput` returns
    false. Pre-ADR-0005 this would silently fall back to ConsoleKey mode;
    post-ADR-0005 it must fatal-exit.
    """
    print("\n--- Test: --protocol cli rejects redirected stdin ---")
    binary_path, use_dotnet = find_binary(str(ROOT))
    cmd = (
        ["dotnet", "exec", binary_path] if use_dotnet else [binary_path]
    )
    cmd.extend(["--ExeDir", str(ROOT / "test_game"), "--protocol", "cli"])

    try:
        proc = subprocess.Popen(
            cmd,
            stdin=subprocess.PIPE,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
            encoding="utf-8",
            errors="replace",
        )
    except FileNotFoundError as e:
        check(False, f"failed to spawn CLI binary: {e}")
        return

    try:
        # Give the process time to fail fast; should exit well under 10s.
        try:
            stdout, stderr = proc.communicate(input="", timeout=15)
        except subprocess.TimeoutExpired:
            proc.kill()
            stdout, stderr = proc.communicate(timeout=5)
            check(False, "CLI did not exit within 15s on stdin redirect")
            return

        check(proc.returncode != 0,
              f"CLI exits non-zero on stdin redirect (got exit {proc.returncode})")

        # stderr should mention the fatal cause and remediation.
        # Cause: "VT 终端初始化失败" or "stdin 被重定向"
        cause_hit = ("VT" in stderr and "失败" in stderr) or "stdin" in stderr.lower()
        check(cause_hit, "stderr contains failure cause (VT init / stdin redirect)")

        # Remediation: should mention server mode or interactive terminal.
        remediation_hit = (
            "--server" in stderr
            or "交互式终端" in stderr
            or "interactive" in stderr.lower()
        )
        check(remediation_hit, "stderr contains remediation hint (server mode / interactive terminal)")

        # The HeadlessRunner header marker must appear (confirms we reached
        # the catch block, not an earlier unrelated crash).
        check("[headless] 致命错误" in stderr,
              "stderr contains HeadlessRunner fatal marker")
    finally:
        if proc.poll() is None:
            proc.kill()
            try:
                proc.wait(timeout=5)
            except subprocess.TimeoutExpired:
                pass


def main():
    test_cli_rejects_stdin_redirect()
    print(f"\n=== VT-only tests: {passed} passed, {failed} failed ===")
    sys.exit(1 if failed else 0)


if __name__ == "__main__":
    main()
