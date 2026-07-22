import { useGameStore } from '../stores/game';
import { useConnectionStore } from '../stores/connection';
import { isMauiEnvironment, registerTurnHandler, sendReady } from '../lib/mauiBridge';

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
 * MAUI 模式分支（issue 07 / spec ID7）：
 * - `window.location.protocol` 判断为 MAUI（`ms-appx-web:` / `file:`）时：
 *   1. 注册 `window.__emueraOnTurn`——C# PostTurn 调此函数，参数为 turn 对象，JSON.stringify 后调 game.applyTurn
 *   2. 标记 conn.status='connected'——让 sendInput / UI 组件认为已连接（MAUI 无 WS 但语义等价）
 *   3. sendReady()——向 C# 投递 `{"type":"ready"}`，C# 侧 BridgeHost.OnInputFromJs 识别后（T08）启动游戏循环
 *   4. return——不走 HTTP/WS 路径
 *
 * 提取原因：onMounted 回调无法直接单测，提取为纯函数后可在 Vitest 中 mock fetch +
 * connection store 验证空闲态/活跃态/未启动/MAUI 四条分支。
 */
export async function initAppState(): Promise<void> {
  const game = useGameStore();
  const conn = useConnectionStore();

  // Issue 07 / spec ID7：MAUI 环境分支——不走 HTTP/WS，用 JS interop 桥接
  if (isMauiEnvironment()) {
    // 1. 注册 C# → JS turn 回调——C# PostTurn 调 window.__emueraOnTurn(turnJson)，
    //    turnJson 是 JS 字面量（JSON ⊂ JS），Vue 端 JSON.stringify 还原为字符串后复用 game.applyTurn
    registerTurnHandler((rawJson) => game.applyTurn(rawJson));
    // 2. 标记已连接——MAUI 无 WS 但 sendInput / UI 组件按 status='connected' 判定可用
    conn.status = 'connected';
    // 3. 发送 ready 信号——C# 侧收到后 T08 启动游戏循环，T07 仅写日志确认
    sendReady();
    // 4. 不走 HTTP/WS 路径
    return;
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
