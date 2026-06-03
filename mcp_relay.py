"""MCP Relay: lightweight MCP server that starts Emuera on demand.

Talks MCP (JSON-RPC 2.0) to Claude Code on stdin/stdout.
Talks JSONL to Emuera internally (simple {"type":"input","value":"..."} protocol).

Always runs (no window). Launches the game process only when a tool is called.
"""
import subprocess
import json
import os
import sys
import threading

PROJECT_DIR = os.path.dirname(os.path.abspath(__file__))
CONFIG_FILE = os.path.join(PROJECT_DIR, ".emuera-mcp.json")

STARTUP_TIMEOUT = 30  # seconds for game to reach first WaitInput
TURN_TIMEOUT = 30     # seconds for game to respond to a step

_game_proc = None     # subprocess.Popen or None
_last_turn = None     # cached last turn JSON from game


def _load_config():
    """Load config from .emuera-mcp.json. Returns dict or None if missing/unreadable."""
    if not os.path.isfile(CONFIG_FILE):
        return None
    try:
        with open(CONFIG_FILE, "r", encoding="utf-8") as f:
            return json.load(f)
    except (json.JSONDecodeError, OSError):
        return None


def _resolve_path(path):
    """Resolve a relative path against PROJECT_DIR; absolute paths pass through."""
    if os.path.isabs(path):
        return path
    return os.path.join(PROJECT_DIR, path)


def _save_config(config):
    """Save config dict to .emuera-mcp.json."""
    with open(CONFIG_FILE, "w", encoding="utf-8") as f:
        json.dump(config, f, indent=2, ensure_ascii=False)


def _kill_game():
    """Kill the game process and clean up state."""
    global _game_proc, _last_turn
    if _game_proc is None:
        return
    try:
        _game_proc.stdin.close()
    except Exception:
        pass
    try:
        _game_proc.kill()
    except Exception:
        pass
    try:
        _game_proc.wait(timeout=5)
    except Exception:
        pass
    _game_proc = None
    _last_turn = None


def _read_game_line(timeout):
    """Read a single line from game stdout with timeout. Returns None on timeout/EOF."""
    result = [None]

    def _read():
        try:
            result[0] = _game_proc.stdout.readline()
        except Exception:
            result[0] = None

    t = threading.Thread(target=_read, daemon=True)
    t.start()
    t.join(timeout)
    if t.is_alive():
        return None
    line = result[0]
    if not line:
        return None
    return line.strip()


def _start_game():
    """Launch Emuera in JSONL mode and read the initial turn.
    Returns None on success, error string on failure."""
    global _game_proc, _last_turn

    if _game_proc is not None:
        if _game_proc.poll() is not None:
            _kill_game()
        elif _last_turn is not None:
            return None  # Already running and ready
        else:
            _kill_game()

    config = _load_config()
    if config is None:
        return ("Binary path not configured. "
                "Use emuera_set_config to set binaryPath and gameDir first.")
    binary_path = config.get("binaryPath") or config.get("dllPath")
    game_dir = config.get("gameDir")
    if not binary_path:
        return "binaryPath is not set in .emuera-mcp.json"
    if not game_dir:
        return "gameDir is not set in .emuera-mcp.json"

    binary_path = _resolve_path(binary_path)
    game_dir = _resolve_path(game_dir)

    if not os.path.isfile(binary_path):
        return (f"Binary file not found: {binary_path}. "
                "Rebuild or update binaryPath via emuera_set_config.")
    if not os.path.isdir(game_dir):
        return (f"Game directory not found: {game_dir}. "
                "Update gameDir via emuera_set_config.")

    is_exe = binary_path.lower().endswith(".exe")
    if is_exe:
        cmd = [binary_path, "--ExeDir", game_dir]
    else:
        cmd = ["dotnet", "exec", binary_path, "--ExeDir", game_dir]

    try:
        _game_proc = subprocess.Popen(
            cmd,
            stdin=subprocess.PIPE,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True, encoding="utf-8"
        )
    except FileNotFoundError:
        if is_exe:
            return (f"Binary not found: {binary_path}. "
                    "Update binaryPath via emuera_set_config.")
        else:
            return ("dotnet executable not found. "
                    "Ensure the .NET SDK or runtime is installed and on PATH.")

    # JSONL: game outputs the initial turn automatically via OnStart
    line = _read_game_line(STARTUP_TIMEOUT)
    if line is None:
        rc = _game_proc.poll()
        _kill_game()
        if rc is not None:
            return f"Game process exited with code {rc}"
        return "Game did not produce initial output within timeout"

    try:
        _last_turn = json.loads(line)
    except json.JSONDecodeError:
        _kill_game()
        return f"Invalid JSON from game: {line[:100]}"

    return None


def _step_game(value=None):
    """Send input to game via JSONL and read the next turn.
    Returns turn dict on success, or error string on failure."""
    global _last_turn

    cmd = {"type": "input", "value": value if value is not None else ""}
    try:
        _game_proc.stdin.write(json.dumps(cmd) + "\n")
        _game_proc.stdin.flush()
    except (BrokenPipeError, OSError) as e:
        _kill_game()
        return f"Game process pipe broken: {e}"

    line = _read_game_line(TURN_TIMEOUT)
    if line is None:
        if _game_proc is not None and _game_proc.poll() is not None:
            rc = _game_proc.returncode
            _kill_game()
            return f"Game process exited with code {rc}"
        _kill_game()
        return "Game did not respond within timeout"

    try:
        turn = json.loads(line)
    except json.JSONDecodeError:
        return f"Invalid JSON from game: {line[:100]}"

    _last_turn = turn

    # Auto-cleanup if game has ended
    if turn.get("state") in ("Quit", "Error"):
        _kill_game()

    return turn


# ─── MCP protocol handlers ────────────────────────────────────────────────────

def _handle_request(req_id, method, params):
    """Handle a single JSON-RPC request. Returns (response_dict, is_notification)."""
    if method == "initialize":
        return {"jsonrpc": "2.0", "id": req_id, "result": {
            "protocolVersion": "2024-11-05",
            "capabilities": {"tools": {}},
            "serverInfo": {"name": "emuera", "version": "1.0"}
        }}, False

    elif method == "notifications/initialized":
        return None, True

    elif method == "tools/list":
        return {"jsonrpc": "2.0", "id": req_id, "result": {
            "tools": [
                {
                    "name": "emuera_step",
                    "description": "Submit input and wait for next game output turn",
                    "inputSchema": {
                        "type": "object",
                        "properties": {
                            "value": {"type": "string",
                                       "description": "Input value; omit to read current state only"}
                        }
                    }
                },
                {
                    "name": "emuera_get_state",
                    "description": "Wait for game to reach WaitInput and return state + output text (blocking until ready)",
                    "inputSchema": {"type": "object", "properties": {}}
                },
                {
                    "name": "emuera_kill",
                    "description": "Force kill the Emuera game process and close its window",
                    "inputSchema": {"type": "object", "properties": {}}
                },
                {
                    "name": "emuera_set_config",
                    "description": "Set binary path and/or game directory for Emuera. Validates paths before saving. Both parameters are optional — omit to keep current value.",
                    "inputSchema": {
                        "type": "object",
                        "properties": {
                            "binaryPath": {
                                "type": "string",
                                "description": "Path to Emuera binary (.dll or .exe, relative to project root or absolute)"
                            },
                            "dllPath": {
                                "type": "string",
                                "description": "(Legacy) Path to Emuera.dll — use binaryPath instead"
                            },
                            "gameDir": {
                                "type": "string",
                                "description": "Game data directory path (relative to project root or absolute)"
                            }
                        }
                    }
                },
                {
                    "name": "emuera_get_config",
                    "description": "Show current binary path and game directory configuration from .emuera-mcp.json.",
                    "inputSchema": {
                        "type": "object",
                        "properties": {}
                    }
                }
            ]
        }}, False

    elif method == "tools/call":
        tool_name = params.get("name", "")
        args = params.get("arguments", {})

        # emuera_get_config: return current config (no game needed)
        if tool_name == "emuera_get_config":
            config = _load_config()
            if config is None:
                config = {"binaryPath": None, "gameDir": None, "configured": False}
            else:
                config["configured"] = True
                if "dllPath" in config and "binaryPath" not in config:
                    config["binaryPath"] = config.pop("dllPath")
                elif "dllPath" in config:
                    del config["dllPath"]
            return {"jsonrpc": "2.0", "id": req_id, "result": {
                "content": [{"type": "text", "text": json.dumps(config)}]
            }}, False

        # emuera_set_config: validate and save paths (no game needed)
        if tool_name == "emuera_set_config":
            new_binary = args.get("binaryPath") or args.get("dllPath")
            new_game_dir = args.get("gameDir")

            if new_binary is not None:
                resolved = _resolve_path(new_binary)
                if not os.path.isfile(resolved):
                    return {"jsonrpc": "2.0", "id": req_id, "error": {
                        "code": -32000,
                        "message": f"Binary file not found: {resolved}"
                    }}, False
            if new_game_dir is not None:
                resolved = _resolve_path(new_game_dir)
                if not os.path.isdir(resolved):
                    return {"jsonrpc": "2.0", "id": req_id, "error": {
                        "code": -32000,
                        "message": f"Game directory not found: {resolved}"
                    }}, False

            config = _load_config() or {}
            if new_binary is not None:
                config["binaryPath"] = new_binary
            elif "dllPath" in config and "binaryPath" not in config:
                config["binaryPath"] = config.pop("dllPath")
            if "binaryPath" in config:
                config.pop("dllPath", None)
            if new_game_dir is not None:
                config["gameDir"] = new_game_dir
            _save_config(config)

            return {"jsonrpc": "2.0", "id": req_id, "result": {
                "content": [{"type": "text", "text": json.dumps({
                    "saved": True, "binaryPath": config.get("binaryPath"),
                    "gameDir": config.get("gameDir")
                })}]
            }}, False

        # emuera_kill: force kill without starting game
        if tool_name == "emuera_kill":
            was_running = _game_proc is not None
            _kill_game()
            return {"jsonrpc": "2.0", "id": req_id, "result": {
                "content": [{"type": "text", "text": json.dumps({
                    "killed": was_running,
                    "message": "Game process terminated" if was_running else "No game was running"
                })}]
            }}, False

        # emuera_get_state: return cached turn (start game if needed)
        if tool_name == "emuera_get_state":
            err = _start_game()
            if err:
                return {"jsonrpc": "2.0", "id": req_id, "error": {
                    "code": -32000, "message": f"Failed to start game: {err}"
                }}, False
            return {"jsonrpc": "2.0", "id": req_id, "result": {
                "content": [{"type": "text", "text": json.dumps(_last_turn)}]
            }}, False

        # emuera_step: submit input to game
        if tool_name == "emuera_step":
            err = _start_game()
            if err:
                return {"jsonrpc": "2.0", "id": req_id, "error": {
                    "code": -32000, "message": f"Failed to start game: {err}"
                }}, False

            value = args.get("value")
            result = _step_game(value)
            if isinstance(result, str):
                return {"jsonrpc": "2.0", "id": req_id, "error": {
                    "code": -32000, "message": result
                }}, False

            return {"jsonrpc": "2.0", "id": req_id, "result": {
                "content": [{"type": "text", "text": json.dumps(result)}]
            }}, False

        return {"jsonrpc": "2.0", "id": req_id, "error": {
            "code": -32601, "message": f"Unknown tool: {tool_name}"
        }}, False

    else:
        return {"jsonrpc": "2.0", "id": req_id, "error": {
            "code": -32601, "message": f"Method not found: {method}"
        }}, False


def main():
    # Read requests line by line from stdin, write responses to stdout
    for line in sys.stdin:
        line = line.strip()
        if not line:
            continue

        try:
            req = json.loads(line)
        except json.JSONDecodeError:
            continue

        req_id = req.get("id", None)
        method = req.get("method", "")
        params = req.get("params", {})

        resp, is_notification = _handle_request(req_id, method, params)

        if resp is not None:
            sys.stdout.write(json.dumps(resp) + "\n")
            sys.stdout.flush()


if __name__ == "__main__":
    try:
        main()
    finally:
        _kill_game()
