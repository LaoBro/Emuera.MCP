"""Python-side wrapper over the single Headless session."""
import threading
import time
from typing import Any, Dict, Optional

from .emuera_client import EmueraClient, EmueraHttpError


class GameSession:
    """Thin wrapper around the one Headless session."""

    def __init__(self, client: EmueraClient):
        self.session_id = "current"
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
    """Holds the single GameSession wrapper for one Headless client."""

    def __init__(self, client: EmueraClient, idle_timeout: float = 1800.0):
        self.client = client
        self.idle_timeout = idle_timeout
        self._session: Optional[GameSession] = None
        self._lock = threading.Lock()

    def create(self, _client_id: str = "") -> GameSession:
        """Return the singleton wrapper, creating it if needed."""
        with self._lock:
            if self._session is None:
                self._session = GameSession(self.client)
            return self._session

    def get(self, _client_id: str = "") -> Optional[GameSession]:
        with self._lock:
            return self._session

    def remove(self, _client_id: str = "") -> bool:
        with self._lock:
            gs = self._session
            self._session = None
        if gs:
            gs.destroy()
            return True
        return False

    def cleanup_idle(self):
        """Destroy the wrapper if it has been idle longer than timeout."""
        cutoff = time.time() - self.idle_timeout
        with self._lock:
            gs = self._session
            if gs is None or gs.last_activity >= cutoff:
                return
            self._session = None
        gs.destroy()
