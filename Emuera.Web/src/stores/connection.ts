import { defineStore } from 'pinia';
import { ref } from 'vue';
import { useGameStore } from './game';
import type { DisplaySnapshot } from '../types/protocol';

export type ConnectionStatus = 'disconnected' | 'connecting' | 'connected' | 'reconnecting';

/**
 * useConnectionStore — WebSocket 连接生命周期。
 *
 * 协议契约（与 C# KestrelGameServer 对齐）：
 * - **必须先 POST /session 创建会话**，server 才会接受 WS 升级；
 *   否则 server 接受 WS 升级后立即下发关闭码 4004 + reason "No active session"
 *   （KestrelGameServer.cs:271）
 * - **WS 升级前必须先 GET /snapshot 拿当前全屏状态**——C# server 不在首帧 diff
 *   重放历史 PRINT 输出，故前端必须主动拉快照渲染初始画面
 * - 服务端发送：裸 JSON 文本帧（每个帧是一个 TurnRecord v5 JSON）
 * - 客户端发送：`{"type":"input","value":"..."}` 文本帧
 *   （C# HandleWsInput 直接入队，AgentJsonlProtocol 校验 type=="input"）
 *
 * 重连策略：v1 不做自动重连。`'reconnecting'` 状态在 v1 仅作为"曾异常断开"标记
 * （与用户主动 `disconnect()` 的 `'disconnected'` 区分），issue 06 起接入指数退避重连。
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
   * 派生 HTTP base URL——从 serverUrl（ws://host:port/ws）推导同源 http://host:port。
   *
   * Dev 模式：serverUrl='ws://localhost:5173/ws' → httpBase='http://localhost:5173'
   *   POST /session / GET /snapshot 经 Vite 代理到 C# 8080（见 vite.config.ts proxy）
   * 生产模式：serverUrl='ws://localhost:8080/ws' → httpBase='http://localhost:8080'
   *   请求直达 C# Kestrel。
   */
  function deriveHttpBase(url: string): string {
    // ws:// → http://, wss:// → https://；并去掉末尾的 /ws 路径
    const httpScheme = url.startsWith('wss://') ? 'https://' : 'http://';
    const rest = url.replace(/^wss?:\/\//, '');
    const hostPort = rest.split('/')[0]; // 取 host:port，丢弃 /ws 等路径
    return `${httpScheme}${hostPort}`;
  }

  /**
   * 调 `POST /session` 创建会话——WS 升级前的前置步骤。
   *
   * 返回值约定：
   * - 成功（201）：会话已创建，可继续后续步骤
   * - 已有活跃会话（409）：视为成功——上个会话还活着，可直接连入
   *   （KestrelGameServer.cs:104 的 conflict 分支）
   * - 其他失败（网络错误 / 4xx / 5xx）：抛 Error，调用方决定如何展示
   */
  async function ensureSession(httpBase: string): Promise<void> {
    const resp = await fetch(`${httpBase}/session`, { method: 'POST' });

    if (resp.status === 201 || resp.status === 409) {
      return;
    }

    // 非 201/409——读 body 做诊断。Response body 是一次性 stream，故先 text() 一次，
    // 再尝试 JSON.parse 提取 error 字段——失败则用原文作 detail。
    let detail = '';
    try {
      const text = await resp.text();
      try {
        const body = JSON.parse(text);
        detail = typeof body?.error === 'string' ? body.error : text;
      } catch {
        detail = text;
      }
    } catch {
      // body 读取失败（极罕见，如连接被中断）——detail 留空
    }
    throw new Error(`POST /session 失败：HTTP ${resp.status}${detail ? ` (${detail})` : ''}`);
  }

  /** GET /snapshot 503 重试上限与间隔——C# POST /session 后 _displayState 极短窗口为 null。 */
  const SNAPSHOT_503_RETRIES = 5;
  const SNAPSHOT_503_INTERVAL_MS = 100;

  /**
   * 调 `GET /snapshot` 拿当前全量显示状态——WS 升级前的前置步骤。
   *
   * C# `Session.GetDisplaySnapshot()`（Session.cs:34）：
   * - session 未初始化（_displayState==null，POST /session 后极短窗口）→ 503
   * - session 运行中 / 已结束 → 200，body = DisplaySnapshot JSON
   * - 无 session → 404
   *
   * 503 重试：POST /session 把游戏循环排到独立 Task，DisplayState 在 GameLoopAsync
   * 内构造——客户端立即 GET /snapshot 时可能撞上 null 窗口。重试 SNAPSHOT_503_RETRIES
   * 次，每次间隔 SNAPSHOT_503_INTERVAL_MS——通常 1-2 次内即可成功。
   *
   * 返回：解析后的 DisplaySnapshot（不直接返回 JSON 字符串——此函数是协议边界，
   * 解析失败立即抛错，避免下游组件处理半结构化数据）。
   */
  async function fetchSnapshot(httpBase: string): Promise<DisplaySnapshot> {
    let lastError: Error | null = null;

    for (let attempt = 0; attempt < SNAPSHOT_503_RETRIES; attempt++) {
      const resp = await fetch(`${httpBase}/snapshot`);

      if (resp.status === 200) {
        const text = await resp.text();
        try {
          const obj = JSON.parse(text);
          // 用 parseTurnRecord 的字段校验逻辑做最小形状校验——DisplaySnapshot 与
          // TurnRecord 顶层结构部分重叠（state/inputType/needValue/protocolVersion），
          // 但 DisplaySnapshot 必有 lines 字段。这里独立校验 lines。
          if (!Array.isArray(obj?.lines)) {
            throw new Error('snapshot.lines 缺失或非数组');
          }
          if (typeof obj.state !== 'string') {
            throw new Error('snapshot.state 缺失或非 string');
          }
          if (typeof obj.needValue !== 'boolean') {
            throw new Error('snapshot.needValue 缺失或非 boolean');
          }
          return obj as DisplaySnapshot;
        } catch (e) {
          throw new Error(`GET /snapshot 解析失败：${e instanceof Error ? e.message : String(e)}`);
        }
      }

      if (resp.status === 503) {
        // session 未初始化——稍等重试
        lastError = new Error('session 尚未初始化');
        await delay(SNAPSHOT_503_INTERVAL_MS);
        continue;
      }

      // 其他状态码（404 / 5xx 等）——读 body 诊断，立即抛错不重试
      let detail = '';
      try {
        const text = await resp.text();
        try {
          const body = JSON.parse(text);
          detail = typeof body?.error === 'string' ? body.error : text;
        } catch {
          detail = text;
        }
      } catch {
        // body 读取失败——detail 留空
      }
      throw new Error(`GET /snapshot 失败：HTTP ${resp.status}${detail ? ` (${detail})` : ''}`);
    }

    throw new Error(
      `GET /snapshot 重试 ${SNAPSHOT_503_RETRIES} 次仍 503${lastError ? `：${lastError.message}` : ''}`,
    );
  }

  /** Promise-based setTimeout——503 重试间隔。*/
  function delay(ms: number): Promise<void> {
    return new Promise((resolve) => setTimeout(resolve, ms));
  }

  /**
   * 连接到指定 WS URL——异步方法。
   *
   * 流程（issue 03 起，含 snapshot 前置）：
   * 1. 标记 status='connecting'，清 closeReason
   * 2. **POST /session 创建会话**（KestrelGameServer 协议契约）
   *    - 失败：status='disconnected' + closeReason，return
   * 3. **GET /snapshot 拿当前全屏状态**（C# 不在首帧 diff 重放历史）
   *    - 失败：status='disconnected' + closeReason，return
   *    - 成功：调 game.setSnapshot 重建 displayState——终端立即渲染初始画面
   * 4. 创建 WebSocket，绑定 onopen/onmessage/onerror/onclose
   *    - onopen 后 status='connected'，此时 displayState 已就绪，WS 增量 diff 可叠加
   *
   * 默认 `ws://localhost:5173/ws` 走 Vite 代理到 C# 8080。
   * 生产构建后用户访问 `localhost:8080`，应传 `ws://localhost:8080/ws`。
   */
  async function connect(url: string = serverUrl.value): Promise<void> {
    if (ws && (status.value === 'connected' || status.value === 'connecting')) {
      return;
    }
    serverUrl.value = url;
    closeReason.value = null;
    status.value = 'connecting';

    const httpBase = deriveHttpBase(url);
    const game = useGameStore();

    // 步骤 2：创建会话——失败立即放弃
    try {
      await ensureSession(httpBase);
    } catch (e) {
      status.value = 'disconnected';
      closeReason.value = e instanceof Error ? e.message : String(e);
      return;
    }

    // 用户在 ensureSession 期间手动 disconnect() 的并发处理
    if (status.value !== 'connecting') return;

    // 步骤 3：拉全量快照——失败立即放弃
    try {
      const snapshot = await fetchSnapshot(httpBase);
      game.setSnapshot(snapshot);
    } catch (e) {
      status.value = 'disconnected';
      closeReason.value = e instanceof Error ? e.message : String(e);
      return;
    }

    if (status.value !== 'connecting') return;

    // 步骤 4：会话 + 快照就绪——升级 WebSocket
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
      // v1 不做自动重连。异常断开（非 1000 关闭码）时标记为 'reconnecting'。
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

  return {
    status,
    serverUrl,
    closeReason,
    connect,
    disconnect,
    sendInput,
    // 测试 seam：导出内部纯函数便于单测
    deriveHttpBase,
    ensureSession,
    fetchSnapshot,
  };
});
