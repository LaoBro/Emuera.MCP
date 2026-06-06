"""Entry point for emuera_gateway: supports Embedded and Standalone modes."""
import argparse
import subprocess
import sys
import time
import os

from .config import load_config
from .emuera_client import EmueraClient
from .session import SessionManager
from .mcp_server import McpServer


def _start_emuera_server(binary_path: str, game_dir: str, port: int) -> subprocess.Popen:
    """Launch Emuera.exe --server in background."""
    cmd = [binary_path, "--ExeDir", game_dir, "--server", "--port", str(port)]
    return subprocess.Popen(
        cmd,
        stdout=sys.stderr,
        stderr=sys.stderr,
        text=True,
        encoding="utf-8"
    )


def main():
    parser = argparse.ArgumentParser(description="Emuera Python Gateway")
    parser.add_argument("--embedded", action="store_true",
                        help="Launch and manage Emuera process automatically (default)")
    parser.add_argument("--standalone", action="store_true",
                        help="Connect to an existing Emuera HTTP server")
    parser.add_argument("--server-url", default="http://localhost:8080",
                        help="Emuera HTTP server URL (standalone mode)")
    parser.add_argument("--port", type=int, default=8080,
                        help="Port for embedded Emuera server")
    parser.add_argument("--emuera-path", default=None,
                        help="Path to Emuera.exe (embedded mode)")
    parser.add_argument("--game-dir", default=None,
                        help="Game data directory (embedded mode)")
    args = parser.parse_args()

    # Default embedded mode
    if not args.standalone:
        config = load_config()
        binary_path = args.emuera_path or config.get("binaryPath")
        game_dir = args.game_dir or config.get("gameDir")
        if not binary_path or not game_dir:
            print("Error: --emuera-path and --game-dir required for embedded mode",
                  file=sys.stderr)
            sys.exit(1)

        proc = _start_emuera_server(binary_path, game_dir, args.port)
        time.sleep(2)  # Wait for server startup
        if proc.poll() is not None:
            print("Error: Emuera server exited early", file=sys.stderr)
            sys.exit(1)

        try:
            client = EmueraClient(f"http://localhost:{args.port}")
            sm = SessionManager(client)
            server = McpServer(sm)
            server.run()
        finally:
            proc.terminate()
            proc.wait(timeout=5)
    else:
        client = EmueraClient(args.server_url)
        sm = SessionManager(client)
        server = McpServer(sm)
        server.run()


if __name__ == "__main__":
    main()
