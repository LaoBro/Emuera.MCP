"""End-to-end checks for the server control handoff contract."""
import json
import sys
from pathlib import Path

TESTS_DIR = Path(__file__).resolve().parent
sys.path.insert(0, str(TESTS_DIR))

from emuera_server import TEST_GAME_DIR, start_server


def assert_json(status, body, expected_status):
    assert status == expected_status, (status, body)
    return json.loads(body) if body else {}


def main():
    server = start_server(str(TEST_GAME_DIR))
    try:
        status, body = server.start_session()
        assert status == 200, (status, body)

        token = "control-test-agent"
        other_token = "other-agent"

        status, body = server.request("POST", "/control/acquire", {"token": token})
        acquired = assert_json(status, body, 200)
        assert acquired["controller"]["kind"] == "agent"
        assert acquired["turnsAdvanced"] >= 0

        status, body = server.request("POST", "/control/acquire", {"token": token})
        assert_json(status, body, 200)

        status, body = server.request("POST", "/control/acquire", {"token": other_token})
        rejected = assert_json(status, body, 409)
        assert rejected["error"] in ("CONTROL_HELD", "CONTROL_HELD_BY_USER")

        status, body = server.post_input("1")
        rejected = assert_json(status, body, 409)
        assert rejected["reason"] == "CONTROL_HELD_BY_AGENT"
        assert "acquire" in rejected["hint"]

        status, body = server.request("POST", "/control/release", {"token": other_token})
        assert_json(status, body, 409)

        status, body = server.request("POST", "/control/acquire")
        stolen = assert_json(status, body, 200)
        assert stolen["controller"]["kind"] == "user"

        status, body = server.request("POST", "/control/release", {"token": token})
        assert_json(status, body, 409)

        status, body = server.request("GET", "/control")
        control = assert_json(status, body, 200)
        assert control["state"] == "held"

        status, body = server.request("POST", "/control/release")
        released = assert_json(status, body, 200)
        assert released["released"] is True
        assert released["state"] == "idle"
    finally:
        server.close()

    print("control handoff: PASS")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
