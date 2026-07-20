import { useGameStore } from '../stores/game';
import { useConnectionStore } from '../stores/connection';

/**
 * T-025 D9 rev：App 挂载初始化逻辑——从 App.vue onMounted 提取以便单测。
 *
 * 流程：
 * 1. GET /state（经 fetchAndApplyStateLayout）→ 拿 server 当前 state + gameDir + 窗口布局元信息
 * 2. server 未启动（state==null）→ 不自动连，让用户用 ConnectionPanel 手动连
 * 3. 空闲态（gameDir==null 或 state=="Idle"）→ 展示选择器并预填 localStorage 上次目录，
 *    **不自动 loadGame**——推翻 issue 05「异则 loadGame」的自动加载语义。
 *    选择器预填靠 game.gameDir（已从 localStorage 初始化），此处只需 return 不 connect。
 * 4. 有活跃 session（游戏运行中/已结束）→ conn.connect() 重连，不放弃当前局
 *
 * 提取原因：onMounted 回调无法直接单测，提取为纯函数后可在 Vitest 中 mock fetch +
 * connection store 验证空闲态/活跃态/未启动三条分支。
 */
export async function initAppState(): Promise<void> {
  const game = useGameStore();
  const conn = useConnectionStore();
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
