"""Emuera Agent - minimal Python wrapper for automated testing.

Usage:
    from emuera_agent import EmueraAgent

    # Auto-detect binary (searches Emuera.Headless/bin then Emuera/artifacts)
    with EmueraAgent("test_game") as game:
        assert game.state == "WaitInput"
        game.step("0")
        assert "Hello" in game.text

    # Or specify binary explicitly
    with EmueraAgent("test_game", binary="path/to/Emuera.Headless.exe") as game:
        game.step("0")

    # Or use environment variable
    # set EMUERA_BINARY=path/to/Emuera.Headless.exe
    with EmueraAgent("test_game") as game:
        game.step("0")
"""
import subprocess, json, os, sys, glob


def find_binary(project_dir=None):
    """Auto-detect Emuera binary. Search order:
    1. EMUERA_BINARY environment variable
    2. Emuera.Headless/bin/Debug/.../Emuera.Headless.exe
    3. Emuera.Headless/bin/Release/.../Emuera.Headless.exe
    4. Emuera/artifacts/bin/Emuera/debug-naudio/Emuera.dll (legacy)
    Returns (path, use_dotnet) tuple.
    """
    if project_dir is None:
        project_dir = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

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

    # 4. Legacy WinForms dll
    legacy_dll = os.path.join(
        project_dir, "Emuera", "artifacts", "bin", "Emuera", "debug-naudio", "Emuera.dll"
    )
    if os.path.isfile(legacy_dll):
        return legacy_dll, True

    raise FileNotFoundError(
        f"Cannot find Emuera binary. Searched:\n"
        f"  - EMUERA_BINARY env var\n"
        f"  - {headless_debug}\n"
        f"  - {headless_release}\n"
        f"  - {legacy_dll}\n"
        f"Set EMUERA_BINARY or build the project first."
    )


class EmueraAgent:
    def __init__(self, game_dir, project_dir=None, binary=None):
        if project_dir is None:
            project_dir = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
        if not os.path.isabs(game_dir):
            game_dir = os.path.join(project_dir, game_dir)
        self.game_dir = game_dir
        self.project_dir = project_dir
        self.text = ""
        self.state = ""
        self.input_type = ""
        self.need_value = False
        self.buttons = []
        self._proc = None
        self._binary = binary
        self._use_dotnet = False

    def start(self):
        """Start Emuera in headless mode and read the initial turn."""
        if self._binary:
            binary_path = self._binary
            self._use_dotnet = binary_path.endswith(".dll")
        else:
            binary_path, self._use_dotnet = find_binary(self.project_dir)

        if self._use_dotnet:
            cmd = ["dotnet", "exec", binary_path, "--ExeDir", self.game_dir, "--protocol", "jsonl"]
        else:
            cmd = [binary_path, "--ExeDir", self.game_dir, "--protocol", "jsonl"]

        print(f"[EmueraAgent] Starting: {' '.join(cmd)}")
        self._proc = subprocess.Popen(
            cmd,
            stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
            text=True, encoding="utf-8"
        )
        self._read_turn()

    def step(self, value):
        """Send input and wait for next turn. Returns self for chaining."""
        if self._proc is None:
            raise RuntimeError("Not started")
        self._proc.stdin.write(json.dumps({"type": "input", "value": value}) + "\n")
        self._proc.stdin.flush()
        self._read_turn()
        return self

    def _read_turn(self):
        """Read one JSON line from stdout and update state."""
        line = self._proc.stdout.readline()
        if not line:
            self.state = "Disconnected"
            return
        turn = json.loads(line)
        self._raw = turn
        self.text = turn.get("text", "")
        self.state = turn.get("state", "")
        self.input_type = turn.get("inputType", "")
        self.need_value = turn.get("needValue", False)
        self.buttons = turn.get("buttons", [])

    def close(self):
        """Shut down Emuera."""
        if self._proc:
            try:
                self._proc.kill()
                self._proc.wait(timeout=5)
            except:
                pass
            self._proc = None

    def __enter__(self):
        self.start()
        return self

    def __exit__(self, *args):
        self.close()

    @property
    def finished(self):
        """True when game has ended (Quit or Error)."""
        return self.state in ("Quit", "Error")
