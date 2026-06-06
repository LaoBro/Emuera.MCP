"""Backward-compatible entry point for Emuera MCP gateway.

Delegates to emuera_gateway package. If --embedded or --standalone is passed,
uses the new gateway. Otherwise, falls back to legacy subprocess mode.
"""
import sys


def main():
    args = sys.argv[1:]
    if any(a in args for a in ("--embedded", "--standalone", "--server-url")):
        from emuera_gateway.__main__ import main as gateway_main
        gateway_main()
    else:
        # Legacy mode: direct subprocess (preserves original behavior)
        from emuera_gateway.legacy import main as legacy_main
        legacy_main()


if __name__ == "__main__":
    try:
        main()
    finally:
        # Ensure cleanup in legacy mode
        try:
            from emuera_gateway.legacy import _kill_game
            _kill_game()
        except Exception:
            pass
