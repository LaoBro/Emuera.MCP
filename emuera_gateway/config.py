"""Configuration loading and saving for emuera_agent."""
import json
import os

PROJECT_DIR = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
CONFIG_FILE = os.path.join(PROJECT_DIR, ".emuera-agent.json")
SERVER_FILE = os.path.join(PROJECT_DIR, ".emuera-server.json")


def load_config():
    """Load config from .emuera-agent.json. Returns dict or None if missing/unreadable."""
    if not os.path.isfile(CONFIG_FILE):
        return None
    try:
        with open(CONFIG_FILE, "r", encoding="utf-8") as f:
            return json.load(f)
    except (json.JSONDecodeError, OSError):
        return None


def save_config(config):
    """Save config dict to .emuera-agent.json."""
    with open(CONFIG_FILE, "w", encoding="utf-8") as f:
        json.dump(config, f, indent=2, ensure_ascii=False)


def resolve_path(path):
    """Resolve a relative path against PROJECT_DIR; absolute paths pass through."""
    if os.path.isabs(path):
        return path
    return os.path.join(PROJECT_DIR, path)


def load_server_record():
    """Load .emuera-server.json (port/pid/token/gameDir). None if missing/unreadable."""
    if not os.path.isfile(SERVER_FILE):
        return None
    try:
        with open(SERVER_FILE, "r", encoding="utf-8") as f:
            return json.load(f)
    except (json.JSONDecodeError, OSError):
        return None


def save_server_record(record):
    """Write .emuera-server.json."""
    with open(SERVER_FILE, "w", encoding="utf-8") as f:
        json.dump(record, f, indent=2, ensure_ascii=False)


def delete_server_record():
    """Remove .emuera-server.json if it exists."""
    try:
        os.remove(SERVER_FILE)
    except FileNotFoundError:
        pass
