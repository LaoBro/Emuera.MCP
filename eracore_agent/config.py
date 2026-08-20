"""Configuration loading and saving for eracore_agent."""
import json
import os

PROJECT_DIR = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
CONFIG_FILE = os.path.join(PROJECT_DIR, ".eracore-agent.json")
SERVER_FILE = os.path.join(PROJECT_DIR, ".eracore-server.json")
# issue 05：MAUI 托管 server 的发现记录（Windows %LOCALAPPDATA%\EmueraCore\eracore-maui-server.json）。
# MAUI 应用跑游戏时写此记录（端口 + token + gameDir），eracore_agent start 据此侦测并复用，
# 避免 agent 另起一局 server（单实例共享）。
MAUI_SERVER_FILE = os.path.join(
    os.environ.get("LOCALAPPDATA", ""),
    "EmueraCore",
    "eracore-maui-server.json",
)


def load_config():
    """Load config from .eracore-agent.json. Returns dict or None if missing/unreadable."""
    if not os.path.isfile(CONFIG_FILE):
        return None
    try:
        with open(CONFIG_FILE, "r", encoding="utf-8") as f:
            return json.load(f)
    except (json.JSONDecodeError, OSError):
        return None


def save_config(config):
    """Save config dict to .eracore-agent.json."""
    with open(CONFIG_FILE, "w", encoding="utf-8") as f:
        json.dump(config, f, indent=2, ensure_ascii=False)


def resolve_path(path):
    """Resolve a relative path against PROJECT_DIR; absolute paths pass through."""
    if os.path.isabs(path):
        return path
    return os.path.join(PROJECT_DIR, path)


def load_server_record():
    """Load .eracore-server.json (port/pid/token/gameDir). None if missing/unreadable."""
    if not os.path.isfile(SERVER_FILE):
        return None
    try:
        with open(SERVER_FILE, "r", encoding="utf-8") as f:
            return json.load(f)
    except (json.JSONDecodeError, OSError):
        return None


def save_server_record(record):
    """Write .eracore-server.json."""
    with open(SERVER_FILE, "w", encoding="utf-8") as f:
        json.dump(record, f, indent=2, ensure_ascii=False)


def delete_server_record():
    """Remove .eracore-server.json if it exists."""
    try:
        os.remove(SERVER_FILE)
    except FileNotFoundError:
        pass


def load_maui_server_record():
    """Load MAUI-hosted server discovery record (issue 05). None if missing/unreadable/not Windows."""
    if not MAUI_SERVER_FILE or not os.path.isfile(MAUI_SERVER_FILE):
        return None
    try:
        with open(MAUI_SERVER_FILE, "r", encoding="utf-8") as f:
            return json.load(f)
    except (json.JSONDecodeError, OSError):
        return None
