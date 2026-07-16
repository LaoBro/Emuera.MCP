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
 * 重连策略：v1 仅做单次尝试 + 状态切换，issue 06 会引入指数退避自动重连。
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
  /** 服务端通过 TurnRecord.protocolVersion 报告的协议版本，由 game store 推断后回填。 */
  const protocolVersion = ref<number | null>(null);

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
      const data = typeof event.data === 'string' ? event.data : '';
      if (!data) return;
      const game = useGameStore();
      game.applyTurn(data);
      // 协议版本探测：合法帧且包含 protocolVersion 字段时回填（v1 调试用）。
      try {
        const obj = JSON.parse(data) as { protocolVersion?: unknown };
        if (typeof obj.protocolVersion === 'number') {
          protocolVersion.value = obj.protocolVersion;
        }
      } catch {
        // 非 JSON 帧忽略——issue 01 仅展示原文，不强制结构化。
      }
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
      // v1 不做自动重连；issue 06 引入指数退避。这里仅在曾经连上过的情况下标记 reconnecting。
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

  return { status, serverUrl, closeReason, protocolVersion, connect, disconnect, sendInput };
});
