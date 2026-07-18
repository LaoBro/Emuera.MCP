<script setup lang="ts">
import { ref, computed } from 'vue';
import { useConnectionStore, type ConnectionStatus, MAX_RECONNECT_ATTEMPTS } from '../stores/connection';
import { useGameStore } from '../stores/game';

const conn = useConnectionStore();
const game = useGameStore();
const urlInput = ref<string>(conn.serverUrl);

function onConnect(): void {
  conn.connect(urlInput.value.trim() || conn.serverUrl);
}

function onDisconnect(): void {
  conn.disconnect();
}

/** 手动重连——重连失败上限 / 重连中用户希望立即重试时触发。 */
function onRetryConnect(): void {
  // 用 urlInput 让用户可以修改 URL 后手动重连（覆盖 serverUrl）
  const url = urlInput.value.trim() || conn.serverUrl;
  conn.serverUrl = url;
  void conn.retryConnect();
}

// issue 06：'reconnecting' 文案诚实显示当前尝试次数（N/10）让用户感知进度。
// 'disconnected' 区分 reconnectFailed=true（已耗尽自动重试）与 false（用户主动断开）。
// 用 computed 让 retryCount / reconnectFailed 变化时文案响应式更新。
const statusText = computed<Record<ConnectionStatus, string>>(() => ({
  disconnected: conn.reconnectFailed ? '连接失败' : '已断开',
  connecting: '连接中…',
  connected: '已连接',
  reconnecting: `重连中…（${Math.min(conn.retryCount, MAX_RECONNECT_ATTEMPTS)}/${MAX_RECONNECT_ATTEMPTS}）`,
}));
</script>

<template>
  <div class="conn-panel">
    <span
      class="status-dot"
      :class="{
        green: conn.status === 'connected',
        yellow: conn.status === 'connecting' || conn.status === 'reconnecting',
        red: conn.status === 'disconnected',
      }"
      :title="statusText[conn.status]"
    />
    <span class="status-text">{{ statusText[conn.status] }}</span>
    <input
      v-model="urlInput"
      class="url-input"
      type="text"
      placeholder="ws://localhost:5173/ws"
      :disabled="conn.status === 'connected' || conn.status === 'connecting'"
      @keyup.enter="onConnect"
    />
    <!--
      按钮策略（issue 06）：
      - 'connected' / 'connecting'：显示"断开"按钮
      - 'reconnecting'：显示"立即重连"按钮——用户可跳过指数退避立即重试
      - 'disconnected' + reconnectFailed=true：显示"重新连接"按钮（强调失败状态需手动恢复）
      - 'disconnected' + reconnectFailed=false（用户主动断开）：显示"连接"按钮
    -->
    <button
      v-if="conn.status === 'disconnected' && !conn.reconnectFailed"
      class="conn-btn"
      @click="onConnect"
    >
      连接
    </button>
    <button
      v-else-if="conn.status === 'disconnected' && conn.reconnectFailed"
      class="conn-btn retry"
      @click="onRetryConnect"
    >
      重新连接
    </button>
    <button
      v-else-if="conn.status === 'reconnecting'"
      class="conn-btn retry"
      @click="onRetryConnect"
    >
      立即重连
    </button>
    <button v-else class="conn-btn disconnect" @click="onDisconnect">断开</button>
    <span v-if="game.protocolVersion !== null" class="proto-version">
      v{{ game.protocolVersion }}
    </span>
    <span v-if="conn.closeReason && conn.status !== 'connected'" class="close-reason">
      {{ conn.closeReason }}
    </span>
  </div>
</template>

<style scoped>
.conn-panel {
  display: flex;
  align-items: center;
  gap: 8px;
  font-size: 13px;
  flex-wrap: wrap;
}
.status-dot {
  width: 10px;
  height: 10px;
  border-radius: 50%;
  display: inline-block;
  flex-shrink: 0;
}
.status-dot.green {
  background: #4ec9b0;
}
.status-dot.yellow {
  background: #dcdcaa;
}
.status-dot.red {
  background: #f48771;
}
.status-text {
  min-width: 50px;
}
.url-input {
  background: #1e1e1e;
  color: #e0e0e0;
  border: 1px solid #3c3c3c;
  padding: 3px 8px;
  border-radius: 3px;
  font-family: ui-monospace, Consolas, monospace;
  font-size: 12px;
  min-width: 240px;
}
.url-input:disabled {
  opacity: 0.6;
}
.conn-btn {
  background: #0e639c;
  color: #fff;
  border: none;
  padding: 4px 12px;
  border-radius: 3px;
  cursor: pointer;
  font-size: 13px;
}
.conn-btn.disconnect {
  background: #5a1d1d;
}
.conn-btn.retry {
  background: #5a4a1d;
}
.proto-version {
  color: #4ec9b0;
  font-family: ui-monospace, Consolas, monospace;
}
.close-reason {
  color: #f48771;
  font-family: ui-monospace, Consolas, monospace;
  font-size: 12px;
}
</style>
