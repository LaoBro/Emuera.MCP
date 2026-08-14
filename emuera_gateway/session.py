"""Python-side session wrapper over the single-session Headless API."""
import threading
import time
from typing import Any, Dict, Optional

from .emuera_client import EmueraClient, EmueraHttpError


class GameSession:
    """Python-side wrapper for the single Emuera game session."""

    def __init__(self, session_id: str, client: EmueraClient):
        self.session_id = session_id
        self.client = client
        self.created_at = time.time()
        self.last_activity = time.time()
        self._lock = threading.Lock()

    def touch(self):
        self.last_activity = time.time()

    def step(self, value: str = "") -> Optional[Dict[str, Any]]:
        """Send input and fetch next turn via long polling."""
        with self._lock:
            self.touch()
            try:
                self.client.post_input(value)
                turn = self.client.get_turn()
            except EmueraHttpError:
                return None
            self.touch()
            return turn

    def get_state(self) -> Optional[Dict[str, Any]]:
        """Fetch current session state."""
        with self._lock:
            self.touch()
            try:
                return self.client.get_state()
            except EmueraHttpError:
                return None

    def destroy(self) -> bool:
        try:
            result = self.client.delete_session()
        except EmueraHttpError:
            return False
        return bool(result.get("removed", True))


class SessionManager:
    """Maps client identities onto the single Headless session."""

    def __init__(self, client: EmueraClient, idle_timeout: float = 1800.0):
        self.client = client
        self.idle_timeout = idle_timeout
        self._sessions: Dict[str, GameSession] = {}
        self._lock = threading.Lock()

    def create(self, client_id: str) -> GameSession:
        """Return the wrapper for a client. Reuses if already exists."""
        with self._lock:
            if client_id in self._sessions:
                return self._sessions[client_id]
            gs = GameSession("current", self.client)
            self._sessions[client_id] = gs
            return gs

    def get(self, client_id: str) -> Optional[GameSession]:
        with self._lock:
            return self._sessions.get(client_id)

    def remove(self, client_id: str) -> bool:
        with self._lock:
            gs = self._sessions.pop(client_id, None)
            if gs:
                gs.destroy()
                return True
            return False

    def cleanup_idle(self):
        """Remove wrappers idle longer than timeout."""
        cutoff = time.time() - self.idle_timeout
        with self._lock:
            to_remove = [
                cid for cid, gs in self._sessions.items()
                if gs.last_activity < cutoff
            ]
            for cid in to_remove:
                gs = self._sessions.pop(cid)
                gs.destroy()
