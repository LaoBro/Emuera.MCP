import { useGameStore } from '../stores/game';
import { useConnectionStore } from '../stores/connection';
import {
  isMauiEnvironment,
  registerTurnHandler,
  registerMessageHandler,
  scanGames as scanGamesBridge,
} from '../lib/mauiBridge';

/**
 * T-025 D9 rev：App 挂载初始化逻辑——从 App.vue onMounted 提取以便单测。
 *
 * 流程（HTTP 模式）：
 * 1. GET /state（经 fetchAndApplyStateLayout）→ 拿 server 当前 state + gameDir + 窗口布局元信息
 * 2. server 未启动（state==null）→ 不自动连，让用户用 ConnectionPanel 手动连
 * 3. 空闲态（gameDir==null 或 state=="Idle"）→ 展示选择器并预填 localStorage 上次目录，
 *    **不自动 loadGame**——推翻 issue 05「异则 loadGame」的自动加载语义。
 *    选择器预填靠 game.gameDir（已从 localStorage 初始化），此处只需 return 不 connect。
 * 4. 有活跃 session（游戏运行中/已结束）→ conn.connect() 重连，不放弃当前局
 *
 * MAUI 模式分支（issue 07 / spec ID7 + issue 09 文件选择器 + 用户反馈修复）：
 * - `window.location.protocol` 判断为 MAUI（`ms-appx-web:` / `file:` / `https:app.local`）时：
 *   1. 注册 `window.__emueraOnTurn`——C# PostTurn 调此函数，参数为 turn 对象，JSON.stringify 后调 game.applyTurn
 *   2. 注册 `window.__emueraOnMessage`——C# PostMessage 调此函数，按 type 分发非 turn 事件
 *      （game-library spec ID9：`folderPicked` → `setMainGameDir` + `scanGames` 更改主目录并重扫；
 *      旧 issue 09 的「folderPicked → loadGameFromPath」语义已废弃）
 *   3. 标记 conn.status='connected'——让 sendInput / UI 组件认为已连接（MAUI 无 WS 但语义等价）
 *   4. **不**发 `sendReady()`——用户反馈：启动时不应自动加载游戏（即便内置 test_game 也不行）。
 *      首次启动仅注册回调 + 设 connected 状态，等用户在列表页点选游戏后通过 `loadGameFromPath`
 *      （在 `MauiGameList.vue` 内）触发 C# `OnReloadGame` → `RecreateHost` + `Start`
 *      （Start 内部设 `_readyReceived=true` 跳过 ready 检查）。
 *      MainPage 构造时创建的占位 BridgeHost 永远等不到 ready 信号，不会启动游戏循环。
 *   5. return——不走 HTTP/WS 路径
 *
 * 提取原因：onMounted 回调无法直接单测，提取为纯函数后可在 Vitest 中 mock fetch +
 * connection store 验证空闲态/活跃态/未启动/MAUI 四条分支。
 */
export async function initAppState(): Promise<void> {
  const game = useGameStore();
  const conn = useConnectionStore();

  // Issue 07 / spec ID7：MAUI 环境分支——不走 HTTP/WS，用 JS interop 桥接
  if (isMauiEnvironment()) {
    console.log('[useAppInit] MAUI environment detected, initializing bridge');
    // 1. 注册 C# → JS turn 回调——C# PostTurn 调 window.__emueraOnTurn(turnJson)，
    //    turnJson 是 JS 字面量（JSON ⊂ JS），Vue 端 JSON.stringify 还原为字符串后复用 game.applyTurn
    registerTurnHandler((rawJson) => game.applyTurn(rawJson));
    // 2. issue 09 / game-library spec ID9：注册 C# → JS 非 turn 消息回调——C# PostMessage 调 window.__emueraOnMessage(msg)，
    //    按 type 分发：folderPicked → setMainGameDir + scanGames（更改主目录 + 重扫，详见 handleMauiMessage）
    registerMessageHandler((msg) => handleMauiMessage(msg, game));
    // 3. 标记已连接——MAUI 无 WS 但 sendInput / UI 组件按 status='connected' 判定可用
    conn.status = 'connected';
    // 4. 用户反馈修复：不发 sendReady()——首次启动不自动加载游戏，等用户主动选目录。
    //    占位 BridgeHost 永远等不到 ready 信号，游戏循环不启动。
    //    用户选目录后 loadGameFromPath → C# OnReloadGame → Start（跳过 ready 检查）
    // ADR-0019：SAF 替代了 MANAGE_EXTERNAL_STORAGE，Android 不再需要检查存储权限。
    // 直接扫描主目录，与 Windows 行为一致。
    game.scanStatus = 'scanning';
    scanGamesBridge(game.mainGameDir);
    // 6. 不走 HTTP/WS 路径
    return;
  }
  // 防御性 window 检查——与 isMauiEnvironment() 同模式，让 node 测试环境（vitest environment='node'）下
  // 不抛 ReferenceError。生产浏览器环境 window 总存在，无行为变化。
  if (typeof window !== 'undefined') {
    console.log('[useAppInit] HTTP environment, protocol=', window.location.protocol, 'hostname=', window.location.hostname);
  }

  const httpBase = conn.deriveHttpBase(conn.serverUrl);

  // fetchAndApplyStateLayout 内部已 catch 网络错误——state==null 表示无法连接 server
  const { gameDir: serverGameDir, state: serverState } =
    await game.fetchAndApplyStateLayout(httpBase);

  // 读取 emuera.config 值（MaxLog 等）。失败不阻塞——保持 fallback。
  await game.fetchConfig(httpBase);

  // server 未启动——不自动连
  if (serverState === null) return;

  // 空闲态 → 展示选择器 + 预填 localStorage，不自动 loadGame（D9 rev）
  if (serverGameDir === null || serverState === 'Idle') return;

  // 有活跃 session → 重连
  await conn.connect();
}

/**
 * issue 09 文件选择器 + game-library spec ID3 / ID9：处理 C# `PostMessage` 推来的非 turn 消息——
 * `registerMessageHandler` 注册的回调，按 `type` 字段分发。
 *
 * 消息契约（C# `BridgeHost.Handle*` 推送）：
 * - `{"type":"folderPicked","path":"..."}`——用户选中目录（Windows FolderPicker），
 *   game-library spec ID9 后语义改为「更改主目录」——保存为 mainGameDir + 重新 scanGames
 * - `{"type":"folderPicked","error":"..."}`——文件选择器失败，写 `mauiError` 让 UI 展示
 * - `{"type":"gamesScanned","games":[{name,fullPath}],"rootDir":...}`——scanGames 回复，写入 store
 * - `{"type":"directoriesListed","currentPath":...,"parentPath":...|"null","subDirectories":[...]}`——listDirectories 回复
 * - `{"type":"gameExited"}`——exitGame 处理完成，清状态 + 自动重新 scanGames
 *
 * 收到 `folderPicked` 带 path 时的处理（game-library spec ID9 修订）：
 * 1. `game.setMainGameDir(path)`——更新 mainGameDir ref + 持久化到 localStorage
 * 2. `game.scanStatus = 'scanning'`——让列表页展示 loading 占位
 * 3. `scanGamesBridge(path)`——投递 `{"type":"scanGames","rootDir":...}`，
 *    C# `HandleScanGames` 收到后更新 `_mainGameDir` + 写 Preferences + 扫描 + 回复 gamesScanned
 *
 * **为何不再调 `loadGameFromPath(path)`**：game-library spec 后 `pickFolder` 仅用于「更改主目录」，
 * 不再用于「选游戏目录加载」（后者改由点击列表项触发 `loadGameFromPath(fullPath)`）。
 * 旧 issue 09 的「folderPicked → loadGameFromPath」语义已废弃。
 *
 * 收到 `gamesScanned` 时的处理（game-library spec ID7）：
 * - `game.setScannedGames(games, rootDir)`——写入 store，列表页据此渲染
 * - **不**自动加载游戏——列表页等用户点击项再触发
 *
 * 收到 `directoriesListed` 时的处理（game-library spec ID8）：
 * - `game.setDirectoryList(result)`——写入 store，DirectoryBrowser.vue 弹窗据此渲染
 *
 * 收到 `gameExited` 时的处理（game-library spec ID10 / ID12）：
 * 1. `game.completeExitGame()`——清 gameDir + displayState + serverState + exitStatus
 * 2. `scanGamesBridge()`——自动重新扫描主目录，列表页填新数据
 *
 * C# 侧 reload 失败时（`GamePaths.Validate` 抛 `GamePathValidationException` 或
 * `EmueraRuntimeInitializer.Initialize` 抛异常）由 `MainPage.OnReloadGame` 调
 * `_host.ShowError(...)` 推 error turn——Vue 端 `applyTurn` 渲染 error 字段，无需此处处理。
 *
 * @param msg C# 推来的消息对象（已解析的 JS 对象）
 * @param game game store 实例
 */
function handleMauiMessage(msg: unknown, game: ReturnType<typeof useGameStore>): void {
  if (!msg || typeof msg !== 'object') return;
  const m = msg as Record<string, unknown>;
  const type = m.type;

  if (type === 'layout') {
    game.setGameLayout({
      windowWidth: typeof m.windowWidth === 'number' ? m.windowWidth : null,
      fontSize: typeof m.fontSize === 'number' ? m.fontSize : null,
      lineHeight: typeof m.lineHeight === 'number' ? m.lineHeight : null,
      gameColumns: typeof m.gameColumns === 'number' ? m.gameColumns : null,
      fontName: typeof m.fontName === 'string' ? m.fontName : null,
    });
    // layout 消息携带 state——同步 serverState，避免首帧到达前误显"请点击快速重开"
    if (typeof m.state === 'string' && m.state !== 'Idle') {
      game.applyServerState(m.state);
    }
    return;
  }

  if (type === 'config') {
    if (typeof m.maxLog === 'number') game.maxLog = m.maxLog;
    return;
  }

  // game-library spec ID3 / ID7：scanGames 回复——写入 store 让列表页渲染
  if (type === 'gamesScanned') {
    const rawGames = Array.isArray(m.games) ? m.games : [];
    const games = rawGames
      .filter((g): g is { name: string; fullPath: string } =>
        !!g &&
        typeof g === 'object' &&
        typeof (g as Record<string, unknown>).name === 'string' &&
        typeof (g as Record<string, unknown>).fullPath === 'string',
      )
      .map((g) => ({ name: g.name, fullPath: g.fullPath }));
    const rootDir = typeof m.rootDir === 'string' ? m.rootDir : null;
    // game-library spec ID13：主目录是否存在（C# HandleScanGames 通过 IGameDirAccessor 检查）
    const rootDirExists = typeof m.rootDirExists === 'boolean' ? m.rootDirExists : null;
    console.log(
      `[useAppInit] gamesScanned: ${games.length} games, rootDir=${rootDir}, rootDirExists=${rootDirExists}`,
    );
    game.setScannedGames(games, rootDir, rootDirExists);
    return;
  }

  // game-library spec ID3 / ID8：listDirectories 回复——写入 store 让 DirectoryBrowser 渲染
  if (type === 'directoriesListed') {
    const currentPath = typeof m.currentPath === 'string' ? m.currentPath : '';
    const parentPath = typeof m.parentPath === 'string' ? m.parentPath : null;
    const subDirectories = Array.isArray(m.subDirectories)
      ? m.subDirectories.filter((s): s is string => typeof s === 'string')
      : [];
    console.log(
      `[useAppInit] directoriesListed: currentPath=${currentPath}, subDirectories.length=${subDirectories.length}`,
    );
    game.setDirectoryList({ currentPath, parentPath, subDirectories });
    return;
  }

  // game-library spec ID3 / ID10 / ID12：exitGame 处理完成——清状态 + 自动重新扫描
  if (type === 'gameExited') {
    console.log('[useAppInit] gameExited: completing exit and rescanning');
    game.completeExitGame();
    // 自动重新扫描主目录——spec ID12 退出后回列表自动 rescan
    scanGamesBridge(game.mainGameDir);
    return;
  }

  // ADR-0019：Android SAF 目录选择结果
  if (type === 'safDirectoryPicked') {
    if (m.cancelled === true) {
      console.log('[useAppInit] safDirectoryPicked: user cancelled');
      return;
    }
    if (typeof m.error === 'string') {
      console.error('[useAppInit] safDirectoryPicked error:', m.error);
      game.mauiError = `选择目录失败：${m.error}`;
      return;
    }
    const path = m.path;
    if (typeof path !== 'string' || !path.trim()) {
      console.warn('[useAppInit] safDirectoryPicked: missing path');
      return;
    }
    console.log('[useAppInit] safDirectoryPicked:', path);
    game.setMainGameDir(path);
    game.scanStatus = 'scanning';
    scanGamesBridge(path);
    return;
  }

  // game-library spec ID10：Android 物理返回键——触发退出确认对话框
  if (type === 'backButtonPressed') {
    console.log('[useAppInit] backButtonPressed: incrementing trigger for exit confirm');
    // 自增计数器——App.vue watch 此值后弹出退出确认对话框
    game.backButtonPressedTick++;
    return;
  }

  // 无限循环检测确认：C# ScriptProc.checkInfiniteLoop 触发后推送，App.vue 弹模态框询问玩家。
  // 玩家选择后经 respondInfiniteLoop 投递 infiniteLoopResponse 让 C# 阻塞读取。
  if (type === 'infiniteLoopPrompt') {
    const message = typeof m.message === 'string' ? m.message : '';
    console.log('[useAppInit] infiniteLoopPrompt received');
    game.setInfiniteLoopPrompt(message);
    return;
  }

  if (type !== 'folderPicked') {
    console.warn('[useAppInit] handleMauiMessage: unknown message type:', type);
    return;
  }
  // folderPicked 带 error → 文件选择器失败
  if (typeof m.error === 'string') {
    console.error('[useAppInit] folderPicked error:', m.error);
    game.mauiError = `选择目录失败：${m.error}`;
    return;
  }
  // folderPicked 带 path → game-library spec ID9：更改主目录 + 重新扫描
  // （旧 issue 09 的「folderPicked → loadGameFromPath」语义已废弃——pickFolder 现仅用于更改主目录）
  const path = m.path;
  if (typeof path !== 'string' || !path.trim()) {
    console.warn('[useAppInit] folderPicked: missing or non-string path');
    return;
  }
  console.log('[useAppInit] folderPicked, updating mainGameDir + rescanning:', path);
  // 1. 更新 mainGameDir + 持久化到 localStorage
  game.setMainGameDir(path);
  // 2. 标记扫描中——列表页展示 loading 占位
  game.scanStatus = 'scanning';
  // 3. 投递 scanGames 让 C# 扫描新主目录——C# HandleScanGames 会同步更新 _mainGameDir + 写 Preferences
  scanGamesBridge(path);
}
