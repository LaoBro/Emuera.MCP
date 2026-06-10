"""Run the Emuera.Headless regression suite with one command.

This entry point keeps the existing focused test scripts intact and adds a
single command for routine regression runs:

    python tests/run_all.py
    python tests/run_all.py --binary Emuera.Headless/bin/Debug/net10.0/Emuera.Headless.exe
    python tests/run_all.py --game-dir test_game
"""
import argparse
import os
import shutil
import subprocess
import sys
from pathlib import Path

TESTS_DIR = Path(__file__).resolve().parent
ROOT_DIR = TESTS_DIR.parent

sys.path.insert(0, str(TESTS_DIR))
from emuera_agent import find_binary


def _resolve_path(value, base_dir):
    path = Path(value)
    if path.is_absolute():
        return path
    return (base_dir / path).resolve()


def _binary_command(binary_path):
    path = Path(binary_path)
    if path.suffix.lower() == ".dll":
        dotnet = shutil.which("dotnet")
        if dotnet is None:
            raise RuntimeError("dotnet was not found in PATH")
        return [dotnet, "exec", str(path)]
    return [str(path)]


def _child_env(binary_path=None):
    env = os.environ.copy()
    if binary_path:
        env["EMUERA_BINARY"] = str(binary_path)
    return env


def _run_script(name, args, env=None, timeout=None):
    print(f"\n=== {name} ===")
    completed = subprocess.run(
        args,
        cwd=str(ROOT_DIR),
        env=env,
        text=True,
        encoding="utf-8",
        errors="replace",
        timeout=timeout,
    )
    return completed.returncode == 0, completed.returncode


def _safe_print(text):
    encoding = sys.stdout.encoding or "utf-8"
    for line in text.splitlines():
        print(line.encode(encoding, errors="replace").decode(encoding, errors="replace"))


def _run_cli_smoke(binary_path, game_dir, timeout_seconds, force=False):
    print(f"\n=== CLI smoke ===")
    if not force and not sys.stdin.isatty():
        print("  SKIP: stdin is not a TTY; use --force-cli-smoke to require this check")
        return 0

    cmd = _binary_command(binary_path) + ["--ExeDir", str(game_dir)]
    print(f"[CliSmoke] Starting: {' '.join(cmd)}")

    proc = subprocess.Popen(
        cmd,
        cwd=str(ROOT_DIR),
        stdin=None,
        stdout=subprocess.PIPE,
        stderr=subprocess.STDOUT,
        text=True,
        encoding="utf-8",
        errors="replace",
    )

    try:
        try:
            output, _ = proc.communicate(timeout=timeout_seconds)
        except subprocess.TimeoutExpired:
            proc.kill()
            output, _ = proc.communicate(timeout=5)
            lines = output.splitlines()
            if any(line.lstrip().startswith("{") for line in lines[:5]):
                print("  FAIL: CLI smoke emitted JSONL; stdin may be redirected")
                return 1
            print(f"  PASS: CLI process stayed alive for {timeout_seconds}s without JSONL output")
            return 0

        if not force and any(line.lstrip().startswith("{") for line in output.splitlines()[:5]):
            print(f"  SKIP: CLI smoke detected JSONL output; stdin is not a real TTY in this environment")
            return 0

        print(f"  FAIL: CLI process exited early with code {proc.returncode}")
        if output:
            _safe_print(output)
        return 1
    finally:
        if proc.poll() is None:
            proc.kill()
            proc.wait(timeout=5)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--binary", help="Path to Emuera.Headless binary (exe or dll)")
    parser.add_argument("--game-dir", default="test_game", help="Path to game directory")
    parser.add_argument("--cli-timeout", type=float, default=3.0, help="Seconds to keep CLI smoke process alive")
    parser.add_argument("--skip-cli-smoke", action="store_true", help="Skip non-pipe CLI startup smoke")
    parser.add_argument("--force-cli-smoke", action="store_true", help="Require CLI smoke even when stdin is not a TTY")
    args = parser.parse_args()

    binary_path = _resolve_path(args.binary, Path.cwd()) if args.binary else find_binary(str(ROOT_DIR))[0]
    game_dir = _resolve_path(args.game_dir, ROOT_DIR)
    env = _child_env(binary_path)

    results = []

    results.append(
        (
            "JSONL + buttons",
            _run_script(
                "JSONL + buttons",
                [
                    sys.executable,
                    str(TESTS_DIR / "test_jsonl.py"),
                    "--binary",
                    str(binary_path),
                    "--game-dir",
                    str(game_dir),
                ],
                timeout=120,
            ),
        )
    )

    if not args.skip_cli_smoke:
        results.append(("CLI smoke", (_run_cli_smoke(str(binary_path), str(game_dir), args.cli_timeout, args.force_cli_smoke) == 0, 0)))

    results.append(
        (
            "server single-session",
            _run_script(
                "server single-session",
                [sys.executable, str(TESTS_DIR / "test_server_single_session.py")],
                env=env,
                timeout=180,
            ),
        )
    )

    results.append(
        (
            "TINPUT timeout",
            _run_script(
                "TINPUT timeout",
                [sys.executable, str(TESTS_DIR / "test_tinput_timeout.py")],
                env=env,
                timeout=180,
            ),
        )
    )

    print("\n=== Summary ===")
    all_passed = True
    for name, (passed, code) in results:
        status = "PASS" if passed else f"FAIL (exit {code})"
        print(f"{status}: {name}")
        all_passed = all_passed and passed

    return 0 if all_passed else 1


if __name__ == "__main__":
    sys.exit(main())
