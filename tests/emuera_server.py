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

from emuera_agent import find_binary


PROJECT_DIR = Path(__file__).resolve().parents[1]
TEST_GAME_DIR = PROJECT_DIR / "test_game"


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
        return self.request("POST", "/sessions")

    def delete_session(self, session_id):
        return self.request("DELETE", f"/sessions/{session_id}")

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


def start_server(game_dir, project_dir=None, binary=None, port=None):
    if project_dir is None:
        project_dir = PROJECT_DIR
    if port is None:
        port = free_port()

    binary_path, use_dotnet = find_binary(str(project_dir)) if binary is None else (binary, binary.endswith(".dll"))
    cmd = ["dotnet", "exec", binary_path] if use_dotnet else [binary_path]
    cmd.extend(["--server", "--port", str(port), "--ExeDir", str(game_dir)])

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
