"""Plugin system for emuera_gateway."""
from abc import ABC, abstractmethod
from typing import Dict, Any, List
import importlib
import pkgutil


class Plugin(ABC):
    """Base class for emuera_gateway plugins."""

    name: str = ""

    @abstractmethod
    def on_turn(self, turn: Dict[str, Any]) -> Dict[str, Any]:
        """Intercept and optionally modify a turn before sending to MCP client."""
        return turn

    @abstractmethod
    def on_input(self, value: str) -> str:
        """Intercept and optionally modify input before sending to Emuera."""
        return value


class PluginManager:
    """Discovers and runs plugins from emuera_gateway.plugins package."""

    def __init__(self):
        self._plugins: List[Plugin] = []

    def load_all(self):
        """Auto-discover plugins from emuera_gateway.plugins."""
        try:
            from . import plugins
        except ImportError:
            return
        for _, name, _ in pkgutil.iter_modules(plugins.__path__):
            try:
                mod = importlib.import_module(f"{plugins.__name__}.{name}")
                for attr in dir(mod):
                    cls = getattr(mod, attr)
                    if isinstance(cls, type) and issubclass(cls, Plugin) and cls is not Plugin:
                        inst = cls()
                        self._plugins.append(inst)
            except Exception:
                continue

    def process_turn(self, turn: Dict[str, Any]) -> Dict[str, Any]:
        for p in self._plugins:
            turn = p.on_turn(turn)
        return turn

    def process_input(self, value: str) -> str:
        for p in self._plugins:
            value = p.on_input(value)
        return value
