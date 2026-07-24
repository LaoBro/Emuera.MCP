/**
 * 调试历史：WS 帧原始 JSON 保留上限。
 *
 * 游戏进行中每个回合（包括状态切换）都会产生一条 JSON 帧，
 * 存入 game store 的 turnHistory 供 DebugView 调试面板回溯。
 * 超出此上限时丢弃最旧帧（FIFO），避免无限增长占用浏览器内存。
 */
export const TURN_HISTORY_MAX = 500;
