import { useGameStore } from '../stores/game';
import { useConnectionStore } from '../stores/connection';
import {
  isMauiEnvironment,
  registerTurnHandler,
  registerMessageHandler,
  loadGameFromPath,
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
 *      （issue 09：`folderPicked` → 调 `loadGameFromPath(path)` 触发 hot-swap reload）
 *   3. 标记 conn.status='connected'——让 sendInput / UI 组件认为已连接（MAUI 无 WS 但语义等价）
 *   4. **不**发 `sendReady()`——用户反馈：启动时不应自动加载游戏（即便内置 test_game 也不行）。
 *      首次启动仅注册回调 + 设 connected 状态，等用户主动点选目录后通过 `loadGameFromPath`
 *      触发 C# `OnReloadGame` → `RecreateHost` + `Start`（Start 内部设 `_readyReceived=true` 跳过 ready 检查）。
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
    // 2. issue 09：注册 C# → JS 非 turn 消息回调——C# PostMessage 调 window.__emueraOnMessage(msg)，
    //    按 type 分发：folderPicked → loadGameFromPath 触发 hot-swap reload
    registerMessageHandler((msg) => handleMauiMessage(msg, game));
    // 3. 标记已连接——MAUI 无 WS 但 sendInput / UI 组件按 status='connected' 判定可用
    conn.status = 'connected';
    // 4. 用户反馈修复：不发 sendReady()——首次启动不自动加载游戏，等用户主动选目录。
    //    占位 BridgeHost 永远等不到 ready 信号，游戏循环不启动。
    //    用户选目录后 loadGameFromPath → C# OnReloadGame → Start（跳过 ready 检查）
    // 5. 不走 HTTP/WS 路径
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

  // server 未启动——不自动连
  if (serverState === null) return;

  // 空闲态 → 展示选择器 + 预填 localStorage，不自动 loadGame（D9 rev）
  if (serverGameDir === null || serverState === 'Idle') return;

  // 有活跃 session → 重连
  await conn.connect();
}

/**
 * issue 09 文件选择器：处理 C# `PostMessage` 推来的非 turn 消息——
 * `registerMessageHandler` 注册的回调，按 `type` 字段分发。
 *
 * 消息契约（C# `BridgeHost.HandlePickFolder` 推送）：
 * - `{"type":"folderPicked","path":"..."}`——用户选中目录，调 `loadGameFromPath(path)` 触发 hot-swap reload
 * - `{"type":"folderPicked","error":"..."}`——文件选择器失败，写 `mauiError` 让 UI 展示
 *
 * 收到 `folderPicked` 带 path 时的处理：
 * 1. `game.setGameDir(path)`——更新 gameDir ref + 持久化到 localStorage
 * 2. `game.reset()`——清空旧游戏显示状态（displayState / turnHistory / TINPUT timer）
 * 3. `loadGameFromPath(path)`——投递 `{"type":"loadGame","path":...}` 让 C# hot-swap reload
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
  // folderPicked 带 path → 触发 hot-swap reload
  const path = m.path;
  if (typeof path !== 'string' || !path.trim()) {
    console.warn('[useAppInit] folderPicked: missing or non-string path');
    return;
  }
  console.log('[useAppInit] folderPicked, triggering reload:', path);
  // 1. 更新 gameDir + 持久化
  game.setGameDir(path);
  // 2. 清空旧游戏显示状态——新游戏首帧到达前不残留旧画面
  game.reset();
  // 3. 通知 C# hot-swap reload——后台重新初始化运行时 + 重建 BridgeHost + Start
  loadGameFromPath(path);
}
