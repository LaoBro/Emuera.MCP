import { defineStore } from 'pinia';
import { ref } from 'vue';
import { useGameStore } from './game';

export type ConnectionStatus = 'disconnected' | 'connecting' | 'connected' | 'reconnecting';

/**
 * useConnectionStore — WebSocket 连接生命周期。
 *
 * 协议契约（与 C# KestrelGameServer.HandleWebSocketAsync 对齐）：
 * - 服务端发送：裸 JSON 文本帧（每个帧是一个 TurnRecord v5 JSON）
 * - 客户端发送：`{"type":"input","value":"..."}` 文本帧
 *   （C# HandleWsInput 直接入队，AgentJsonlProtocol 校验 type=="input"）
 *
 * 重连策略：v1 不做自动重连。`'reconnecting'` 状态在 v1 仅作为"曾异常断开"标记
 * （与用户主动 `disconnect()` 的 `'disconnected'` 区分），issue 06 起接入指数退避重连。
 *
 * Issue 03 起：协议版本号 protocolVersion 移到 game store 暴露（避免重复 parse），
 * 由 game.lastTurn?.protocolVersion 派生。本 store 只管连接生命周期。
 */
export const useConnectionStore = defineStore('connection', () => {
  /** 当前连接状态。 */
  const status = ref<ConnectionStatus>('disconnected');
  /** 当前 WebSocket 实例（仅供本 store 内部使用，非响应式）。 */
  let ws: WebSocket | null = null;
  /** 上次连接的 server URL，重连时复用。 */
  const serverUrl = ref<string>('ws://localhost:5173/ws');
  /** WS 关闭时收到的 reason/错误信息（用于 UI 诊断）。 */
  const closeReason = ref<string | null>(null);

  /**
   * 连接到指定 WS URL。
   *
   * 默认 `ws://localhost:5173/ws` 走 Vite 代理到 C# 8080，避免浏览器 dev 模式跨域。
   * 生产构建后用户访问 `localhost:8080`，应传 `ws://localhost:8080/ws`。
   */
  function connect(url: string = serverUrl.value): void {
    if (ws && (status.value === 'connected' || status.value === 'connecting')) {
      return;
    }
    serverUrl.value = url;
    closeReason.value = null;
    status.value = 'connecting';

    const socket = new WebSocket(url);
    ws = socket;

    socket.onopen = () => {
      status.value = 'connected';
    };

    socket.onmessage = (event) => {
      // C# SendLoopAsync 把 turn 字符串作为单个 Text 帧下发，data 即原始 JSON。
      // 直接交给 game store——它内部会 parseTurnRecord + applyDiff 更新显示状态。
      const data = typeof event.data === 'string' ? event.data : '';
      if (!data) return;
      const game = useGameStore();
      game.applyTurn(data);
    };

    socket.onerror = () => {
      // 浏览器安全策略：error 事件不暴露详情。后续 onclose 会给出状态码/原因。
      closeReason.value = closeReason.value ?? 'WebSocket error (see browser console)';
    };

    socket.onclose = (event) => {
      const wasConnected = status.value === 'connected';
      status.value = 'disconnected';
      ws = null;
      closeReason.value = event.reason || `code=${event.code}`;
      // v1 不做自动重连。异常断开（非 1000 关闭码）时标记为 'reconnecting' 以与
      // 主动 disconnect 的 'disconnected' 区分，给 UI 显示"已断开（异常）"诊断信号。
      // issue 06 接入真正的指数退避重连后会在此触发 connect()。
      if (wasConnected && event.code !== 1000) {
        status.value = 'reconnecting';
      }
    };
  }

  /** 主动断开。 */
  function disconnect(): void {
    if (ws) {
      ws.close(1000, 'client disconnect');
      ws = null;
    }
    status.value = 'disconnected';
  }

  /**
   * 通过 WS 发送输入帧。
   *
   * 帧格式与 C# HTTP /input 端点对称：`{"type":"input","value":"..."}`，
   * 由 HandleWsInput 直接 EnqueueInput 给 HttpSessionIO，再被 AgentJsonlProtocol 消费。
   */
  function sendInput(value: string): void {
    if (!ws || status.value !== 'connected') return;
    const payload = JSON.stringify({ type: 'input', value });
    ws.send(payload);
  }

  return { status, serverUrl, closeReason, connect, disconnect, sendInput };
});
