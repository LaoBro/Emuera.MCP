"""Configuration loading and saving for emuera_gateway."""
import json
import os

PROJECT_DIR = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
CONFIG_FILE = os.path.join(PROJECT_DIR, ".emuera-mcp.json")


def load_config():
    """Load config from .emuera-mcp.json. Returns dict or None if missing/unreadable."""
    if not os.path.isfile(CONFIG_FILE):
        return None
    try:
        with open(CONFIG_FILE, "r", encoding="utf-8") as f:
            return json.load(f)
    except (json.JSONDecodeError, OSError):
        return None


def save_config(config):
    """Save config dict to .emuera-mcp.json."""
    with open(CONFIG_FILE, "w", encoding="utf-8") as f:
        json.dump(config, f, indent=2, ensure_ascii=False)


def resolve_path(path):
    """Resolve a relative path against PROJECT_DIR; absolute paths pass through."""
    if os.path.isabs(path):
        return path
    return os.path.join(PROJECT_DIR, path)
