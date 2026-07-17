import { describe, it, expect, beforeEach, vi, afterEach } from 'vitest';
import { setActivePinia, createPinia } from 'pinia';
import { useConnectionStore } from '../connection';

/**
 * useConnectionStore 单测——覆盖 issue 03 的 session 创建前置修复。
 *
 * 修复背景：C# KestrelGameServer 协议契约——WS 升级前必须先 POST /session 创建会话，
 * 否则 server 接受 WS 升级后立即下发关闭码 4004 + reason "No active session"。
 * 前端 connect() 流程改为：POST /session → WS connect。
 *
 * 测试矩阵：
 * - deriveHttpBase（纯函数）：ws:// / wss:// / 不同 host:port / 含路径
 * - ensureSession（mock fetch）：201 / 409 / 500+JSON / 500+text / fetch 异常
 * - connect 端到端（mock fetch + fake WebSocket）：
 *   - session 创建成功 → WS onopen → status=connected
 *   - session 创建失败 → status=disconnected + closeReason，不创建 WS
 */

// ---------- deriveHttpBase 纯函数测试 ----------

describe('useConnectionStore.deriveHttpBase', () => {
  beforeEach(() => setActivePinia(createPinia()));

  it('ws:// URL → http:// 同源', () => {
    const conn = useConnectionStore();
    expect(conn.deriveHttpBase('ws://localhost:5173/ws')).toBe('http://localhost:5173');
    expect(conn.deriveHttpBase('ws://localhost:8080/ws')).toBe('http://localhost:8080');
  });

  it('wss:// URL → https:// 同源', () => {
    const conn = useConnectionStore();
    expect(conn.deriveHttpBase('wss://example.com/ws')).toBe('https://example.com');
    expect(conn.deriveHttpBase('wss://secure.host:443/ws')).toBe('https://secure.host:443');
  });

  it('含路径的 URL：只取 host:port，丢弃路径', () => {
    const conn = useConnectionStore();
    expect(conn.deriveHttpBase('ws://localhost:5173/some/path')).toBe('http://localhost:5173');
  });
});

// ---------- ensureSession mock fetch 测试 ----------

describe('useConnectionStore.ensureSession', () => {
  let fetchSpy: ReturnType<typeof vi.spyOn>;

  beforeEach(() => {
    setActivePinia(createPinia());
    fetchSpy = vi.spyOn(globalThis, 'fetch');
  });

  afterEach(() => {
    fetchSpy.mockRestore();
  });

  it('201：会话已创建，resolve', async () => {
    fetchSpy.mockResolvedValueOnce(
      new Response(JSON.stringify({ sessionId: 'abc', createdAt: '2026-01-01T00:00:00Z', state: 'Idle' }), {
        status: 201,
      }),
    );
    const conn = useConnectionStore();
    await expect(conn.ensureSession('http://localhost:5173')).resolves.toBeUndefined();
    expect(fetchSpy).toHaveBeenCalledWith('http://localhost:5173/session', { method: 'POST' });
  });

  it('409：已有活跃会话，视为成功 resolve', async () => {
    fetchSpy.mockResolvedValueOnce(
      new Response(JSON.stringify({ error: 'A session is already active' }), { status: 409 }),
    );
    const conn = useConnectionStore();
    await expect(conn.ensureSession('http://localhost:5173')).resolves.toBeUndefined();
  });

  it('500 + JSON error body：throw Error 含 detail', async () => {
    fetchSpy.mockResolvedValueOnce(
      new Response(JSON.stringify({ error: 'Internal server error' }), { status: 500 }),
    );
    const conn = useConnectionStore();
    await expect(conn.ensureSession('http://localhost:5173')).rejects.toThrow(
      /POST \/session 失败：HTTP 500.*Internal server error/,
    );
  });

  it('500 + 非 JSON body：throw Error 含 text', async () => {
    fetchSpy.mockResolvedValueOnce(
      new Response('plain text error', { status: 500 }),
    );
    const conn = useConnectionStore();
    await expect(conn.ensureSession('http://localhost:5173')).rejects.toThrow(
      /POST \/session 失败：HTTP 500.*plain text error/,
    );
  });

  it('fetch 网络异常：throw 原始 Error', async () => {
    fetchSpy.mockRejectedValueOnce(new TypeError('Failed to fetch'));
    const conn = useConnectionStore();
    await expect(conn.ensureSession('http://localhost:5173')).rejects.toThrow('Failed to fetch');
  });

  it('4xx（非 409）：throw Error', async () => {
    fetchSpy.mockResolvedValueOnce(
      new Response(JSON.stringify({ error: 'Bad request' }), { status: 400 }),
    );
    const conn = useConnectionStore();
    await expect(conn.ensureSession('http://localhost:5173')).rejects.toThrow(
      /HTTP 400.*Bad request/,
    );
  });
});

// ---------- connect 端到端流程测试（mock fetch + fake WebSocket） ----------

/**
 * 最小化 fake WebSocket——只实现 connect 流程需要的属性/方法。
 *
 * Vitest 默认 node 环境无 WebSocket 全局，需 vi.stubGlobal 注入。
 * 每个 fake 实例暴露 open/close/error/message 触发器，由测试主动调用模拟事件。
 */
class FakeWebSocket {
  static lastInstance: FakeWebSocket | null = null;
  static instances: FakeWebSocket[] = [];

  url: string;
  onopen: (() => void) | null = null;
  onmessage: ((event: { data: string }) => void) | null = null;
  onerror: (() => void) | null = null;
  onclose: ((event: { code: number; reason: string }) => void) | null = null;

  constructor(url: string) {
    this.url = url;
    FakeWebSocket.lastInstance = this;
    FakeWebSocket.instances.push(this);
  }

  close(code = 1000, reason = ''): void {
    if (this.onclose) this.onclose({ code, reason });
  }

  send(): void {
    // 测试中不验证发送内容
  }

  // 测试辅助：触发 onopen
  triggerOpen(): void {
    if (this.onopen) this.onopen();
  }
  triggerMessage(data: string): void {
    if (this.onmessage) this.onmessage({ data });
  }
  triggerError(): void {
    if (this.onerror) this.onerror();
  }
  triggerClose(code: number, reason: string): void {
    if (this.onclose) this.onclose({ code, reason });
  }
}

describe('useConnectionStore.connect — session 创建前置', () => {
  let fetchSpy: ReturnType<typeof vi.spyOn>;
  let originalWebSocket: typeof globalThis.WebSocket | undefined;

  beforeEach(() => {
    setActivePinia(createPinia());
    fetchSpy = vi.spyOn(globalThis, 'fetch');
    originalWebSocket = globalThis.WebSocket;
    FakeWebSocket.lastInstance = null;
    FakeWebSocket.instances = [];
    globalThis.WebSocket = FakeWebSocket as unknown as typeof globalThis.WebSocket;
  });

  afterEach(() => {
    fetchSpy.mockRestore();
    if (originalWebSocket === undefined) {
      delete (globalThis as { WebSocket?: unknown }).WebSocket;
    } else {
      globalThis.WebSocket = originalWebSocket;
    }
  });

  it('session 创建成功 → WS 升级 → onopen → status=connected', async () => {
    fetchSpy.mockResolvedValueOnce(
      new Response(JSON.stringify({ sessionId: 'abc', state: 'Idle' }), { status: 201 }),
    );
    const conn = useConnectionStore();
    const connectPromise = conn.connect('ws://localhost:5173/ws');

    // ensureSession await 期间 status='connecting'，closeReason=null
    expect(conn.status).toBe('connecting');
    expect(conn.closeReason).toBeNull();

    // ensureSession resolve → WebSocket 构造
    await connectPromise;
    expect(FakeWebSocket.lastInstance).not.toBeNull();
    expect(FakeWebSocket.lastInstance!.url).toBe('ws://localhost:5173/ws');
    expect(conn.status).toBe('connecting'); // 等 onopen

    // 触发 onopen
    FakeWebSocket.lastInstance!.triggerOpen();
    expect(conn.status).toBe('connected');
  });

  it('session 创建失败（500）→ status=disconnected + closeReason，不创建 WS', async () => {
    fetchSpy.mockResolvedValueOnce(
      new Response(JSON.stringify({ error: 'Server down' }), { status: 500 }),
    );
    const conn = useConnectionStore();
    await conn.connect('ws://localhost:5173/ws');

    expect(conn.status).toBe('disconnected');
    expect(conn.closeReason).toMatch(/POST \/session 失败：HTTP 500.*Server down/);
    expect(FakeWebSocket.lastInstance).toBeNull();
  });

  it('session 创建网络异常 → status=disconnected + closeReason', async () => {
    fetchSpy.mockRejectedValueOnce(new TypeError('Failed to fetch'));
    const conn = useConnectionStore();
    await conn.connect('ws://localhost:5173/ws');

    expect(conn.status).toBe('disconnected');
    expect(conn.closeReason).toBe('Failed to fetch');
    expect(FakeWebSocket.lastInstance).toBeNull();
  });

  it('409（已有活跃会话）→ 视为成功 → 继续 WS 升级', async () => {
    fetchSpy.mockResolvedValueOnce(
      new Response(JSON.stringify({ error: 'A session is already active' }), { status: 409 }),
    );
    const conn = useConnectionStore();
    await conn.connect('ws://localhost:5173/ws');

    expect(conn.status).toBe('connecting');
    expect(FakeWebSocket.lastInstance).not.toBeNull();
  });

  it('WS 连接后 onclose 4004 → status=reconnecting + closeReason=No active session', async () => {
    fetchSpy.mockResolvedValueOnce(
      new Response(JSON.stringify({ sessionId: 'abc', state: 'Idle' }), { status: 201 }),
    );
    const conn = useConnectionStore();
    await conn.connect('ws://localhost:5173/ws');
    FakeWebSocket.lastInstance!.triggerOpen();
    expect(conn.status).toBe('connected');

    // 模拟 server 端 session 突然消失（极端场景）→ 关闭码 4004
    FakeWebSocket.lastInstance!.triggerClose(4004, 'No active session');

    expect(conn.status).toBe('reconnecting');
    expect(conn.closeReason).toBe('No active session');
  });

  it('WS onclose 1000（正常关闭）→ status=disconnected（非 reconnecting）', async () => {
    fetchSpy.mockResolvedValueOnce(
      new Response(JSON.stringify({ sessionId: 'abc', state: 'Idle' }), { status: 201 }),
    );
    const conn = useConnectionStore();
    await conn.connect('ws://localhost:5173/ws');
    FakeWebSocket.lastInstance!.triggerOpen();
    expect(conn.status).toBe('connected');

    FakeWebSocket.lastInstance!.triggerClose(1000, '');

    expect(conn.status).toBe('disconnected'); // 正常关闭不进 reconnecting
  });

  it('WS 接收到 turn JSON → game store.applyTurn 被调用', async () => {
    fetchSpy.mockResolvedValueOnce(
      new Response(JSON.stringify({ sessionId: 'abc', state: 'Idle' }), { status: 201 }),
    );
    const conn = useConnectionStore();
    const { useGameStore } = await import('../game');
    const game = useGameStore();

    await conn.connect('ws://localhost:5173/ws');
    FakeWebSocket.lastInstance!.triggerOpen();

    // 模拟 server 推送一帧 turn
    const turnJson = JSON.stringify({
      state: 'WaitInput',
      needValue: false,
      protocolVersion: 5,
      diff: {
        lineOps: [
          { type: 'append', newLines: [{ entries: [{ segments: [{ text: 'Hello' }] }], isLineEnd: true }] },
        ],
      },
    });
    FakeWebSocket.lastInstance!.triggerMessage(turnJson);

    expect(game.lastTurnJson).toBe(turnJson);
    expect(game.displayState.lines).toHaveLength(1);
    expect(game.displayState.lines[0].entries[0].segments[0].text).toBe('Hello');
    expect(game.protocolVersion).toBe(5);
  });

  it('disconnect() 主动断开 → status=disconnected', async () => {
    fetchSpy.mockResolvedValueOnce(
      new Response(JSON.stringify({ sessionId: 'abc', state: 'Idle' }), { status: 201 }),
    );
    const conn = useConnectionStore();
    await conn.connect('ws://localhost:5173/ws');
    FakeWebSocket.lastInstance!.triggerOpen();
    expect(conn.status).toBe('connected');

    conn.disconnect();
    expect(conn.status).toBe('disconnected');
  });
});
