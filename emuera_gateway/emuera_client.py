"""HTTP client for Emuera C# server."""
import json
import urllib.request
import urllib.error
from typing import Optional, Dict, Any


class EmueraClient:
    """HTTP client for Emuera C# server."""

    def __init__(self, base_url: str = "http://localhost:8080"):
        self.base_url = base_url.rstrip("/")

    def create_session(self) -> Dict[str, Any]:
        """POST /sessions -> {"sessionId", "createdAt"}"""
        req = urllib.request.Request(
            f"{self.base_url}/sessions",
            method="POST",
            headers={"Accept": "application/json"}
        )
        with urllib.request.urlopen(req, timeout=10) as resp:
            return json.loads(resp.read().decode("utf-8"))

    def get_session(self, session_id: str) -> Optional[Dict[str, Any]]:
        """GET /sessions/{id} -> session state or None if 404."""
        try:
            with urllib.request.urlopen(
                f"{self.base_url}/sessions/{session_id}", timeout=10
            ) as resp:
                return json.loads(resp.read().decode("utf-8"))
        except urllib.error.HTTPError as e:
            if e.code == 404:
                return None
            raise

    def send_input(self, session_id: str, value: str = "") -> bool:
        """POST /sessions/{id}/input -> True if accepted."""
        body = json.dumps({"type": "input", "value": value}).encode("utf-8")
        req = urllib.request.Request(
            f"{self.base_url}/sessions/{session_id}/input",
            data=body,
            method="POST",
            headers={"Content-Type": "application/json", "Accept": "application/json"}
        )
        try:
            with urllib.request.urlopen(req, timeout=10) as resp:
                return resp.status == 200
        except urllib.error.HTTPError:
            return False

    def get_turn(self, session_id: str, timeout: float = 30.0) -> Optional[Dict[str, Any]]:
        """GET /sessions/{id}/turn (long polling). Returns turn JSON or None."""
        try:
            with urllib.request.urlopen(
                f"{self.base_url}/sessions/{session_id}/turn",
                timeout=timeout
            ) as resp:
                if resp.status == 204:
                    return None
                return json.loads(resp.read().decode("utf-8"))
        except urllib.error.HTTPError as e:
            if e.code == 404:
                return None
            raise

    def delete_session(self, session_id: str) -> bool:
        """DELETE /sessions/{id} -> True if removed."""
        req = urllib.request.Request(
            f"{self.base_url}/sessions/{session_id}",
            method="DELETE"
        )
        try:
            with urllib.request.urlopen(req, timeout=10) as resp:
                return resp.status == 200
        except urllib.error.HTTPError:
            return False
