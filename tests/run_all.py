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
import time
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
    # Brief pause between test suites on Windows to avoid file-lock races
    # (the .NET single-file host may briefly hold the exe after process exit)
    if sys.platform == "win32":
        time.sleep(1)
    return completed.returncode == 0, completed.returncode


def _safe_print(text):
    encoding = sys.stdout.encoding or "utf-8"
    for line in text.splitlines():
        print(line.encode(encoding, errors="replace").decode(encoding, errors="replace"))


def _run_cli_protocol(binary_path, game_dir):
    print(f"\n=== CLI protocol ===")
    return _run_script(
        "CLI protocol",
        [
            sys.executable,
            str(TESTS_DIR / "test_cli.py"),
            "--binary",
            str(binary_path),
            "--game-dir",
            str(game_dir),
        ],
        timeout=120,
    )


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--binary", help="Path to Emuera.Headless binary (exe or dll)")
    parser.add_argument("--game-dir", default="test_game", help="Path to game directory")
    parser.add_argument("--skip-cli-protocol", action="store_true", help="Skip CLI protocol test")
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

    if not args.skip_cli_protocol:
        results.append(("CLI protocol", _run_cli_protocol(str(binary_path), str(game_dir))))

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

    results.append(
        (
            "I-11 exit survival",
            _run_script(
                "I-11 exit survival",
                [sys.executable, str(TESTS_DIR / "test_force_quit_survival.py")],
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
