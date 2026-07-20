"""Shared helpers for Emuera.Headless server-mode tests."""
import json
import os
import shutil
import socket
import subprocess
import tempfile
import time
import urllib.error
import urllib.request
from pathlib import Path


PROJECT_DIR = Path(__file__).resolve().parents[1]
TEST_GAME_DIR = PROJECT_DIR / "test_game"


def find_binary(project_dir=None):
    """Auto-detect Emuera binary. Search order:
    1. EMUERA_BINARY environment variable
    2. Emuera.Headless/bin/Debug/.../Emuera.Headless.exe
    3. Emuera.Headless/bin/Release/.../Emuera.Headless.exe
    Returns (path, use_dotnet) tuple.
    """
    if project_dir is None:
        project_dir = PROJECT_DIR

    # 1. Environment variable
    env_binary = os.environ.get("EMUERA_BINARY")
    if env_binary and os.path.isfile(env_binary):
        use_dotnet = env_binary.endswith(".dll")
        return env_binary, use_dotnet

    # 2. Headless Debug
    headless_debug = os.path.join(
        project_dir, "Emuera.Headless", "bin", "Debug", "net10.0", "Emuera.Headless.exe"
    )
    if os.path.isfile(headless_debug):
        return headless_debug, False

    # 3. Headless Release
    headless_release = os.path.join(
        project_dir, "Emuera.Headless", "bin", "Release", "net10.0", "Emuera.Headless.exe"
    )
    if os.path.isfile(headless_release):
        return headless_release, False

    raise FileNotFoundError(
        f"Cannot find Emuera binary. Searched:\n"
        f"  - EMUERA_BINARY env var\n"
        f"  - {headless_debug}\n"
        f"  - {headless_release}\n"
        f"Set EMUERA_BINARY or build the project first."
    )


class ServerProcess:
    def __init__(self, proc, port):
        self.proc = proc
        self.port = port
        self.base_url = f"http://localhost:{port}"

    def request(self, method, path, body=None, timeout=35):
        data = None if body is None else json.dumps(body).encode("utf-8")
        headers = {}
        if body is not None:
            headers["Content-Type"] = "application/json"
        req = urllib.request.Request(
            f"{self.base_url}{path}",
            data=data,
            method=method,
            headers=headers,
        )
        try:
            with urllib.request.urlopen(req, timeout=timeout) as resp:
                return resp.status, resp.read().decode("utf-8")
        except urllib.error.HTTPError as e:
            return e.code, e.read().decode("utf-8")

    def create_session(self):
        return self.request("POST", "/session")

    def delete_session(self):
        return self.request("DELETE", "/session")

    def get_turn(self, timeout=35):
        return self.request("GET", "/turn", timeout=timeout)

    def post_input(self, value):
        return self.request("POST", "/input", {"value": value})

    def get_state(self):
        return self.request("GET", "/state")

    def get_snapshot(self, timeout=35):
        return self.request("GET", "/snapshot", timeout=timeout)

    def load_game(self, game_dir):
        """T-025：POST /load-game 辅助方法——issue 05 端点。"""
        return self.request("POST", "/load-game", {"gameDir": game_dir})

    def close(self):
        if self.proc.poll() is None:
            try:
                self.proc.stdin.write("\n")
                self.proc.stdin.flush()
            except Exception:
                pass
            try:
                self.proc.wait(timeout=5)
            except subprocess.TimeoutExpired:
                self.proc.kill()
                self.proc.wait(timeout=5)


def diff_text(turn):
    """Extract concatenated text from v5 turn diff.lineOps for text-content assertions.

    Returns empty string when diff is null/absent (first turn or no-op turn).
    For first-turn content checks, fetch GET /snapshot and use snapshot_text() instead.

    plan C (v5): diff.lineOps carries append/clear_line_diff/clear_screen.
    only append.newLines carry text. clear_line_diff (clearCount) and clear_screen are
    pure tail-cuts / full-clears (no text).
    """
    diff = turn.get("diff")
    if diff is None:
        return ""
    parts = []
    for op in diff.get("lineOps", []):
        op_type = op.get("type")
        if op_type == "append":
            lines = op.get("newLines", [])
        else:
            continue  # clear_line_diff / clear_screen are pure tail-cut / full-clear (no text)
        for line in lines:
            for entry in line.get("entries", []):
                for seg in entry.get("segments", []):
                    parts.append(seg.get("text", ""))
    return " ".join(parts)


def snapshot_text(snap):
    """Extract concatenated text from DisplaySnapshot lines[].entries[].segments[].text."""
    return " ".join(
        seg.get("text", "")
        for line in snap.get("lines", [])
        for entry in line.get("entries", [])
        for seg in entry.get("segments", [])
    )


def free_port():
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as sock:
        sock.bind(("127.0.0.1", 0))
        return sock.getsockname()[1]


def wait_for_port(port, timeout=30):
    deadline = time.time() + timeout
    while time.time() < deadline:
        try:
            with socket.create_connection(("localhost", port), timeout=0.2):
                return
        except OSError:
            time.sleep(0.2)
    raise TimeoutError(f"Server did not listen on port {port}")


def start_server(game_dir=None, project_dir=None, binary=None, port=None):
    """启动 Emuera.Headless server。

    T-025：game_dir 改为可选——None 时不传 --ExeDir，server 进入空闲启动模式
    （Program.Main 跳过 Validate，等待 /load-game 加载游戏）。
    """
    if project_dir is None:
        project_dir = PROJECT_DIR
    if port is None:
        port = free_port()

    binary_path, use_dotnet = find_binary(str(project_dir)) if binary is None else (binary, binary.endswith(".dll"))
    cmd = ["dotnet", "exec", binary_path] if use_dotnet else [binary_path]
    cmd.extend(["--server", "--port", str(port)])
    if game_dir is not None:
        cmd.extend(["--ExeDir", str(game_dir)])

    print(f"[EmueraServer] Starting: {' '.join(cmd)}")
    proc = subprocess.Popen(
        cmd,
        stdin=subprocess.PIPE,
        stdout=subprocess.PIPE,
        stderr=subprocess.STDOUT,
        text=True,
        encoding="utf-8",
        errors="replace",
    )
    try:
        wait_for_port(port)
    except Exception:
        proc.terminate()
        try:
            proc.wait(timeout=5)
        except subprocess.TimeoutExpired:
            proc.kill()
        raise

    return ServerProcess(proc, port)


def copy_test_game_with_erb(erb_text):
    temp_dir = tempfile.mkdtemp(prefix="emuera_server_test_")
    game_dir = Path(temp_dir) / "game"
    shutil.copytree(TEST_GAME_DIR, game_dir)
    (game_dir / "erb" / "TEST.ERB").write_text(erb_text, encoding="utf-8")
    return temp_dir, str(game_dir)
