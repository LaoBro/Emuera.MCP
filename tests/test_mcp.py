"""Test MCP mode: JSON-RPC handshake via relay + tools/list + emuera_step full round-trip.

This tests the mcp_relay.py which talks JSONL to Emuera internally.
Requires .emuera-mcp.json to be configured with valid dllPath and gameDir.
"""
import subprocess, json, os, sys

project_dir = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
relay_script = os.path.join(project_dir, "mcp_relay.py")

# Write a temporary .emuera-mcp.json for the test
config_file = os.path.join(project_dir, ".emuera-mcp.json")
emuera_dll = os.path.join(project_dir, "Emuera", "artifacts", "bin", "Emuera", "debug-naudio", "Emuera.dll")
test_game = os.path.join(project_dir, "test_game")
test_config = {"dllPath": emuera_dll, "gameDir": test_game}

# Save existing config if any
existing_config = None
if os.path.isfile(config_file):
    with open(config_file, "r", encoding="utf-8") as f:
        existing_config = f.read()

with open(config_file, "w", encoding="utf-8") as f:
    json.dump(test_config, f, indent=2)

proc = subprocess.Popen(
    ["python", relay_script],
    stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
    text=True, encoding="utf-8"
)

passed = 0
failed = 0

def check(cond, msg):
    global passed, failed
    if cond:
        passed += 1
        print(f"  PASS: {msg}")
    else:
        failed += 1
        print(f"  FAIL: {msg}")

def send_req(method, params=None, id_val=1):
    req = {"jsonrpc": "2.0", "id": id_val, "method": method, "params": params or {}}
    proc.stdin.write(json.dumps(req) + "\n")
    proc.stdin.flush()
    return json.loads(proc.stdout.readline())

def send_notif(method, params=None):
    req = {"jsonrpc": "2.0", "method": method, "params": params or {}}
    proc.stdin.write(json.dumps(req) + "\n")
    proc.stdin.flush()

def step(value=None):
    """Call emuera_step and parse the inner JSON from the content text."""
    args = {}
    if value is not None:
        args["value"] = value
    resp = send_req("tools/call", {"name": "emuera_step", "arguments": args})
    return json.loads(resp["result"]["content"][0]["text"])

try:
    # 1. Initialize
    resp = send_req("initialize", {"protocolVersion": "2024-11-05", "capabilities": {}, "clientInfo": {"name": "test", "version": "1.0"}})
    check("result" in resp, "Initialize response has result")
    check(resp["result"]["serverInfo"]["name"] == "emuera", "Server name is emuera")

    # 2. Initialized notification
    send_notif("notifications/initialized")

    # 3. tools/list
    resp = send_req("tools/list")
    tools = resp["result"]["tools"]
    tool_names = [t["name"] for t in tools]
    check("emuera_step" in tool_names, "emuera_step in tool list")
    check("emuera_get_state" in tool_names, "emuera_get_state in tool list")

    # 4. emuera_get_state (starts game, returns initial state)
    resp = send_req("tools/call", {"name": "emuera_get_state", "arguments": {}})
    state_info = json.loads(resp["result"]["content"][0]["text"])
    check("state" in state_info, "emuera_get_state returns state field")

    # 5. emuera_step("0") - select start game on title
    turn1 = step("0")
    print(f"Turn 1: state={turn1['state']} text_len={len(turn1['text'])}")
    check(turn1["state"] == "WaitInput", "Turn 1 is WaitInput")
    check("Agent Test Start" in turn1["text"], "Turn 1 contains test ERB content")

    # 6. emuera_step("0") - select [0] Hello
    turn2 = step("0")
    print(f"Turn 2: state={turn2['state']} text_len={len(turn2['text'])}")
    check("You entered: 0" in turn2["text"], "Turn 2 shows input result")
    check("Agent Test End" in turn2["text"], "Turn 2 shows end message")
    check(turn2["state"] in ("Quit", "WaitInput"), f"Turn 2 state valid: {turn2['state']}")

    print(f"\n=== MCP (via relay): {passed} passed, {failed} failed ===")

except Exception as e:
    print(f"TEST ERROR: {e}")
    import traceback
    traceback.print_exc()

finally:
    proc.kill()
    proc.wait(timeout=5)
    # Restore existing config
    if existing_config is not None:
        with open(config_file, "w", encoding="utf-8") as f:
            f.write(existing_config)
    else:
        os.remove(config_file)
    sys.exit(0 if failed == 0 else 1)
