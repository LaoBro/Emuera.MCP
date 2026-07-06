"""Verify ConPTY behavior with minimal verification project.

Builds and runs tools/VerifyConPTY via pywinpty, captures output,
parses step results, and prints a diagnostic summary.

Usage:
    python tests/verify_conpty.py
"""

import sys
import threading
import time
import subprocess
from pathlib import Path

HAS_WINPTY = False
try:
    from winpty import PtyProcess
    HAS_WINPTY = True
except ImportError:
    pass

ROOT = Path(__file__).resolve().parents[1]
PROJECT_DIR = ROOT / "tools" / "VerifyConPTY"
BINARY = PROJECT_DIR / "bin" / "Debug" / "net10.0" / "VerifyConPTY.exe"

PASS = 0
FAIL = 0


def build() -> bool:
    print("=== Build VerifyConPTY ===")
    result = subprocess.run(
        ["dotnet", "build", str(PROJECT_DIR / "VerifyConPTY.csproj"), "-c", "Debug", "--nologo"],
        capture_output=True, text=True, cwd=str(PROJECT_DIR), timeout=60,
    )
    if result.returncode != 0:
        print("Build FAILED:")
        for line in result.stderr.splitlines():
            print(f"  {line}")
        for line in result.stdout.splitlines():
            print(f"  {line}")
        return False
    print("Build OK\n")
    return True


def run() -> str:
    print("=== Run via pywinpty ===")
    proc = PtyProcess.spawn([str(BINARY)])
    output: list[str] = []
    stop = False

    def reader():
        while not stop:
            try:
                data = proc.read()
                if data:
                    output.append(data)
            except Exception:
                break

    t = threading.Thread(target=reader, daemon=True)
    t.start()

    input_sent = False

    try:
        deadline = time.time() + 20
        sent_time = None
        while time.time() < deadline:
            current = "".join(output)
            if "WAITING_FOR_INPUT" in current and not input_sent:
                input_sent = True
                sent_time = time.time()
                time.sleep(0.3)
                proc.write("HELLO\r")
                print("  [sent HELLO\\r]")
            if not proc.isalive():
                print("  [process exited]")
                break
            # After sending input, wait for process to exit or 8s timeout
            if sent_time and time.time() - sent_time > 8:
                print("  [timeout after input]")
                break
            time.sleep(0.3)

        full = "".join(output)
        print(f"Captured {len(full)} bytes\n")
        return full
    finally:
        stop = True
        if proc.isalive():
            proc.terminate()


def analyze(text: str):
    global PASS, FAIL

    steps = [
        ("Step 0: Environment", ["OS:", ".NET:", "Console.IsInputRedirected"]),
        ("Step 1: stdin handle", ["GetConsoleMode(stdin) = 0x"]),
        ("Step 2: SetConsoleMode(stdin)", ["SetConsoleMode(VT_INPUT) = OK"]),
        ("Step 3: WaitForSingleObject", ["WaitForSingleObject(stdin, 0) ="]),
        ("Step 4: Console.KeyAvailable", ["ReadKey:", "gotKey=True"]),
        ("Step 5: stdout console mode", ["GetConsoleMode(stdout) = 0x"]),
        ("Step 6: DA1 probe", ["DA1 result: responded="]),
    ]

    print("\n=== Step-by-step Results ===\n")
    for label, markers in steps:
        ok = all(m in text for m in markers)
        if ok:
            print(f"  PASS: {label}")
            PASS += 1
        else:
            print(f"  FAIL: {label}")
            FAIL += 1
            for m in markers:
                if m not in text:
                    print(f"        missing: {m!r}")

    # Detailed extraction
    print("\n=== Key Extracted Values ===\n")
    lines = text.splitlines()
    for l in lines:
        l = l.strip()
        if any(kw in l for kw in [
            "IsInputRedirected", "IsOutputRedirected",
            "GetConsoleMode(stdin)", "GetConsoleMode(stdout)",
            "VT test:", "DA1 result:", "gotKey=", "keyCount=",
            "Signaled", "Flushed", "Raw response hex",
            "SetConsoleMode(VT_INPUT)", "SetConsoleMode(raw",
            "WAITING_FOR_INPUT", "ReadKey:", "hStdin", "hStdout",
        ]):
            print(f"  {l}")


def summary():
    print(f"\n=== Summary: {PASS} passed, {FAIL} failed ===")
    if FAIL > 0:
        print("\n=== Diagnostic Suggestions ===")
        sys.exit(1)
    else:
        print("All checks passed — ConPTY behaves as expected.")
        sys.exit(0)


def main():
    if not HAS_WINPTY:
        print("Skipped: pywinpty not available (pip install pywinpty)")
        sys.exit(0)
    if not build():
        sys.exit(1)
    output = run()
    analyze(output)
    summary()


if __name__ == "__main__":
    main()
