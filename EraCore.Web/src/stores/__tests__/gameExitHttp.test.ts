import { describe, it, expect, beforeEach, vi, afterEach } from 'vitest';
import { setActivePinia, createPinia } from 'pinia';
import { useGameStore } from '../game';

/**
 * HTTP 模式退出游戏（exitGameHttp）单测——Web 游戏页「退出」路径。
 *
 * 测试矩阵：
 * - 正常退出：DELETE /session + 清状态回列表态 + 重新扫描主目录
 * - 二次进入保护：exitStatus 已是 'exiting' → 直接返回，不调 fetch
 * - DELETE 失败不阻断：仍清状态回列表态 + 重新扫描（不抛异常）
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

describe('useGameStore HTTP 退出（exitGameHttp）', () => {
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

  /** 构造一个"游戏中"的 store 状态（有 gameDir + 活跃 serverState + 主目录）。 */
  function setupActiveGame(): ReturnType<typeof useGameStore> {
    const game = useGameStore();
    game.setGameDir('D:/emuera/g1');
    game.applyServerState('WaitInput');
    game.setMainGameDir('D:/emuera');
    return game;
  }

  it('正常退出 → DELETE /session + 清状态回列表态 + 重新扫描主目录', async () => {
    const game = setupActiveGame();
    // DELETE /session（200，无 body）→ POST /game/scan（200，返回新列表）
    fetchMock
      .mockResolvedValueOnce({ status: 200, json: async () => ({}), text: async () => '' } as Response)
      .mockResolvedValueOnce({
        status: 200,
        json: async () => ({
          rootDir: 'D:/emuera',
          rootDirExists: true,
          games: [{ name: 'g2', fullPath: 'D:/emuera/g2' }],
        }),
        text: async () => '',
      } as Response);

    await game.exitGameHttp();

    // 1. DELETE /session
    expect(fetchMock).toHaveBeenNthCalledWith(
      1,
      'http://localhost:5173/session',
      expect.objectContaining({ method: 'DELETE' }),
    );
    // 2. 重新扫描主目录（scanRootDir 为空 → 回退 mainGameDir）
    expect(fetchMock).toHaveBeenNthCalledWith(
      2,
      'http://localhost:5173/game/scan',
      expect.objectContaining({
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ rootDir: 'D:/emuera' }),
      }),
    );
    // 3. 清状态回列表态
    expect(game.gameDir).toBeNull();
    expect(game.serverState).toBe('Idle');
    expect(game.exitStatus).toBe('idle');
    // 4. 列表已用新扫描数据填充
    expect(game.scannedGames).toEqual([{ name: 'g2', fullPath: 'D:/emuera/g2' }]);
    expect(game.scanRootDir).toBe('D:/emuera');
  });

  it('二次进入保护——exitStatus 已是 exiting → 直接返回，不调 fetch、不清状态', async () => {
    const game = setupActiveGame();
    expect(game.beginExitGame()).toBe(true); // 置 exitStatus='exiting'

    await game.exitGameHttp();

    expect(fetchMock).not.toHaveBeenCalled();
    expect(game.exitStatus).toBe('exiting');
    expect(game.gameDir).toBe('D:/emuera/g1'); // 未清
    expect(game.serverState).toBe('WaitInput'); // 未清
  });

  it('DELETE 失败不阻断——仍清状态回列表态 + 重新扫描，不抛异常', async () => {
    const game = setupActiveGame();
    // DELETE /session 网络失败 → POST /game/scan 正常返回
    fetchMock
      .mockRejectedValueOnce(new Error('Network error'))
      .mockResolvedValueOnce({
        status: 200,
        json: async () => ({ rootDir: 'D:/emuera', rootDirExists: true, games: [] }),
        text: async () => '',
      } as Response);

    await expect(game.exitGameHttp()).resolves.toBeUndefined();

    expect(game.gameDir).toBeNull();
    expect(game.serverState).toBe('Idle');
    expect(game.exitStatus).toBe('idle');
    // 重新扫描仍执行（第二次 fetch 是 /game/scan）
    expect(fetchMock).toHaveBeenNthCalledWith(
      2,
      'http://localhost:5173/game/scan',
      expect.objectContaining({ method: 'POST' }),
    );
  });
});
