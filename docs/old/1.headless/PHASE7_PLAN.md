# Phase 7: Python 网关扩展 — `mcp_relay.py` 升级为完整协议网关

> 目标：在 Phase 6 的 HTTP 多会话服务器基础上，将 Python 层从单一进程管道模式升级为完整的协议网关，承担 MCP 协议转换、HTTP/WebSocket 服务、多用户会话映射、复杂逻辑扩展等职责。C# 端保持最小改动，仅提供 JSONL 协议与 HTTP API。

---

## 7.1 现状分析

### 7.1.1 Phase 6 已完成的成果

- C# 端 `Emuera.exe --server --port 8080` 提供 HTTP API：
  - `POST /sessions` — 创建会话
  - `GET /sessions/{id}` — 查询状态
  - `POST /sessions/{id}/input` — 提交输入
  - `GET /sessions/{id}/turn` — 长轮询获取回合
  - `DELETE /sessions/{id}` — 销毁会话
- C# 端支持 `--headless` 管道模式（单进程单会话 stdin/stdout JSONL）
- `AgentJsonlProtocol` 已解耦，支持注入 `SessionIO`

### 7.1.2 当前 `mcp_relay.py` 的限制

| 限制 | 说明 |
|------|------|
| 单进程单会话 | 直接 `subprocess.Popen` 启动 Emuera，一次只能运行一个游戏实例 |
| 管道耦合 | 通过 stdin/stdout 与 Emuera 交互，进程崩溃则会话丢失 |
| 无网络层 | 本身不暴露 HTTP/WebSocket，只能作为本地 MCP 子进程 |
| 无多用户隔离 | 多个 MCP 客户端共享同一个游戏进程和状态 |
| 无持久化 | 会话状态全部在内存，重启后丢失 |
| 扩展性差 | 图片处理、AI 逻辑等复杂功能难以在单文件中维护 |

### 7.1.3 Phase 7 需要引入的能力

1. **HTTP 客户端**：Python 层通过 HTTP API 与 C# 服务器交互，替代直接进程管理
2. **会话映射**：将 MCP 客户端标识映射到 C# 端的 sessionId，支持多用户隔离
3. **进程池管理**：按需启动/复用 C# 服务器进程，或连接到已运行的独立服务器
4. **MCP 协议完整实现**：支持 `resources`、`prompts`、`sampling` 等 MCP 能力（按需）
5. **复杂逻辑扩展点**：图片处理、AI 预处理、存档管理等可无缝接入
6. **可选独立服务模式**：`mcp_relay.py` 既可作为 MCP 子进程，也可作为独立网关服务

---

## 7.2 架构设计

### 7.2.1 总体架构

```
┌─────────────────────────────────────────────────────────────────────┐
│                        Python 网关层 (Phase 7)                       │
│  ┌─────────────┐  ┌─────────────┐  ┌─────────────┐  ┌───────────┐  │
│  │  MCP Server │  │ HTTP Server │  │  Session    │  │ Extension │  │
│  │  (stdio)    │  │  (可选)     │  │  Manager    │  │  Plugins  │  │
│  └──────┬──────┘  └──────┬──────┘  └──────┬──────┘  └─────┬─────┘  │
│         │                │                │               │        │
│         └────────────────┴────────────────┘               │        │
│                          │                                │        │
│                   ┌──────┴──────┐                         │        │
│                   │  EmueraClient │ ←──────────────────────┘        │
│                   │  (HTTP Client)│                                 │
│                   └──────┬──────┘                                 │
└──────────────────────────┼─────────────────────────────────────────┘
                           │ HTTP
┌──────────────────────────┼─────────────────────────────────────────┐
│  C# 后端层 (Phase 6)     │                                         │
│  ┌───────────────────────┴─────────────────────────────────────┐   │
│  │              Emuera.exe --server --port 8080                │   │
│  │  ┌─────────────┐  ┌─────────────┐  ┌─────────────────────┐ │   │
│  │  │HttpGameServer│  │SessionManager│  │  Session × N        │ │   │
│  │  │             │  │             │  │  ├─ HeadlessConsole  │ │   │
│  │  │             │  │             │  │  ├─ EmueraConsole    │ │   │
│  │  │             │  │             │  │  ├─ AgentJsonlProtocol│ │   │
│  │  │             │  │             │  │  └─ HttpSessionIO    │ │   │
│  │  └─────────────┘  └─────────────┘  └─────────────────────┘ │   │
│  └─────────────────────────────────────────────────────────────┘   │
└─────────────────────────────────────────────────────────────────────┘
```

### 7.2.2 两种运行模式

| 模式 | 说明 | 适用场景 |
|------|------|---------|
| **Embedded 模式** | Python 网关自行启动并管理 `Emuera.exe` 子进程（类似现有行为） | 单机使用、Claude Desktop 等直接调用 |
| **Standalone 模式** | Python 网关作为独立服务运行，连接到外部已启动的 C# 服务器 | 多用户、远程部署、与现有服务集成 |

### 7.2.3 与现有代码的集成点

```
mcp_relay.py (现有)
  ├── 保留: MCP JSON-RPC 协议处理逻辑
  ├── 替换: subprocess.Popen + stdin/stdout → EmueraClient (HTTP 客户端)
  ├── 新增: SessionManager (Python 层会话映射)
  ├── 新增: emuera_gateway 包结构
  └── 保留: --embedded 模式兼容旧行为（可选）

EmueraClient (新增)
  ├── POST /sessions → 创建会话，返回 sessionId
  ├── POST /sessions/{id}/input → 提交输入
  ├── GET /sessions/{id}/turn → 长轮询获取回合
  ├── GET /sessions/{id} → 查询状态
  └── DELETE /sessions/{id} → 销毁会话
```

---

## 7.3 修改方案

### Phase 7a: 项目结构重构 — 创建 `emuera_gateway` 包

**目标**：将单文件 `mcp_relay.py` 重构为可维护的 Python 包。

**新增/修改文件：**

| 文件 | 说明 |
|------|------|
| `emuera_gateway/__init__.py` | 包入口，暴露主要类 |
| `emuera_gateway/__main__.py` | `python -m emuera_gateway` 启动入口 |
| `emuera_gateway/config.py` | 配置加载/保存（替代现有 `_load_config` 等） |
| `emuera_gateway/emuera_client.py` | C# 服务器 HTTP 客户端 |
| `emuera_gateway/session.py` | Python 层会话管理 |
| `emuera_gateway/mcp_server.py` | MCP JSON-RPC 协议处理 |
| `emuera_gateway/plugin.py` | 扩展插件基类与加载器 |
| `mcp_relay.py` | 保留为兼容入口，内部转发到包 |

**目录结构：**

```
Emuera.MCP/
├── mcp_relay.py              # 兼容入口（保留）
├── emuera_gateway/
│   ├── __init__.py
│   ├── __main__.py
│   ├── config.py
│   ├── emuera_client.py
│   ├── session.py
│   ├── mcp_server.py
│   ├── plugin.py
│   └── plugins/              # 扩展插件目录
│       ├── __init__.py
│       └── image_handler.py  # 图片处理预留
```

**验收：**
- `python -m emuera_gateway --help` 正常输出
- `python mcp_relay.py` 仍能正常运行（向后兼容）

---

### Phase 7b: `EmueraClient` — C# 服务器 HTTP 客户端（Phase 7b）

**目标**：封装与 C# 端 HTTP API 的通信。

**文件：** `emuera_gateway/emuera_client.py`

```python
import json
import urllib.request
import urllib.error
from typing import Optional, Dict, Any


class EmueraClient:
    """HTTP client for Emuera C# server."""

    def __init__(self, base_url: str = "http://localhost:8080"):
        self.base_url = base_url.rstrip("/")

    def create_session(self) -> Dict[str, Any]:
        """POST /sessions → {"sessionId", "createdAt"}"""
        req = urllib.request.Request(
            f"{self.base_url}/sessions",
            method="POST",
            headers={"Accept": "application/json"}
        )
        with urllib.request.urlopen(req, timeout=10) as resp:
            return json.loads(resp.read().decode("utf-8"))

    def get_session(self, session_id: str) -> Optional[Dict[str, Any]]:
        """GET /sessions/{id} → session state or None if 404."""
        try:
            with urllib.request.urlopen(
                f"{self.base_url}/sessions/{session_id}", timeout=10
            ) as resp:
                return json.loads(resp.read().decode("utf-8"))
        except urllib.error.HTTPError as e:
            if e.code == 404:
                return None
            raise

    def send_input(self, session_id: str, value: str = "") -> bool:
        """POST /sessions/{id}/input → True if accepted."""
        body = json.dumps({"type": "input", "value": value}).encode("utf-8")
        req = urllib.request.Request(
            f"{self.base_url}/sessions/{session_id}/input",
            data=body,
            method="POST",
            headers={"Content-Type": "application/json", "Accept": "application/json"}
        )
        try:
            with urllib.request.urlopen(req, timeout=10) as resp:
                return resp.status == 200
        except urllib.error.HTTPError:
            return False

    def get_turn(self, session_id: str, timeout: float = 30.0) -> Optional[Dict[str, Any]]:
        """GET /sessions/{id}/turn (long polling). Returns turn JSON or None."""
        try:
            with urllib.request.urlopen(
                f"{self.base_url}/sessions/{session_id}/turn",
                timeout=timeout
            ) as resp:
                if resp.status == 204:
                    return None
                return json.loads(resp.read().decode("utf-8"))
        except urllib.error.HTTPError as e:
            if e.code == 404:
                return None
            raise

    def delete_session(self, session_id: str) -> bool:
        """DELETE /sessions/{id} → True if removed."""
        req = urllib.request.Request(
            f"{self.base_url}/sessions/{session_id}",
            method="DELETE"
        )
        try:
            with urllib.request.urlopen(req, timeout=10) as resp:
                return resp.status == 200
        except urllib.error.HTTPError:
            return False
```

**验收：**
- 单元测试：mock HTTP 响应，验证各方法解析正确
- 集成测试：C# 服务器运行后，`EmueraClient` 能创建会话并获取回合

---

### Phase 7c: `SessionManager` — Python 层会话映射（Phase 7c）

**目标**：管理 MCP 客户端与 C# sessionId 的映射，支持多用户隔离。

**文件：** `emuera_gateway/session.py`

```python
import threading
import time
from typing import Dict, Optional, Any
from .emuera_client import EmueraClient


class GameSession:
    """Python-side wrapper for a single Emuera game session."""

    def __init__(self, session_id: str, client: EmueraClient):
        self.session_id = session_id
        self.client = client
        self.created_at = time.time()
        self.last_activity = time.time()
        self._lock = threading.Lock()

    def touch(self):
        self.last_activity = time.time()

    def step(self, value: str = "") -> Optional[Dict[str, Any]]:
        """Send input and fetch next turn via long polling."""
        with self._lock:
            self.touch()
            if not self.client.send_input(self.session_id, value):
                return None
            turn = self.client.get_turn(self.session_id)
            self.touch()
            return turn

    def get_state(self) -> Optional[Dict[str, Any]]:
        """Fetch current turn (non-blocking, uses cached or new long poll)."""
        with self._lock:
            self.touch()
            return self.client.get_turn(self.session_id, timeout=5.0)

    def destroy(self) -> bool:
        return self.client.delete_session(self.session_id)


class SessionManager:
    """Manages multiple game sessions, mapping client identities to sessions."""

    def __init__(self, client: EmueraClient, idle_timeout: float = 1800.0):
        self.client = client
        self.idle_timeout = idle_timeout
        self._sessions: Dict[str, GameSession] = {}
        self._lock = threading.Lock()

    def create(self, client_id: str) -> GameSession:
        """Create a new session for a client. Reuses if already exists."""
        with self._lock:
            if client_id in self._sessions:
                return self._sessions[client_id]
            resp = self.client.create_session()
            session_id = resp["sessionId"]
            gs = GameSession(session_id, self.client)
            self._sessions[client_id] = gs
            return gs

    def get(self, client_id: str) -> Optional[GameSession]:
        with self._lock:
            return self._sessions.get(client_id)

    def remove(self, client_id: str) -> bool:
        with self._lock:
            gs = self._sessions.pop(client_id, None)
            if gs:
                gs.destroy()
                return True
            return False

    def cleanup_idle(self):
        """Remove sessions idle longer than timeout."""
        cutoff = time.time() - self.idle_timeout
        with self._lock:
            to_remove = [
                cid for cid, gs in self._sessions.items()
                if gs.last_activity < cutoff
            ]
            for cid in to_remove:
                gs = self._sessions.pop(cid)
                gs.destroy()
```

**验收：**
- 单元测试：验证 `create`/`get`/`remove`/`cleanup_idle` 行为
- 集成测试：两个 `client_id` 对应独立 session，输入互不影响

---

### Phase 7d: `McpServer` — MCP 协议处理重构（Phase 7d）

**目标**：将现有 `mcp_relay.py` 中的 MCP 逻辑迁移到 `mcp_server.py`，并适配新的 `SessionManager`。

**文件：** `emuera_gateway/mcp_server.py`

**关键改动：**
- `emuera_step` / `emuera_get_state` 不再直接操作 `subprocess.Popen`，而是通过 `SessionManager`
- 新增 `client_id` 来源：MCP 请求的 `params._meta` 或进程级隔离
- 保留所有现有 tool schema，行为不变

```python
import json
import sys
from typing import Any, Dict, Optional, Tuple
from .session import SessionManager


class McpServer:
    """MCP JSON-RPC 2.0 server over stdin/stdout."""

    def __init__(self, session_manager: SessionManager):
        self.sm = session_manager

    def handle(self, req: Dict[str, Any]) -> Tuple[Optional[Dict[str, Any]], bool]:
        """Returns (response, is_notification)."""
        req_id = req.get("id")
        method = req.get("method", "")
        params = req.get("params", {})

        if method == "initialize":
            return self._initialize(req_id), False
        if method == "notifications/initialized":
            return None, True
        if method == "tools/list":
            return self._tools_list(req_id), False
        if method == "tools/call":
            return self._tools_call(req_id, params), False
        return self._error(req_id, -32601, f"Method not found: {method}"), False

    def _tools_call(self, req_id, params):
        tool_name = params.get("name", "")
        args = params.get("arguments", {})
        # 从 params 中提取 client_id（若 MCP 客户端支持）
        client_id = params.get("_meta", {}).get("clientId", "default")

        if tool_name == "emuera_step":
            session = self.sm.create(client_id)
            turn = session.step(args.get("value", ""))
            if turn is None:
                return self._error(req_id, -32000, "Session lost or game ended"), False
            return self._success(req_id, json.dumps(turn)), False

        if tool_name == "emuera_get_state":
            session = self.sm.create(client_id)
            turn = session.get_state()
            if turn is None:
                return self._error(req_id, -32000, "Session not ready"), False
            return self._success(req_id, json.dumps(turn)), False

        if tool_name == "emuera_kill":
            removed = self.sm.remove(client_id)
            return self._success(req_id, json.dumps({
                "killed": removed,
                "message": "Session terminated" if removed else "No session was running"
            })), False

        # ... emuera_set_config / emuera_get_config 保留原有逻辑 ...

        return self._error(req_id, -32601, f"Unknown tool: {tool_name}"), False

    def run(self):
        for line in sys.stdin:
            line = line.strip()
            if not line:
                continue
            try:
                req = json.loads(line)
            except json.JSONDecodeError:
                continue
            resp, is_notification = self.handle(req)
            if resp is not None:
                sys.stdout.write(json.dumps(resp) + "\n")
                sys.stdout.flush()
```

**验收：**
- `python -m emuera_gateway` 作为 MCP 子进程，Claude Code 能正常调用 `emuera_step`
- 多轮对话行为与旧 `mcp_relay.py` 一致

---

### Phase 7e: 进程管理 — Embedded 模式与 Standalone 模式（Phase 7e）

**目标**：支持两种运行模式，兼容旧行为同时支持独立部署。

**文件：** `emuera_gateway/__main__.py`

```python
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

    # 默认 embedded 模式
    if not args.standalone:
        config = load_config()
        binary_path = args.emuera_path or config.get("binaryPath")
        game_dir = args.game_dir or config.get("gameDir")
        if not binary_path or not game_dir:
            print("Error: --emuera-path and --game-dir required for embedded mode",
                  file=sys.stderr)
            sys.exit(1)

        proc = _start_emuera_server(binary_path, game_dir, args.port)
        time.sleep(2)  # 等待服务器启动
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
```

**验收：**
- `python -m emuera_gateway --embedded --emuera-path ./Emuera.exe --game-dir ./test_game`
  - 自动启动 C# 服务器，MCP 正常交互
- `python -m emuera_gateway --standalone --server-url http://localhost:8080`
  - 连接到已运行的 C# 服务器，MCP 正常交互

---

### Phase 7f: 扩展插件系统 — 预留复杂逻辑接入点（Phase 7f）

**目标**：提供插件机制，使图片处理、AI 逻辑等可在不修改核心代码的情况下扩展。

**文件：** `emuera_gateway/plugin.py`

```python
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
        from . import plugins
        for _, name, _ in pkgutil.iter_modules(plugins.__path__):
            mod = importlib.import_module(f"{plugins.__name__}.{name}")
            for attr in dir(mod):
                cls = getattr(mod, attr)
                if isinstance(cls, type) and issubclass(cls, Plugin) and cls is not Plugin:
                    inst = cls()
                    self._plugins.append(inst)

    def process_turn(self, turn: Dict[str, Any]) -> Dict[str, Any]:
        for p in self._plugins:
            turn = p.on_turn(turn)
        return turn

    def process_input(self, value: str) -> str:
        for p in self._plugins:
            value = p.on_input(value)
        return value
```

**预留插件：** `emuera_gateway/plugins/image_handler.py`

```python
from ..plugin import Plugin


class ImageHandlerPlugin(Plugin):
    """Placeholder for future image processing logic."""

    name = "image_handler"

    def on_turn(self, turn):
        # TODO: intercept image generation commands, offload to external service
        return turn

    def on_input(self, value):
        return value
```

**验收：**
- `PluginManager.load_all()` 能自动发现 `ImageHandlerPlugin`
- `process_turn` / `process_input` 链式调用正常

---

### Phase 7g: 兼容入口与回归测试（Phase 7g）

**目标**：保留 `mcp_relay.py` 作为兼容入口，确保旧工作流不受影响。

**文件：** `mcp_relay.py`（修改）

```python
#!/usr/bin/env python3
"""Backward-compatible entry point.

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
        # Legacy mode: direct subprocess (保留原有完整代码)
        from emuera_gateway.legacy import main as legacy_main
        legacy_main()


if __name__ == "__main__":
    main()
```

> 或者更简单的方案：将原有 `mcp_relay.py` 完整内容移入 `emuera_gateway/legacy.py`，`mcp_relay.py` 仅做 import 转发。

**验收：**
- `python mcp_relay.py`（无参数）行为与之前完全一致
- `python mcp_relay.py --embedded --emuera-path ./Emuera.exe` 使用新网关
- Claude Desktop / Cursor 等 MCP 客户端配置无需修改即可工作

---

## 7.4 修改步骤汇总

### Phase 7a: 项目结构重构
- [ ] **Step 7a.1** 创建 `emuera_gateway/` 目录与 `__init__.py`
- [ ] **Step 7a.2** 创建 `config.py`，迁移配置读写逻辑
- [ ] **Step 7a.3** 创建 `__main__.py`，添加参数解析骨架
- [ ] **Step 7a.4** 将现有 `mcp_relay.py` 完整逻辑备份为 `emuera_gateway/legacy.py`
- [ ] **Step 7a.5** 修改 `mcp_relay.py` 为兼容转发入口

### Phase 7b: `EmueraClient` HTTP 客户端
- [ ] **Step 7b.1** 新建 `emuera_gateway/emuera_client.py`
- [ ] **Step 7b.2** 实现 `create_session` / `send_input` / `get_turn` / `delete_session`
- [ ] **Step 7b.3** 单元测试（mock HTTP）
- [ ] **Step 7b.4** 集成测试（连接运行中的 C# 服务器）

### Phase 7c: `SessionManager` 会话映射
- [ ] **Step 7c.1** 新建 `emuera_gateway/session.py`
- [ ] **Step 7c.2** 实现 `GameSession` 与 `SessionManager`
- [ ] **Step 7c.3** 单元测试（多 client_id 隔离）

### Phase 7d: `McpServer` 协议重构
- [ ] **Step 7d.1** 新建 `emuera_gateway/mcp_server.py`
- [ ] **Step 7d.2** 迁移 `tools/list` 与 `tools/call` 逻辑
- [ ] **Step 7d.3** 适配 `SessionManager` 替代直接 subprocess
- [ ] **Step 7d.4** MCP 客户端端到端测试

### Phase 7e: 进程管理与运行模式
- [ ] **Step 7e.1** 完善 `__main__.py` 的 `--embedded` 与 `--standalone` 分支
- [ ] **Step 7e.2** 实现 `_start_emuera_server` 与进程生命周期管理
- [ ] **Step 7e.3** 测试 Embedded 模式：自动启动 C# 服务器
- [ ] **Step 7e.4** 测试 Standalone 模式：连接外部服务器

### Phase 7f: 扩展插件系统
- [ ] **Step 7f.1** 新建 `emuera_gateway/plugin.py`
- [ ] **Step 7f.2** 新建 `emuera_gateway/plugins/` 目录与 `image_handler.py`
- [ ] **Step 7f.3** 在 `McpServer` 中集成 `PluginManager`
- [ ] **Step 7f.4** 验证插件链式调用

### Phase 7g: 兼容入口与回归测试
- [ ] **Step 7g.1** 确保 `python mcp_relay.py` 无参数时走 Legacy 模式
- [ ] **Step 7g.2** Claude Desktop MCP 配置回归测试
- [ ] **Step 7g.3** 多轮游戏对话一致性对比（旧版 vs 新版）

---

## 7.5 验收标准

### Phase 7a-7c 验收
```bash
# 1. 包结构正确
python -c "from emuera_gateway import EmueraClient, SessionManager, McpServer; print('OK')"

# 2. EmueraClient 能连接 C# 服务器
python -c "
from emuera_gateway.emuera_client import EmueraClient
c = EmueraClient('http://localhost:8080')
print(c.create_session())
"
```

### Phase 7d-7e 验收
```bash
# 1. Embedded 模式：自动启动并交互
python -m emuera_gateway \
  --embedded \
  --emuera-path ./Emuera.exe \
  --game-dir ./test_game \
  --port 8080
# → stderr 显示 C# 服务器启动日志
# → MCP 客户端能正常调用 emuera_step

# 2. Standalone 模式：连接已运行服务器
Emuera.exe --server --port 8080 --ExeDir ./test_game
# 另一个终端：
python -m emuera_gateway --standalone --server-url http://localhost:8080
# → MCP 客户端能正常调用 emuera_step
```

### Phase 7f-7g 验收
```bash
# 1. 插件系统
python -c "
from emuera_gateway.plugin import PluginManager
pm = PluginManager()
pm.load_all()
print([p.name for p in pm._plugins])
"
# → ['image_handler']

# 2. 兼容入口
python mcp_relay.py
# → 行为与旧版完全一致（直接 subprocess 模式）

# 3. MCP 客户端端到端
# Claude Desktop / Cursor 配置 mcp_relay.py 后：
# - emuera_set_config 正常
# - emuera_get_state 正常
# - emuera_step 多轮正常
# - emuera_kill 正常
```

---

## 7.6 风险与缓解

| 风险 | 影响 | 缓解 |
|------|------|------|
| `urllib` 长轮询阻塞 stdin 读取线程 | 高 | 使用 `threading` 将 `get_turn` 放到独立线程；或换用 `requests` + `Session` |
| C# 服务器启动慢导致首次请求失败 | 中 | Embedded 模式启动后 `time.sleep(2)` 并轮询 `GET /sessions` 探测就绪 |
| 多 MCP 客户端共享同一 Python 进程 | 中 | `client_id` 从 `params._meta` 或进程环境变量区分；默认 `default` |
| Legacy 模式代码漂移 | 低 | `legacy.py` 为完整副本，旧工作流不受影响；新功能只在包中开发 |
| 插件加载失败导致网关崩溃 | 低 | `PluginManager.load_all` 捕获异常，跳过故障插件并记录警告 |

---

## 7.7 与后续阶段的衔接

- **TODO 冻结项**：WebSocket 实时推送、跨平台编译（Linux/macOS）
- **Phase 7 完成后**，Python 网关成为主要交互入口：
  - C# 端专注脚本执行与状态管理
  - Python 端专注协议转换、会话管理、复杂逻辑
  - 新增功能（如图片处理、AI 对话）通过插件系统接入，无需修改 C# 代码

---

## 7.8 检查清单

- [ ] `emuera_gateway/` 包结构创建完成
- [ ] `EmueraClient` HTTP 客户端实现并测试
- [ ] `SessionManager` 多会话映射实现并测试
- [ ] `McpServer` MCP 协议重构完成
- [ ] `--embedded` 模式自动启动 C# 服务器并交互
- [ ] `--standalone` 模式连接外部 C# 服务器并交互
- [ ] 插件系统框架与 `image_handler` 预留实现
- [ ] `mcp_relay.py` 兼容入口保留旧行为
- [ ] Claude Desktop / Cursor MCP 客户端回归测试通过
- [ ] 多轮游戏对话行为与旧版一致
