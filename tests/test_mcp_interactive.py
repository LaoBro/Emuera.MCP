"""Interactive MCP mode test - step by step with user confirmation.

Tests via mcp_relay.py which talks JSONL to Emuera internally.
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
    args = {}
    if value is not None:
        args["value"] = value
    resp = send_req("tools/call", {"name": "emuera_step", "arguments": args})
    return json.loads(resp["result"]["content"][0]["text"])

try:
    # Step 1: Initialize handshake
    resp = send_req("initialize", {"protocolVersion": "2024-11-05", "capabilities": {}, "clientInfo": {"name": "test", "version": "1.0"}})
    send_notif("notifications/initialized")

    # Step 2: emuera_step("0") - starts game, selects start on title
    print("正在启动游戏...")
    turn1 = step("0")
    print(f"\n=== 游戏输出 ===\n{turn1['text']}\n")

    input(">>> 请检查 Emuera 窗口是否已打开。按 Enter 继续发送 '0'...")

    # Step 3: Send "0" to select Hello
    print("\n发送输入: 0")
    turn2 = step("0")
    print(f"\n=== 游戏输出 ===\n{turn2['text']}\n")

    input(">>> 请检查窗口是否已自动关闭。按 Enter 结束...")

    print("测试完成！")

except Exception as e:
    print(f"错误: {e}")
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
        try:
            os.remove(config_file)
        except OSError:
            pass
