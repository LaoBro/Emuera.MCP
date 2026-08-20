import type { ConnectionStatus } from '../stores/connection';

/** Issue 05：/load-game 进行中标志，与 game.ts 中 reloadStatus 同型。 */
export type ReloadStatus = 'idle' | 'loading';

/**
 * Issue 11 D5：派生的"显示用连接状态"——reloadStatus='loading' 时返 'loading'，
 * 否则返原 connectionStatus。
 *
 * 背景：loadGame 切换游戏目录时序列为 disconnect 旧 WS → POST /load-game → connect 新 WS，
 * 期间 connectionStatus 短暂为 'disconnected'。若 UI 直接显示 connectionStatus，
 * 用户会看到"已断开"闪烁，破坏切换游戏时的"加载中"体验。
 *
 * 派生 'loading' 后，UI 据此显示"加载中…"文案 + 黄色（进行中）状态点，
 * 屏蔽 'disconnected' 闪断。
 *
 * 纯函数便于单测；TerminalView.vue / ConnectionPanel.vue 共享此契约。
 */
export function deriveDisplayStatus(
  reloadStatus: ReloadStatus,
  connectionStatus: ConnectionStatus,
): 'loading' | ConnectionStatus {
  return reloadStatus === 'loading' ? 'loading' : connectionStatus;
}
