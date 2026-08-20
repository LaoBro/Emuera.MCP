import { describe, it, expect, beforeEach, vi, afterEach } from 'vitest';
import { setActivePinia, createPinia } from 'pinia';
import { useGameStore } from '../game';

/**
 * Web/HTTP 模式游戏选择（/game/scan + /game/dirs）单测——server 代扫 + 目录浏览。
 *
 * 测试矩阵：
 * - scanGamesHttp 成功：POST /game/scan → scannedGames / scanRootDir / scanRootDirExists 更新
 * - scanGamesHttp rootDir 不存在：200 rootDirExists=false + 空列表（空状态语义）
 * - scanGamesHttp 非 200 / fetch throw：返回 false（组件展示连接错误）
 * - scanGamesHttp 空目录：返回 false + 不调 fetch
 * - browseDirectoryHttp 成功：返回 currentPath / parentPath / dirs
 * - browseDirectoryHttp 失败：返回 null
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

describe('useGameStore Web 游戏选择（scanGamesHttp / browseDirectoryHttp）', () => {
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

  function mockScanResponse(status: number, body: unknown): void {
    fetchMock.mockResolvedValueOnce({
      status,
      json: async () => body,
      text: async () => JSON.stringify(body),
    } as Response);
  }

  it('scanGamesHttp 成功 → 写入游戏列表 + 返回 true', async () => {
    const game = useGameStore();
    mockScanResponse(200, {
      rootDir: 'D:/emuera',
      rootDirExists: true,
      games: [
        { name: 'game1', fullPath: 'D:/emuera/game1' },
        { name: 'game2', fullPath: 'D:/emuera/game2' },
      ],
    });

    const ok = await game.scanGamesHttp('D:/emuera');

    expect(ok).toBe(true);
    expect(game.scannedGames).toEqual([
      { name: 'game1', fullPath: 'D:/emuera/game1' },
      { name: 'game2', fullPath: 'D:/emuera/game2' },
    ]);
    expect(game.scanRootDir).toBe('D:/emuera');
    expect(game.scanRootDirExists).toBe(true);
    expect(game.scanStatus).toBe('idle');
    // 请求参数校验
    expect(fetchMock).toHaveBeenCalledWith(
      'http://localhost:5173/game/scan',
      expect.objectContaining({
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ rootDir: 'D:/emuera' }),
      }),
    );
  });

  it('scanGamesHttp rootDir 不存在 → rootDirExists=false + 空列表 + 返回 true', async () => {
    const game = useGameStore();
    mockScanResponse(200, { rootDir: 'D:/missing', rootDirExists: false, games: [] });

    const ok = await game.scanGamesHttp('D:/missing');

    expect(ok).toBe(true);
    expect(game.scannedGames).toEqual([]);
    expect(game.scanRootDirExists).toBe(false);
    expect(game.scanRootDir).toBe('D:/missing');
  });

  it('scanGamesHttp 非 200 → 空列表 + 返回 false', async () => {
    const game = useGameStore();
    mockScanResponse(500, { error: { code: 'INTERNAL', message: 'boom' } });

    const ok = await game.scanGamesHttp('D:/emuera');

    expect(ok).toBe(false);
    expect(game.scannedGames).toEqual([]);
    expect(game.scanStatus).toBe('idle');
  });

  it('scanGamesHttp fetch throw → 返回 false + 不抛出', async () => {
    const game = useGameStore();
    fetchMock.mockRejectedValueOnce(new Error('Network error'));

    const ok = await game.scanGamesHttp('D:/emuera');

    expect(ok).toBe(false);
    expect(game.scanStatus).toBe('idle');
  });

  it('scanGamesHttp 空目录 → 返回 false + 不调 fetch', async () => {
    const game = useGameStore();
    const ok = await game.scanGamesHttp('   ');

    expect(ok).toBe(false);
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it('scanGamesHttp 返回的 games 含非法条目时过滤', async () => {
    const game = useGameStore();
    mockScanResponse(200, {
      rootDir: 'D:/emuera',
      rootDirExists: true,
      games: [
        { name: 'good', fullPath: 'D:/emuera/good' },
        { name: 'no-path' },
        { fullPath: 'D:/no-name' },
        'not-an-object',
      ],
    });

    await game.scanGamesHttp('D:/emuera');

    expect(game.scannedGames).toEqual([{ name: 'good', fullPath: 'D:/emuera/good' }]);
  });

  it('browseDirectoryHttp 成功 → 返回 currentPath / parentPath / dirs', async () => {
    const game = useGameStore();
    fetchMock.mockResolvedValueOnce({
      status: 200,
      json: async () => ({
        currentPath: 'D:/emuera',
        parentPath: 'D:',
        dirs: ['game1', 'game2'],
      }),
      text: async () => '',
    } as Response);

    const result = await game.browseDirectoryHttp('D:/emuera');

    expect(result).toEqual({ currentPath: 'D:/emuera', parentPath: 'D:', dirs: ['game1', 'game2'] });
    expect(fetchMock).toHaveBeenCalledWith(
      'http://localhost:5173/game/dirs',
      expect.objectContaining({
        method: 'POST',
        body: JSON.stringify({ dir: 'D:/emuera' }),
      }),
    );
  });

  it('browseDirectoryHttp 非 200 / 空路径 / fetch throw → 返回 null', async () => {
    const game = useGameStore();

    // 空路径
    expect(await game.browseDirectoryHttp('   ')).toBeNull();

    // 非 200
    fetchMock.mockResolvedValueOnce({ status: 404, json: async () => ({}), text: async () => '' } as Response);
    expect(await game.browseDirectoryHttp('D:/x')).toBeNull();

    // fetch throw
    fetchMock.mockRejectedValueOnce(new Error('Network error'));
    expect(await game.browseDirectoryHttp('D:/x')).toBeNull();
  });
});
