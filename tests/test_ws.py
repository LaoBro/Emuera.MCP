"""Test: WebSocket transport (GET /ws) — bare JSON frames, fanout, HTTP/WS coexistence.

Verifies the PRD "WebSocket 传输实现" wire contract end-to-end against a live
Emuera.Headless server:

1. Gate: connecting /ws with no active session is rejected/closed.
2. Initial frame over WS is TurnRecord v3 JSON (protocolVersion == 3, ops[] non-empty,
   no legacy text/buttons) — identical to GET /turn's response body.
3. Frame format consistency: WS first frame == HTTP GET /turn first frame.
4. Input round-trip: send {"type":"input","value":"..."} -> receive next turn (no protocolVersion).
5. Fan-out: 2 WS clients + one input -> both receive the same next turn (OutputHub broadcast).
6. HTTP/WS coexistence: an HTTP long-poll client and a WS client consume the same session
   and receive consistent turn streams.
7. Late join: a WS connecting after the first turn receives only later turns, not history.

Only the public contract is exercised (ws://localhost:<port>/ws, POST /session,
GET /turn, POST /input). No internal types or private fields are touched.

Usage:
    python test_ws.py --binary <path> --game-dir test_game
"""
import argparse
import json
import os
import sys
from pathlib import Path

_project_dir = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, _project_dir)
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from emuera_server import TEST_GAME_DIR, ops_text, start_server

try:
    import asyncio
    import websockets
    from websockets.asyncio.client import connect as _ws_connect_impl
    _WS_AVAILABLE = True
except Exception:  # pragma: no cover - exercised only without the dependency
    _WS_AVAILABLE = False
    _ws_connect_impl = None


VALID_OP_TYPES = {"print", "newline", "clearline", "clear", "set_bg"}
VALID_ALIGN = {"left", "center", "right"}


def check(condition, message, passed, failed):
    if condition:
        passed[0] += 1
        print(f"  PASS: {message}")
    else:
        failed[0] += 1
        print(f"  FAIL: {message}")


def check_ops(turn, passed, failed, turn_name):
    ops = turn.get("ops", [])
    check(isinstance(ops, list), f"{turn_name} ops is a list", passed, failed)
    for i, op in enumerate(ops):
        check("type" in op, f"{turn_name} op[{i}] has type", passed, failed)
        check(op["type"] in VALID_OP_TYPES, f"{turn_name} op[{i}] type valid: {op['type']}", passed, failed)
        if op["type"] == "print":
            check("segments" in op, f"{turn_name} print op[{i}] has segments", passed, failed)
            check(isinstance(op["segments"], list) and len(op["segments"]) > 0,
                  f"{turn_name} print op[{i}] segments non-empty", passed, failed)
        elif op["type"] == "newline" and "align" in op:
            check(op["align"] in VALID_ALIGN, f"{turn_name} newline align valid: {op['align']}", passed, failed)


def ws_uri(base_url):
    return base_url.replace("http://", "ws://", 1) + "/ws"


async def ws_connect(base_url, timeout=10):
    return await asyncio.wait_for(_ws_connect_impl(ws_uri(base_url)), timeout=timeout)


async def recv_json(ws, timeout=15):
    raw = await asyncio.wait_for(ws.recv(), timeout=timeout)
    return json.loads(raw)


# Grace period between sessions.
# 历史：高频 create→delete→create 曾触发单会话拆除竞态（GlobalStatic.Console 未释放时被
# 下一会话复用，首帧缺 protocolVersion）。该竞态已由 Session.Dispose() 同步 join 游戏循环并
# 显式 GlobalStatic.Reset() 修复（TODO#6），Dispose 返回时全局静态已清空，无需再靠延迟掩盖。
# 保留 0.0 作为默认值——修复后间隔已无必要；保留变量以便个别慢机器临时调大做对照。
TEARDOWN_GRACE = 0.0


async def reset_session(server):
    """Ensure no active session lingers and the previous session has fully torn down
    (including GlobalStatic.Reset) before the next create_session."""
    try:
        server.delete_session()
    except Exception:
        pass
    await asyncio.sleep(TEARDOWN_GRACE)


async def test_gate_no_session(server, passed, failed):
    print("\n[gate] connect /ws without an active session")
    uri = ws_uri(server.base_url)
    closed = False
    try:
        ws = await asyncio.wait_for(_ws_connect_impl(uri), timeout=10)
        try:
            await asyncio.wait_for(ws.recv(), timeout=5)
        except websockets.exceptions.ConnectionClosed as e:
            closed = (int(e.code) == 4004)
        try:
            await ws.close()
        except Exception:
            pass
    except websockets.exceptions.ConnectionClosed as e:
        closed = (int(e.code) == 4004)
    except Exception:
        # Handshake rejected (HTTP 400) or other connection refusal — still "closed".
        closed = True
    check(closed, "connect /ws without session is rejected/closed (no active session)", passed, failed)


async def test_initial_frame(server, passed, failed):
    print("\n[initial] WS first frame is TurnRecord v3 JSON")
    s, _ = server.create_session()
    check(s == 201, f"POST /session returns 201, got {s}", passed, failed)
    ws = await ws_connect(server.base_url)
    try:
        turn = await recv_json(ws)
        check(turn.get("protocolVersion") == 3, "WS initial frame protocolVersion == 3", passed, failed)
        check(turn.get("state") == "WaitInput", f"WS initial state WaitInput, got {turn.get('state')}", passed, failed)
        check("text" not in turn, "WS initial frame has no text field (v2)", passed, failed)
        check("buttons" not in turn, "WS initial frame has no buttons field (v2)", passed, failed)
        check(isinstance(turn.get("ops"), list) and len(turn.get("ops", [])) > 0,
              "WS initial frame ops[] non-empty", passed, failed)
        check_ops(turn, passed, failed, "WS initial")
    finally:
        await ws.close()
        server.delete_session()


async def test_frame_consistency(server, passed, failed):
    print("\n[consistency] WS first frame == HTTP GET /turn first frame")
    s, _ = server.create_session()
    check(s == 201, f"POST /session returns 201, got {s}", passed, failed)
    ws = await ws_connect(server.base_url)
    try:
        _, http_body = server.get_turn(timeout=20)
        ws_turn = await recv_json(ws)
        http_turn = json.loads(http_body)
        check(ws_turn.get("protocolVersion") == 3, "WS frame protocolVersion == 3", passed, failed)
        check(http_turn.get("protocolVersion") == 3, "HTTP frame protocolVersion == 3", passed, failed)
        check(ws_turn == http_turn, "WS initial frame == HTTP GET /turn initial frame (byte-identical payload)", passed, failed)
    finally:
        await ws.close()
        server.delete_session()


async def test_input_roundtrip(server, passed, failed):
    print("\n[roundtrip] WS input frame -> next turn (no protocolVersion)")
    s, _ = server.create_session()
    check(s == 201, f"POST /session returns 201, got {s}", passed, failed)
    ws = await ws_connect(server.base_url)
    try:
        init = await recv_json(ws)
        check(init.get("protocolVersion") == 3, "initial frame protocolVersion == 3", passed, failed)
        await ws.send(json.dumps({"type": "input", "value": "0"}))
        nxt = await recv_json(ws)
        check("protocolVersion" not in nxt, "step turn has no protocolVersion", passed, failed)
        check(nxt.get("state") == "WaitInput", f"step turn state WaitInput, got {nxt.get('state')}", passed, failed)
        check_ops(nxt, passed, failed, "WS step")
        check("You entered: 0" in ops_text(nxt), "step turn shows input result", passed, failed)
    finally:
        await ws.close()
        server.delete_session()


async def test_fanout(server, passed, failed):
    print("\n[fanout] 2 WS clients both receive the same next turn after one input")
    s, _ = server.create_session()
    check(s == 201, f"POST /session returns 201, got {s}", passed, failed)
    ws1 = await ws_connect(server.base_url)
    ws2 = await ws_connect(server.base_url)
    try:
        init1 = await recv_json(ws1)
        init2 = await recv_json(ws2)
        check(init1.get("protocolVersion") == 3 and init2.get("protocolVersion") == 3,
              "both WS clients receive initial turn", passed, failed)
        await ws1.send(json.dumps({"type": "input", "value": "0"}))
        nxt1 = await recv_json(ws1)
        nxt2 = await recv_json(ws2)
        check("protocolVersion" not in nxt1 and "protocolVersion" not in nxt2,
              "neither WS step turn has protocolVersion", passed, failed)
        check(nxt1 == nxt2, "both WS clients receive the SAME next turn (OutputHub broadcast)", passed, failed)
    finally:
        await ws1.close()
        await ws2.close()
        server.delete_session()


async def test_http_ws_coexistence(server, passed, failed):
    print("\n[coexist] HTTP long-poll + WS consume same session, consistent streams")
    s, _ = server.create_session()
    check(s == 201, f"POST /session returns 201, got {s}", passed, failed)
    ws = await ws_connect(server.base_url)
    try:
        _, http_init = server.get_turn(timeout=20)
        ws_init = await recv_json(ws)
        check(json.loads(http_init) == ws_init, "HTTP initial == WS initial", passed, failed)

        server.post_input("0")
        _, http_next = server.get_turn(timeout=20)
        ws_next = await recv_json(ws)
        check("protocolVersion" not in json.loads(http_next), "HTTP step turn has no protocolVersion", passed, failed)
        check("protocolVersion" not in ws_next, "WS step turn has no protocolVersion", passed, failed)
        check(json.loads(http_next) == ws_next, "HTTP next == WS next (shared OutputHub)", passed, failed)
    finally:
        await ws.close()
        server.delete_session()


async def test_late_join(server, passed, failed):
    print("\n[late-join] WS connecting after first turn receives only later turns")
    s, _ = server.create_session()
    check(s == 201, f"POST /session returns 201, got {s}", passed, failed)
    try:
        # Consume the initial turn over HTTP: proves it was already published.
        _, http_init = server.get_turn(timeout=20)
        init_turn = json.loads(http_init)
        check(init_turn.get("protocolVersion") == 3, "HTTP initial protocolVersion == 3", passed, failed)

        # Late WS connection: subscribes AFTER the initial turn -> must NOT receive it.
        ws = await ws_connect(server.base_url)
        try:
            server.post_input("0")
            _, http_next = server.get_turn(timeout=20)
            ws_next = await recv_json(ws)
            # Late joiner missed the initial turn (which had protocolVersion).
            check("protocolVersion" not in ws_next, "late WS first frame has no protocolVersion (joined late)", passed, failed)
            check(ws_next == json.loads(http_next), "late WS receives the step turn (shared stream)", passed, failed)
        finally:
            await ws.close()
    finally:
        server.delete_session()


async def _run(binary, game_dir):
    passed = [0]
    failed = [0]
    server = None
    try:
        server = start_server(str(game_dir), binary=binary)
        await reset_session(server)
        await test_gate_no_session(server, passed, failed)
        await reset_session(server)
        await test_initial_frame(server, passed, failed)
        await reset_session(server)
        await test_frame_consistency(server, passed, failed)
        await reset_session(server)
        await test_input_roundtrip(server, passed, failed)
        await reset_session(server)
        await test_fanout(server, passed, failed)
        await reset_session(server)
        await test_http_ws_coexistence(server, passed, failed)
        await reset_session(server)
        await test_late_join(server, passed, failed)
    finally:
        if server is not None:
            try:
                server.delete_session()
            except Exception:
                pass
            server.close()

    print(f"\n=== WebSocket transport test: {passed[0]} passed, {failed[0]} failed ===")
    return 0 if failed[0] == 0 else 1


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--binary", help="Path to Emuera binary (exe or dll)")
    parser.add_argument("--game-dir", default="test_game", help="Path to game directory")
    parser.add_argument("--suite-name", default="WebSocket transport", help="Test suite name in summary")
    args = parser.parse_args()

    if not _WS_AVAILABLE:
        print("SKIP: 'websockets' package not installed. Run: pip install websockets")
        print(f"=== {args.suite_name}: SKIPPED (0 passed, 0 failed) ===")
        return 0

    game_dir = Path(args.game_dir)
    if not game_dir.is_absolute():
        game_dir = _project_dir / game_dir

    return asyncio.run(_run(args.binary, game_dir))


if __name__ == "__main__":
    sys.exit(main())
