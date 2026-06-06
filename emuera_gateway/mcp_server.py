"""MCP JSON-RPC 2.0 server over stdin/stdout."""
import json
import sys
from typing import Any, Dict, Optional, Tuple
from .session import SessionManager
from .config import load_config, resolve_path, save_config
import os


class McpServer:
    """MCP JSON-RPC 2.0 server over stdin/stdout."""

    def __init__(self, session_manager: SessionManager):
        self.sm = session_manager

    def handle(self, req: Dict[str, Any]) -> Tuple[Optional[Dict[str, Any]], bool]:
        """Returns (response, is_notification)."""
        req_id = req.get("id")
        method = req.get("method", "")
        params = req.get("params", {})

        if method == "initialize":
            return self._initialize(req_id), False
        if method == "notifications/initialized":
            return None, True
        if method == "tools/list":
            return self._tools_list(req_id), False
        if method == "tools/call":
            return self._tools_call(req_id, params), False
        return self._error(req_id, -32601, f"Method not found: {method}"), False

    def _initialize(self, req_id):
        return {
            "jsonrpc": "2.0", "id": req_id, "result": {
                "protocolVersion": "2024-11-05",
                "capabilities": {"tools": {}},
                "serverInfo": {"name": "emuera", "version": "1.0"}
            }
        }

    def _tools_list(self, req_id):
        return {
            "jsonrpc": "2.0", "id": req_id, "result": {
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
                        "inputSchema": {"type": "object", "properties": {}}
                    }
                ]
            }
        }

    def _tools_call(self, req_id, params):
        tool_name = params.get("name", "")
        args = params.get("arguments", {})
        client_id = params.get("_meta", {}).get("clientId", "default")

        if tool_name == "emuera_get_config":
            return self._tool_get_config(req_id)

        if tool_name == "emuera_set_config":
            return self._tool_set_config(req_id, args)

        if tool_name == "emuera_kill":
            removed = self.sm.remove(client_id)
            return self._success(req_id, json.dumps({
                "killed": removed,
                "message": "Session terminated" if removed else "No session was running"
            })), False

        if tool_name == "emuera_get_state":
            session = self.sm.create(client_id)
            turn = session.get_state()
            if turn is None:
                return self._error(req_id, -32000, "Session not ready"), False
            return self._success(req_id, json.dumps(turn)), False

        if tool_name == "emuera_step":
            session = self.sm.create(client_id)
            turn = session.step(args.get("value", ""))
            if turn is None:
                return self._error(req_id, -32000, "Session lost or game ended"), False
            return self._success(req_id, json.dumps(turn)), False

        return self._error(req_id, -32601, f"Unknown tool: {tool_name}"), False

    def _tool_get_config(self, req_id):
        config = load_config()
        if config is None:
            config = {"binaryPath": None, "gameDir": None, "configured": False}
        else:
            config["configured"] = True
            if "dllPath" in config and "binaryPath" not in config:
                config["binaryPath"] = config.pop("dllPath")
            elif "dllPath" in config:
                del config["dllPath"]
        return self._success(req_id, json.dumps(config)), False

    def _tool_set_config(self, req_id, args):
        new_binary = args.get("binaryPath") or args.get("dllPath")
        new_game_dir = args.get("gameDir")

        if new_binary is not None:
            resolved = resolve_path(new_binary)
            if not os.path.isfile(resolved):
                return self._error(req_id, -32000, f"Binary file not found: {resolved}"), False
        if new_game_dir is not None:
            resolved = resolve_path(new_game_dir)
            if not os.path.isdir(resolved):
                return self._error(req_id, -32000, f"Game directory not found: {resolved}"), False

        config = load_config() or {}
        if new_binary is not None:
            config["binaryPath"] = new_binary
        elif "dllPath" in config and "binaryPath" not in config:
            config["binaryPath"] = config.pop("dllPath")
        if "binaryPath" in config:
            config.pop("dllPath", None)
        if new_game_dir is not None:
            config["gameDir"] = new_game_dir
        save_config(config)

        return self._success(req_id, json.dumps({
            "saved": True,
            "binaryPath": config.get("binaryPath"),
            "gameDir": config.get("gameDir")
        })), False

    def _success(self, req_id, text: str):
        return {"jsonrpc": "2.0", "id": req_id, "result": {
            "content": [{"type": "text", "text": text}]
        }}

    def _error(self, req_id, code: int, message: str):
        return {"jsonrpc": "2.0", "id": req_id, "error": {
            "code": code, "message": message
        }}

    def run(self):
        for line in sys.stdin:
            line = line.strip()
            if not line:
                continue
            try:
                req = json.loads(line)
            except json.JSONDecodeError:
                continue
            resp, is_notification = self.handle(req)
            if resp is not None:
                sys.stdout.write(json.dumps(resp) + "\n")
                sys.stdout.flush()
