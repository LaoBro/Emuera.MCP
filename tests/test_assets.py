"""Test: /assets 资源通道（issue 03）——路径消毒 + 白名单 + 缓存/CORS + 协议帧 image segment。

验证 spec Q5 安全决策端到端（HTTP seam，tests/ 既有套件风格）：

1. GET /assets/img/test.png → 200 + image/png + Cache-Control + CORS 头
2. GET /assets/Face_1（sprite 名，无扩展名）→ 200——经 resources/Face.csv 映射到
   1_Face.png（era 的 <img src='Face_1'> 引用 sprite 名而非文件路径）
3. 白名单外扩展名（csv/ERB/config）→ 404（游戏源文件永不泄出）
4. 路径穿越（%2e%2e / %2e%2e%2f / %5c 反斜杠变体）→ 4xx
   （Kestrel PathNormalization 或 AssetPathValidator 消毒拒绝，二者皆满足安全目标）
5. 不存在文件 / 目录请求 → 404
6. GET /snapshot 协议帧 image segment：src=img/test.png、width=36、height=18、ypos=0
   —— 8×4 PNG 缺省高度=FontSize(18) → 纵横比 width = 8*18/4 = 36（探针端到端验证）
7. 协议帧 sprite 图片：src=Face_1、width=36、height=18——探针经 ImageNameTable
   （resources csv）定位 1_Face.png 读 8×4 推算（sprite 名解析端到端验证）

Usage:
    python test_assets.py --binary <path> --game-dir test_game
"""
import argparse
import json
import os
import sys
import urllib.error
import urllib.request
from pathlib import Path

_project_dir = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, _project_dir)
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from emuera_server import PROTOCOL_VERSION, start_server

passed = [0]
failed = [0]


def check(condition, message):
    if condition:
        passed[0] += 1
        print(f"  PASS: {message}")
    else:
        failed[0] += 1
        print(f"  FAIL: {message}")


def raw_get(server, path):
    """原始 GET（不解码 body——图片是二进制）。返回 (status, body_bytes, headers)。"""
    req = urllib.request.Request(f"{server.base_url}{path}", method="GET")
    try:
        with urllib.request.urlopen(req, timeout=15) as resp:
            return resp.status, resp.read(), dict(resp.headers)
    except urllib.error.HTTPError as e:
        return e.code, e.read(), dict(e.headers)


def find_image_segment(snapshot):
    """在快照 lines 中找第一个含 image 的 segment。"""
    for line in snapshot.get("lines", []):
        for entry in line.get("entries", []):
            for seg in entry.get("segments", []):
                if seg.get("image") is not None:
                    return seg["image"]
    return None


def find_all_image_segments(snapshot):
    """按出现顺序收集全部含 image 的 segment（sprite 名断言用）。"""
    out = []
    for line in snapshot.get("lines", []):
        for entry in line.get("entries", []):
            for seg in entry.get("segments", []):
                if seg.get("image") is not None:
                    out.append(seg["image"])
    return out


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--binary", required=True)
    parser.add_argument("--game-dir", required=True)
    args = parser.parse_args()

    print(f"protocolVersion expected: {PROTOCOL_VERSION}")
    server = start_server(game_dir=args.game_dir, binary=args.binary)
    try:
        status, body = server.start_session()
        check(status == 200, f"POST /load-game -> {status}")
        if status != 200:
            print(body)
            sys.exit(1)

        # ---------- 协议帧 image segment 几何断言 ----------
        # 先 GET /turn 等首帧 turn 产生（游戏执行 @SYSTEM_TITLE，缓冲区填充），再取快照
        status, turn_body = server.get_turn()
        check(status == 200, f"GET /turn (first frame) -> {status}")
        status, snap_body = server.get_snapshot()
        check(status == 200, f"GET /snapshot -> {status}")
        snap = json.loads(snap_body)
        imgs = find_all_image_segments(snap)
        check(len(imgs) >= 2, f"snapshot contains 2 image segments (direct + sprite, got {len(imgs)})")
        img = imgs[0] if imgs else None
        check(img is not None, "snapshot contains an image segment")
        if img is not None:
            check(img["src"] == "img/test.png", f"image.src == 'img/test.png' (got '{img['src']}')")
            check(img["width"] == 36, f"image.width == 36 (8x4 aspect @ h=18, got {img['width']})")
            check(img["height"] == 18, f"image.height == 18 (FontSize, got {img['height']})")
            check(img["ypos"] == 0, f"image.ypos == 0 (got {img['ypos']})")

        # ---------- 协议帧：sprite 名图片（era 的 <img src='Face_1'>） ----------
        # src 是 sprite 名（无扩展名），几何经 ImageNameTable → resources/1_Face.png 探针
        if len(imgs) >= 2:
            spr = imgs[1]
            check(spr["src"] == "Face_1", f"sprite image.src == 'Face_1' (got '{spr['src']}')")
            check(spr["width"] == 36, f"sprite image.width == 36 (8x4 aspect @ h=18, got {spr['width']})")
            check(spr["height"] == 18, f"sprite image.height == 18 (FontSize, got {spr['height']})")

        # ---------- /assets 正常加载 ----------
        status, body, headers = raw_get(server, "/assets/img/test.png")
        check(status == 200, f"GET /assets/img/test.png -> {status}")
        check(body[:8] == b"\x89PNG\r\n\x1a\n", "body is a PNG signature")
        check(headers.get("Content-Type") == "image/png",
              f"Content-Type == image/png (got {headers.get('Content-Type')})")
        cc = headers.get("Cache-Control", "")
        check("max-age" in cc, f"Cache-Control carries max-age (got '{cc}')")
        check(headers.get("Access-Control-Allow-Origin") == "*",
              "CORS Access-Control-Allow-Origin == *")

        # ---------- /assets sprite 名解析（无扩展名 → resources csv 映射） ----------
        status, body, headers = raw_get(server, "/assets/Face_1")
        check(status == 200, f"GET /assets/Face_1 (sprite name) -> {status}")
        check(body[:8] == b"\x89PNG\r\n\x1a\n", "sprite body is a PNG signature")
        check(headers.get("Content-Type") == "image/png",
              f"sprite Content-Type == image/png (got {headers.get('Content-Type')})")

        # ---------- 白名单外扩展名 404 ----------
        for path, name in [
            ("/assets/csv/GameBase.csv", "csv source"),
            ("/assets/erb/TEST.ERB", "ERB source"),
            ("/assets/emuera.config", "config file"),
            ("/assets/setting.json", "json file"),
            ("/assets/img/page.html", "html source"),
        ]:
            status, _, _ = raw_get(server, path)
            check(status == 404, f"GET {path} ({name}) -> 404 (got {status})")

        # ---------- 路径穿越拒绝（编码形式——urllib 会客户端规范化裸 ..） ----------
        for path, name in [
            ("/assets/%2e%2e/csv/GameBase.csv", "encoded .. traversal"),
            ("/assets/img/%2e%2e/%2e%2e/csv/GameBase.csv", "nested .. traversal"),
            ("/assets/%2e%2e%2f%2e%2e%2fcsv%2fGameBase.csv", "encoded slash traversal"),
            ("/assets/img/..%5c..%5ccsv%5cGameBase.csv", "backslash traversal"),
            ("/assets/%2e%2e/setting.json", "encoded .. to json"),
        ]:
            status, _, _ = raw_get(server, path)
            check(400 <= status <= 499, f"GET {path} ({name}) rejected ({status})")

        # ---------- 不存在文件 / 目录 ----------
        status, _, _ = raw_get(server, "/assets/img/missing.png")
        check(status == 404, f"GET /assets/img/missing.png -> 404 (got {status})")
        status, _, _ = raw_get(server, "/assets/img/")
        check(status == 404, f"GET /assets/img/ (dir) -> 404 (got {status})")
        status, _, _ = raw_get(server, "/assets/")
        check(400 <= status <= 499, f"GET /assets/ (empty path) rejected ({status})")
    finally:
        server.close()

    print(f"\n{passed[0]} passed, {failed[0]} failed")
    sys.exit(1 if failed[0] else 0)


if __name__ == "__main__":
    main()
