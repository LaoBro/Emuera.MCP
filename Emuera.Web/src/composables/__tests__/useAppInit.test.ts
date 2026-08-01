import { describe, it, expect, beforeEach, vi, afterEach } from 'vitest';
import { setActivePinia, createPinia } from 'pinia';
import { initAppState } from '../useAppInit';
import { useGameStore } from '../../stores/game';
import { useConnectionStore } from '../../stores/connection';

/**
 * T-025 D9 rev：initAppState 单测——App.vue onMounted 提取的逻辑。
 *
 * 测试矩阵（spec L122）：
 * - 空闲态（gameDir==null / state=="Idle"）→ 展示选择器 + 预填 localStorage，不自动 loadGame / connect
 * - 有活跃 session（state 非 Idle + gameDir 非 null）→ conn.connect() 重连
 * - server 未启动（fetch 抛错）→ 不自动连
 *
 * 推翻 issue 05「异则 loadGame」自动加载语义——空闲态不再自动 loadGame(localStorage value)。
 */

function makeLocalStorage(): Storage {
  const map = new Map<string, string>();
  return {
    getItem: (k: string) => (map.has(k) ? map.get(k)! : null),
    setItem: (k: string, v: string) => {
      map.set(k, v);
    },
    removeItem: (k: string) => {
      map.delete(k);
    },
    clear: () => {
      map.clear();
    },
    key: (i: number) => Array.from(map.keys())[i] ?? null,
    get length() {
      return map.size;
    },
  };
}

describe('initAppState (T-025 D9 rev)', () => {
  let fetchMock: ReturnType<typeof vi.fn>;

  beforeEach(() => {
    setActivePinia(createPinia());
    vi.stubGlobal('localStorage', makeLocalStorage());
    fetchMock = vi.fn();
    vi.stubGlobal('fetch', fetchMock);
  });

  afterEach(() => {
    vi.unstubAllGlobals();
    vi.restoreAllMocks();
  });

  /** 模拟 GET /state 响应。fetchAndApplyStateLayout 内部 fetch /state。 */
  function mockStateResponse(status: number, body: unknown): void {
    fetchMock.mockResolvedValueOnce({
      status,
      json: async () => body,
      text: async () => JSON.stringify(body),
    } as Response);
  }

  /** 模拟 GET /config 响应。fetchConfig 内部 fetch /config。 */
  function mockConfigResponse(status: number, body: unknown): void {
    fetchMock.mockResolvedValueOnce({
      status,
      json: async () => body,
      text: async () => JSON.stringify(body),
    } as Response);
  }

  it('空闲态（gameDir==null + state=="Idle"）→ 不调 connect，展示选择器', async () => {
    // localStorage 有上次目录——选择器应预填，但不自动 loadGame
    // 必须在 useGameStore() 之前设置（store 初始化时读 localStorage）
    localStorage.setItem('emuera.gameDir', 'D:/old/game');

    const game = useGameStore();
    const conn = useConnectionStore();
    const connectSpy = vi.spyOn(conn, 'connect').mockResolvedValue(undefined);

    // game.gameDir 从 localStorage 初始化——确认预填源
    expect(game.gameDir).toBe('D:/old/game');

    mockStateResponse(200, { state: 'Idle', gameDir: null });
    mockConfigResponse(200, { maxLog: 5000 });

    await initAppState();

    // D9 rev：空闲态不自动 loadGame / connect
    expect(connectSpy).not.toHaveBeenCalled();
    // gameDir 保留 localStorage 值（选择器预填源）
    expect(game.gameDir).toBe('D:/old/game');
    // serverState 应被 fetchAndApplyStateLayout 写入为 Idle
    expect(game.serverState).toBe('Idle');
  });

  it('有活跃 session（state=="WaitInput" + gameDir 非 null）→ conn.connect() 重连', async () => {
    const game = useGameStore();
    const conn = useConnectionStore();
    const connectSpy = vi.spyOn(conn, 'connect').mockResolvedValue(undefined);

    mockStateResponse(200, {
      state: 'WaitInput',
      gameDir: 'D:/current/game',
      windowWidth: 760,
      fontSize: 18,
      lineHeight: 19,
      gameColumns: 84,
      fontName: 'ＭＳ ゴシック',
    });
    mockConfigResponse(200, { maxLog: 5000 });

    await initAppState();

    expect(connectSpy).toHaveBeenCalledOnce();
    expect(game.serverState).toBe('WaitInput');
    expect(game.maxLog).toBe(5000);
  });

  it('server 未启动（fetch 抛错）→ 不调 connect', async () => {
    const game = useGameStore();
    const conn = useConnectionStore();
    const connectSpy = vi.spyOn(conn, 'connect').mockResolvedValue(undefined);

    fetchMock.mockRejectedValueOnce(new Error('Network error'));

    await initAppState();

    expect(connectSpy).not.toHaveBeenCalled();
    // serverState 保持初始 Idle（fetchAndApplyStateLayout catch 后不写 serverState）
    expect(game.serverState).toBe('Idle');
  });

  it('空闲态不自动 loadGame——推翻 issue 05 自动加载语义', async () => {
    // issue 05 旧行为：localStorage gameDir 与 server gameDir 不同 → 自动 loadGame(localStorage)
    // T-025 D9 rev：空闲态展示选择器 + 预填，不自动 loadGame
    // 必须在 useGameStore() 之前设置（store 初始化时读 localStorage）
    localStorage.setItem('emuera.gameDir', 'D:/old/game');

    const game = useGameStore();
    const conn = useConnectionStore();
    const connectSpy = vi.spyOn(conn, 'connect').mockResolvedValue(undefined);

    // localStorage 有值，server 返 idle（gameDir=null）——旧行为会 loadGame('D:/old')
    expect(game.gameDir).toBe('D:/old/game');

    mockStateResponse(200, { state: 'Idle', gameDir: null });
    mockConfigResponse(200, { maxLog: 5000 });

    await initAppState();

    // 不调 connect，不调 loadGame（fetch 调两次 = GET /state + GET /config，无 POST /load-game）
    expect(connectSpy).not.toHaveBeenCalled();
    expect(fetchMock).toHaveBeenCalledTimes(2);
    // gameDir 保留——选择器预填
    expect(game.gameDir).toBe('D:/old/game');
    expect(game.maxLog).toBe(5000);
  });

  it('GET /state 非 200 → 不调 connect（state==null 视为无法连接）', async () => {
    const conn = useConnectionStore();
    const connectSpy = vi.spyOn(conn, 'connect').mockResolvedValue(undefined);

    mockStateResponse(500, { error: 'Internal Server Error' });

    await initAppState();

    expect(connectSpy).not.toHaveBeenCalled();
  });

  it('有活跃 session 时写入窗口布局元信息', async () => {
    const game = useGameStore();
    const conn = useConnectionStore();
    vi.spyOn(conn, 'connect').mockResolvedValue(undefined);

    mockStateResponse(200, {
      state: 'WaitInput',
      gameDir: 'D:/game',
      windowWidth: 1000,
      fontSize: 20,
      lineHeight: 22,
      gameColumns: 99,
      fontName: 'CustomFont',
    });
    mockConfigResponse(200, { maxLog: 5000 });

    await initAppState();

    expect(game.windowWidth).toBe(1000);
    expect(game.fontSize).toBe(20);
    expect(game.lineHeight).toBe(22);
    expect(game.gameColumns).toBe(99);
    expect(game.fontName).toBe('CustomFont');
    expect(game.maxLog).toBe(5000);
  });
});

// ---------- handleMauiMessage：游戏线程存活探测回复 ----------

describe('handleMauiMessage - gameThreadStatus', () => {
  beforeEach(() => {
    setActivePinia(createPinia());
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('alive=true → gameStatusHint=running（游戏运行中提示）', async () => {
    // 模拟 MAUI unpackaged window（app.local 虚拟主机）+ chrome.webview 让 postInput 可用
    vi.stubGlobal('window', {
      location: { protocol: 'https:', hostname: 'app.local' },
      chrome: { webview: { postMessage: vi.fn() } },
    });

    await initAppState();

    const game = useGameStore();
    const handler = (window as any).__emueraOnMessage as ((msg: unknown) => void) | undefined;
    expect(typeof handler).toBe('function');

    handler!({ type: 'gameThreadStatus', alive: true });

    expect(game.gameStatusHint).toBe('running');
  });

  it('alive=false → gameStatusHint=stopped（游戏已停止提示）', async () => {
    vi.stubGlobal('window', {
      location: { protocol: 'https:', hostname: 'app.local' },
      chrome: { webview: { postMessage: vi.fn() } },
    });

    await initAppState();

    const game = useGameStore();
    const handler = (window as any).__emueraOnMessage as ((msg: unknown) => void) | undefined;

    handler!({ type: 'gameThreadStatus', alive: false });

    expect(game.gameStatusHint).toBe('stopped');
  });
});
