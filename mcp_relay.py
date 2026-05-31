"""MCP Relay: lightweight MCP server that starts Emuera on demand.

Always runs (no window). Launches the game process only when a tool is called.
"""
import subprocess
import json
import os
import sys
import signal
import time

PROJECT_DIR = os.path.dirname(os.path.abspath(__file__))
CONFIG_FILE = os.path.join(PROJECT_DIR, ".emuera-mcp.json")

STARTUP_TIMEOUT = 30  # seconds for MCP handshake with game
TURN_TIMEOUT = 30     # seconds for game to respond to a tool call

_game_proc = None     # subprocess.Popen or None
_game_ready = False   # True after MCP handshake completed


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
    global _game_proc, _game_ready
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
    _game_ready = False


def _start_game():
    """Launch Emuera in agent mode and complete MCP handshake. Returns None on success, error string on failure."""
    global _game_proc, _game_ready

    if _game_proc is not None:
        # Check if existing process is still alive AND ready
        if _game_proc.poll() is not None or not _game_ready:
            _kill_game()
        else:
            return None  # Already running

    config = _load_config()
    if config is None:
        return ("DLL path not configured. "
                "Use emuera_set_config to set dllPath and gameDir first.")
    dll_path = config.get("dllPath")
    game_dir = config.get("gameDir")
    if not dll_path:
        return "dllPath is not set in .emuera-mcp.json"
    if not game_dir:
        return "gameDir is not set in .emuera-mcp.json"

    dll_path = _resolve_path(dll_path)
    game_dir = _resolve_path(game_dir)

    if not os.path.isfile(dll_path):
        return (f"DLL file not found: {dll_path}. "
                "Rebuild or update dllPath via emuera_set_config.")
    if not os.path.isdir(game_dir):
        return (f"Game directory not found: {game_dir}. "
                "Update gameDir via emuera_set_config.")

    try:
        _game_proc = subprocess.Popen(
            ["dotnet", "exec", dll_path, "--ExeDir", game_dir],
            stdin=subprocess.PIPE,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True, encoding="utf-8"
        )
    except FileNotFoundError:
        return (f"DLL file not found: {dll_path}. "
                "Rebuild or update dllPath via emuera_set_config.")

    # MCP handshake with game
    try:
        handshake = json.dumps({
            "jsonrpc": "2.0", "id": 0, "method": "initialize",
            "params": {
                "protocolVersion": "2024-11-05",
                "capabilities": {},
                "clientInfo": {"name": "relay", "version": "1.0"}
            }
        }) + "\n"
        _game_proc.stdin.write(handshake)
        _game_proc.stdin.flush()

        _game_proc.stdin.write(json.dumps({
            "jsonrpc": "2.0", "method": "notifications/initialized"
        }) + "\n")
        _game_proc.stdin.flush()

        # Read initialize response (with timeout)
        line = _read_game_line(STARTUP_TIMEOUT)
        if line is None:
            _kill_game()
            return "Game did not respond to initialize handshake within timeout"
        resp = json.loads(line)
        if "error" in resp:
            _kill_game()
            return f"Game rejected handshake: {resp['error'].get('message', 'unknown')}"
    except Exception as e:
        _kill_game()
        return f"Handshake failed: {e}"

    _game_ready = True
    return None


def _read_game_line(timeout):
    """Read a single line from game stdout with timeout. Returns None on timeout/EOF."""
    import threading
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


def _send_game(method, params=None, id_val=1):
    """Send a JSON-RPC request to the game and return the response dict, or error dict."""
    req = {"jsonrpc": "2.0", "id": id_val, "method": method, "params": params or {}}
    try:
        _game_proc.stdin.write(json.dumps(req) + "\n")
        _game_proc.stdin.flush()
    except (BrokenPipeError, OSError) as e:
        _kill_game()
        return {"error": {"code": -32000, "message": f"Game process pipe broken: {e}"}}

    line = _read_game_line(TURN_TIMEOUT)
    if line is None:
        # Check if process died
        if _game_proc is not None and _game_proc.poll() is not None:
            rc = _game_proc.returncode
            _game_ready = False
            return {"error": {"code": -32000, "message": f"Game process exited with code {rc}"}}
        _kill_game()
        return {"error": {"code": -32000, "message": "Game did not respond within timeout"}}

    try:
        return json.loads(line)
    except json.JSONDecodeError:
        return {"error": {"code": -32000, "message": f"Invalid JSON from game: {line[:100]}"}}


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
                    "description": "Set DLL path and/or game directory for Emuera. Validates paths before saving. Both parameters are optional — omit to keep current value.",
                    "inputSchema": {
                        "type": "object",
                        "properties": {
                            "dllPath": {
                                "type": "string",
                                "description": "Path to Emuera.dll (relative to project root or absolute)"
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
                    "description": "Show current DLL path and game directory configuration from .emuera-mcp.json.",
                    "inputSchema": {
                        "type": "object",
                        "properties": {}
                    }
                }
            ]
        }}, False

    elif method == "tools/call":
        tool_name = params.get("name", "")

        # emuera_get_config: return current config (no game needed)
        if tool_name == "emuera_get_config":
            config = _load_config()
            if config is None:
                config = {"dllPath": None, "gameDir": None, "configured": False}
            else:
                config["configured"] = True
            return {"jsonrpc": "2.0", "id": req_id, "result": {
                "content": [{"type": "text", "text": json.dumps(config)}]
            }}, False

        # emuera_set_config: validate and save paths (no game needed)
        if tool_name == "emuera_set_config":
            args = params.get("arguments", {})
            new_dll = args.get("dllPath")
            new_game_dir = args.get("gameDir")

            if new_dll is not None:
                resolved = _resolve_path(new_dll)
                if not os.path.isfile(resolved):
                    return {"jsonrpc": "2.0", "id": req_id, "error": {
                        "code": -32000,
                        "message": f"DLL file not found: {resolved}"
                    }}, False
            if new_game_dir is not None:
                resolved = _resolve_path(new_game_dir)
                if not os.path.isdir(resolved):
                    return {"jsonrpc": "2.0", "id": req_id, "error": {
                        "code": -32000,
                        "message": f"Game directory not found: {resolved}"
                    }}, False

            config = _load_config() or {}
            if new_dll is not None:
                config["dllPath"] = new_dll
            if new_game_dir is not None:
                config["gameDir"] = new_game_dir
            _save_config(config)

            return {"jsonrpc": "2.0", "id": req_id, "result": {
                "content": [{"type": "text", "text": json.dumps({
                    "saved": True, "dllPath": config.get("dllPath"),
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

        # Ensure game is running
        err = _start_game()
        if err:
            return {"jsonrpc": "2.0", "id": req_id, "error": {
                "code": -32000, "message": f"Failed to start game: {err}"
            }}, False

        # Forward tool call to game
        resp = _send_game("tools/call", params, req_id)
        if "error" in resp:
            return {"jsonrpc": "2.0", "id": req_id, "error": resp["error"]}, False

        # Check if game has quit and clean up
        try:
            content = resp.get("result", {}).get("content", [])
            if content:
                inner = json.loads(content[0]["text"])
                if inner.get("state") in ("Quit", "Error"):
                    _kill_game()
        except (json.JSONDecodeError, KeyError, IndexError):
            pass

        return {"jsonrpc": "2.0", "id": req_id, "result": resp.get("result", {})}, False

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
