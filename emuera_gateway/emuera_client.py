"""HTTP client for the current single-session Emuera Headless server."""
import json
import urllib.error
import urllib.request
from typing import Any, Dict, Optional
from urllib.parse import urlencode


class EmueraHttpError(Exception):
    """Raised when the server returns an HTTP error or cannot be reached."""

    def __init__(self, status: int, payload: Any, body: str = ""):
        self.status = status
        self.payload = payload if isinstance(payload, dict) else {"error": payload or body}
        self.body = body
        super().__init__(f"HTTP {status} {self.code}")

    @property
    def code(self) -> str:
        error = self.payload.get("error")
        if isinstance(error, dict):
            return str(error.get("code") or "ERROR")
        if error:
            return str(error)
        return str(self.payload.get("code") or self.status)


def _parse_json(raw: str) -> Any:
    if not raw:
        return None
    try:
        return json.loads(raw)
    except json.JSONDecodeError:
        return raw


class EmueraClient:
    """HTTP client for the single-session Headless API."""

    def __init__(self, base_url: str = "http://localhost:8080", token: Optional[str] = None):
        self.base_url = base_url.rstrip("/")
        self.token = token

    def load_game(self, game_dir: str) -> Dict[str, Any]:
        """POST /load-game {gameDir}."""
        status, payload, _ = self.request("POST", "/load-game", {"gameDir": game_dir})
        if status != 200:
            raise EmueraHttpError(status, payload)
        return payload

    def get_turn(self, timeout: float = 35.0) -> Optional[Dict[str, Any]]:
        """GET /turn (long poll). 200 → turn JSON; 204 → None; 404/409 → EmueraHttpError."""
        status, payload, _ = self.request("GET", "/turn", timeout=timeout)
        if status == 204:
            return None
        if status != 200:
            raise EmueraHttpError(status, payload)
        return payload

    def post_input(self, value: str = "") -> Dict[str, Any]:
        """POST /input {value}."""
        status, payload, _ = self.request("POST", "/input", {"value": value})
        if status != 200:
            raise EmueraHttpError(status, payload)
        return payload

    def get_state(self, timeout: float = 10.0) -> Dict[str, Any]:
        """GET /state."""
        status, payload, _ = self.request("GET", "/state", timeout=timeout)
        if status != 200:
            raise EmueraHttpError(status, payload)
        return payload

    def delete_session(self) -> Dict[str, Any]:
        """DELETE /session. 404 means nothing to remove."""
        try:
            status, payload, _ = self.request("DELETE", "/session")
        except EmueraHttpError as exc:
            if exc.status == 404:
                return exc.payload if isinstance(exc.payload, dict) else {"removed": False}
            raise
        if status != 200:
            raise EmueraHttpError(status, payload)
        return payload if isinstance(payload, dict) else {"removed": True}

    def acquire_control(self) -> Dict[str, Any]:
        """POST /control/acquire."""
        body = {"token": self.token} if self.token else None
        status, payload, _ = self.request("POST", "/control/acquire", body)
        if status != 200:
            raise EmueraHttpError(status, payload)
        return payload

    def release_control(self) -> Dict[str, Any]:
        """POST /control/release."""
        body = {"token": self.token} if self.token else None
        status, payload, _ = self.request("POST", "/control/release", body)
        if status != 200:
            raise EmueraHttpError(status, payload)
        return payload

    def get_control(self, timeout: float = 10.0) -> Dict[str, Any]:
        """GET /control."""
        status, payload, _ = self.request("GET", "/control", timeout=timeout)
        if status != 200:
            raise EmueraHttpError(status, payload)
        return payload

    def wait_control(self, timeout: float = 35.0) -> Optional[Dict[str, Any]]:
        """GET /control/wait. 200 → event; 204 → None."""
        status, payload, _ = self.request("GET", "/control/wait", timeout=timeout)
        if status == 204:
            return None
        if status != 200:
            raise EmueraHttpError(status, payload)
        return payload

    def get_snapshot(self, timeout: float = 15.0) -> Dict[str, Any]:
        """GET /snapshot."""
        status, payload, _ = self.request("GET", "/snapshot", timeout=timeout)
        if status != 200:
            raise EmueraHttpError(status, payload)
        return payload

    def request(self, method: str, path: str, body: Optional[Dict[str, Any]] = None, timeout: float = 35.0):
        headers = {"Accept": "application/json"}
        data = None
        if body is not None:
            payload = dict(body)
            if self.token and "token" not in payload:
                payload["token"] = self.token
            data = json.dumps(payload).encode("utf-8")
            headers["Content-Type"] = "application/json"
        if self.token:
            headers["X-Control-Token"] = self.token

        url = f"{self.base_url}{path}"
        if method == "GET" and self.token and "token=" not in path:
            sep = "&" if "?" in path else "?"
            url = f"{url}{sep}{urlencode({'token': self.token})}"

        req = urllib.request.Request(url, data=data, method=method, headers=headers)
        try:
            with urllib.request.urlopen(req, timeout=timeout) as resp:
                raw = resp.read().decode("utf-8")
                return resp.status, _parse_json(raw), raw
        except urllib.error.HTTPError as exc:
            raw = exc.read().decode("utf-8", errors="replace")
            raise EmueraHttpError(exc.code, _parse_json(raw), raw) from exc
        except urllib.error.URLError as exc:
            raise EmueraHttpError(0, {"error": "CONNECTION_FAILED", "message": str(exc.reason)}) from exc
