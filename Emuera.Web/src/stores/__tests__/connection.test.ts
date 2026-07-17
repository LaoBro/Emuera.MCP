import { describe, it, expect, beforeEach, vi, afterEach } from 'vitest';
import { setActivePinia, createPinia } from 'pinia';
import { useConnectionStore } from '../connection';

/**
 * useConnectionStore 单测——覆盖 issue 03 的 session + snapshot 双重前置修复。
 *
 * 修复背景 1：C# KestrelGameServer 协议契约——WS 升级前必须先 POST /session 创建会话，
 * 否则 server 接受 WS 升级后立即下发关闭码 4004 + reason "No active session"。
 * 修复背景 2：C# server 不在首帧 diff 重放历史 PRINT 输出——必须 GET /snapshot 拿全量状态。
 * 前端 connect() 流程改为：POST /session → GET /snapshot → setSnapshot → WS connect。
 *
 * 测试矩阵：
 * - deriveHttpBase（纯函数）：ws:// / wss:// / 不同 host:port / 含路径
 * - ensureSession（mock fetch）：201 / 409 / 500+JSON / 500+text / fetch 异常
 * - fetchSnapshot（mock fetch）：200 / 503 重试 / 503 上限 / 404 / 字段校验 / 网络异常
 * - connect 端到端（mock fetch + fake WebSocket）：
 *   - session + snapshot 成功 → WS onopen → status=connected + displayState 已填充
 *   - session 创建失败 → status=disconnected + closeReason，不创建 WS、不拉 snapshot
 *   - snapshot 拉取失败 → status=disconnected + closeReason，不创建 WS
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

// ---------- fetchSnapshot mock fetch 测试 ----------

/**
 * 构造合法 DisplaySnapshot JSON body——只覆盖 fetchSnapshot 校验的必填字段。
 */
function snapshotJson(lines: unknown[] = [], opts: { state?: string; needValue?: boolean; protocolVersion?: number } = {}): string {
  return JSON.stringify({
    lines,
    state: opts.state ?? 'WaitInput',
    inputType: null,
    needValue: opts.needValue ?? false,
    protocolVersion: opts.protocolVersion ?? 5,
  });
}

describe('useConnectionStore.fetchSnapshot', () => {
  let fetchSpy: ReturnType<typeof vi.spyOn>;
  let originalSetTimeout: typeof globalThis.setTimeout;

  beforeEach(() => {
    setActivePinia(createPinia());
    fetchSpy = vi.spyOn(globalThis, 'fetch');
    // setTimeout 替换为同步立即执行——503 重试测试不等真实 100ms
    originalSetTimeout = globalThis.setTimeout;
    globalThis.setTimeout = ((cb: () => void) => { cb(); return 0; }) as unknown as typeof globalThis.setTimeout;
  });

  afterEach(() => {
    fetchSpy.mockRestore();
    globalThis.setTimeout = originalSetTimeout;
  });

  it('200 + 合法 JSON：返回 DisplaySnapshot', async () => {
    const body = snapshotJson([{ entries: [{ segments: [{ text: 'Hello' }] }], isLineEnd: true }]);
    fetchSpy.mockResolvedValueOnce(new Response(body, { status: 200 }));

    const conn = useConnectionStore();
    const snapshot = await conn.fetchSnapshot('http://localhost:5173');
    expect(snapshot.lines).toHaveLength(1);
    expect(snapshot.state).toBe('WaitInput');
    expect(snapshot.protocolVersion).toBe(5);
    expect(fetchSpy).toHaveBeenCalledWith('http://localhost:5173/snapshot');
  });

  it('503 后 200：重试成功', async () => {
    fetchSpy.mockResolvedValueOnce(new Response('session not yet initialized', { status: 503 }));
    fetchSpy.mockResolvedValueOnce(new Response(snapshotJson(), { status: 200 }));

    const conn = useConnectionStore();
    const snapshot = await conn.fetchSnapshot('http://localhost:5173');
    expect(snapshot.state).toBe('WaitInput');
    expect(fetchSpy).toHaveBeenCalledTimes(2);
  });

  it('连续 503 直到上限：throw 含重试次数', async () => {
    for (let i = 0; i < 10; i++) {
      fetchSpy.mockResolvedValueOnce(new Response('session not initialized', { status: 503 }));
    }

    const conn = useConnectionStore();
    await expect(conn.fetchSnapshot('http://localhost:5173')).rejects.toThrow(
      /GET \/snapshot 重试 5 次仍 503/,
    );
  });

  it('404（无 session）：立即抛错不重试', async () => {
    fetchSpy.mockResolvedValueOnce(new Response(JSON.stringify({ error: 'No active session' }), { status: 404 }));

    const conn = useConnectionStore();
    await expect(conn.fetchSnapshot('http://localhost:5173')).rejects.toThrow(
      /GET \/snapshot 失败：HTTP 404.*No active session/,
    );
    expect(fetchSpy).toHaveBeenCalledTimes(1);
  });

  it('200 但 lines 非数组：抛解析失败', async () => {
    fetchSpy.mockResolvedValueOnce(new Response(JSON.stringify({ state: 'WaitInput', needValue: false, lines: 'oops' }), { status: 200 }));

    const conn = useConnectionStore();
    await expect(conn.fetchSnapshot('http://localhost:5173')).rejects.toThrow(
      /snapshot\.lines 缺失或非数组/,
    );
  });

  it('200 但 state 非 string：抛解析失败', async () => {
    fetchSpy.mockResolvedValueOnce(new Response(JSON.stringify({ state: 123, needValue: false, lines: [] }), { status: 200 }));

    const conn = useConnectionStore();
    await expect(conn.fetchSnapshot('http://localhost:5173')).rejects.toThrow(
      /snapshot\.state 缺失或非 string/,
    );
  });

  it('fetch 网络异常：throw 原始 Error', async () => {
    fetchSpy.mockRejectedValueOnce(new TypeError('Failed to fetch'));

    const conn = useConnectionStore();
    await expect(conn.fetchSnapshot('http://localhost:5173')).rejects.toThrow('Failed to fetch');
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

describe('useConnectionStore.connect — session + snapshot 前置', () => {
  let fetchSpy: ReturnType<typeof vi.spyOn>;
  let originalWebSocket: typeof globalThis.WebSocket | undefined;
  let originalSetTimeout: typeof globalThis.setTimeout;

  beforeEach(() => {
    setActivePinia(createPinia());
    fetchSpy = vi.spyOn(globalThis, 'fetch');
    originalWebSocket = globalThis.WebSocket;
    // setTimeout 替换为同步立即执行——503 重试路径不等真实 100ms
    originalSetTimeout = globalThis.setTimeout;
    globalThis.setTimeout = ((cb: () => void) => { cb(); return 0; }) as unknown as typeof globalThis.setTimeout;
    FakeWebSocket.lastInstance = null;
    FakeWebSocket.instances = [];
    globalThis.WebSocket = FakeWebSocket as unknown as typeof globalThis.WebSocket;
  });

  afterEach(() => {
    fetchSpy.mockRestore();
    globalThis.setTimeout = originalSetTimeout;
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
    fetchSpy.mockResolvedValueOnce(new Response(snapshotJson(), { status: 200 }));
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
    fetchSpy.mockResolvedValueOnce(new Response(snapshotJson(), { status: 200 }));
    const conn = useConnectionStore();
    await conn.connect('ws://localhost:5173/ws');

    expect(conn.status).toBe('connecting');
    expect(FakeWebSocket.lastInstance).not.toBeNull();
  });

  it('WS 连接后 onclose 4004 → status=reconnecting + closeReason=No active session', async () => {
    fetchSpy.mockResolvedValueOnce(
      new Response(JSON.stringify({ sessionId: 'abc', state: 'Idle' }), { status: 201 }),
    );
    fetchSpy.mockResolvedValueOnce(new Response(snapshotJson(), { status: 200 }));
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
    fetchSpy.mockResolvedValueOnce(new Response(snapshotJson(), { status: 200 }));
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
    // snapshot 返回空 lines——确保后续 applyTurn 后 displayState 只含 turn 新增的一行
    fetchSpy.mockResolvedValueOnce(new Response(snapshotJson(), { status: 200 }));
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
    fetchSpy.mockResolvedValueOnce(new Response(snapshotJson(), { status: 200 }));
    const conn = useConnectionStore();
    await conn.connect('ws://localhost:5173/ws');
    FakeWebSocket.lastInstance!.triggerOpen();
    expect(conn.status).toBe('connected');

    conn.disconnect();
    expect(conn.status).toBe('disconnected');
  });

  it('snapshot 拉取失败（500）→ status=disconnected + closeReason，不创建 WS', async () => {
    fetchSpy.mockResolvedValueOnce(
      new Response(JSON.stringify({ sessionId: 'abc', state: 'Idle' }), { status: 201 }),
    );
    // GET /snapshot 立即 500——fetchSnapshot 不重试非 503 状态码
    fetchSpy.mockResolvedValueOnce(
      new Response(JSON.stringify({ error: 'Snapshot internal error' }), { status: 500 }),
    );
    const conn = useConnectionStore();
    await conn.connect('ws://localhost:5173/ws');

    expect(conn.status).toBe('disconnected');
    expect(conn.closeReason).toMatch(/GET \/snapshot 失败：HTTP 500.*Snapshot internal error/);
    expect(FakeWebSocket.lastInstance).toBeNull();
  });

  it('snapshot 拉取成功 → game.setSnapshot 被调用 → displayState 有内容', async () => {
    fetchSpy.mockResolvedValueOnce(
      new Response(JSON.stringify({ sessionId: 'abc', state: 'Idle' }), { status: 201 }),
    );
    // snapshot 返回带 1 行 "Snapshot init"——验证 setSnapshot 把它写进 displayState
    const snapshotBody = snapshotJson(
      [{ entries: [{ segments: [{ text: 'Snapshot init' }] }], isLineEnd: true }],
      { state: 'WaitInput', needValue: true, protocolVersion: 5 },
    );
    fetchSpy.mockResolvedValueOnce(new Response(snapshotBody, { status: 200 }));

    const conn = useConnectionStore();
    const { useGameStore } = await import('../game');
    const game = useGameStore();

    await conn.connect('ws://localhost:5173/ws');
    // 注意：此时 WS 已构造但未 triggerOpen——displayState 应已由 setSnapshot 填充
    expect(game.displayState.lines).toHaveLength(1);
    expect(game.displayState.lines[0].entries[0].segments[0].text).toBe('Snapshot init');
    expect(game.displayState.state).toBe('WaitInput');
    expect(game.displayState.needValue).toBe(true);
    expect(game.protocolVersion).toBe(5);
    // setSnapshot 不写 lastTurnJson/turnHistory（snapshot 不是 WS 帧）
    expect(game.lastTurnJson).toBeNull();
    expect(game.turnHistory).toHaveLength(0);

    // 后续 WS 增量 diff 在 snapshot 基础上叠加——验证 wiring 正确
    FakeWebSocket.lastInstance!.triggerOpen();
    const turnJson = JSON.stringify({
      state: 'WaitInput',
      needValue: true,
      diff: {
        lineOps: [
          { type: 'append', newLines: [{ entries: [{ segments: [{ text: 'Appended' }] }], isLineEnd: true }] },
        ],
      },
    });
    FakeWebSocket.lastInstance!.triggerMessage(turnJson);

    expect(game.displayState.lines).toHaveLength(2);
    expect(game.displayState.lines[0].entries[0].segments[0].text).toBe('Snapshot init');
    expect(game.displayState.lines[1].entries[0].segments[0].text).toBe('Appended');
    expect(game.lastTurnJson).toBe(turnJson);
  });
});
