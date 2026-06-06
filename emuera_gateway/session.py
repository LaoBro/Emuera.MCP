"""Python-side session management mapping client identities to Emuera sessions."""
import threading
import time
from typing import Dict, Optional, Any
from .emuera_client import EmueraClient


class GameSession:
    """Python-side wrapper for a single Emuera game session."""

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
            if not self.client.send_input(self.session_id, value):
                return None
            turn = self.client.get_turn(self.session_id)
            self.touch()
            return turn

    def get_state(self) -> Optional[Dict[str, Any]]:
        """Fetch current turn (non-blocking, uses cached or new long poll)."""
        with self._lock:
            self.touch()
            return self.client.get_turn(self.session_id, timeout=5.0)

    def destroy(self) -> bool:
        return self.client.delete_session(self.session_id)


class SessionManager:
    """Manages multiple game sessions, mapping client identities to sessions."""

    def __init__(self, client: EmueraClient, idle_timeout: float = 1800.0):
        self.client = client
        self.idle_timeout = idle_timeout
        self._sessions: Dict[str, GameSession] = {}
        self._lock = threading.Lock()

    def create(self, client_id: str) -> GameSession:
        """Create a new session for a client. Reuses if already exists."""
        with self._lock:
            if client_id in self._sessions:
                return self._sessions[client_id]
            resp = self.client.create_session()
            session_id = resp["sessionId"]
            gs = GameSession(session_id, self.client)
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
        """Remove sessions idle longer than timeout."""
        cutoff = time.time() - self.idle_timeout
        with self._lock:
            to_remove = [
                cid for cid, gs in self._sessions.items()
                if gs.last_activity < cutoff
            ]
            for cid in to_remove:
                gs = self._sessions.pop(cid)
                gs.destroy()
