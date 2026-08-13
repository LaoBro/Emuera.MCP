<script setup lang="ts">
import { ref, computed } from 'vue';
import { useConnectionStore, type ConnectionStatus, MAX_RECONNECT_ATTEMPTS } from '../stores/connection';
import { useGameStore } from '../stores/game';
import { deriveDisplayStatus } from '../lib/loadingStatus';

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

/**
 * Issue 11 D5：reloadStatus='loading' 时强制覆盖连接状态显示。
 *
 * loadGame 切换游戏目录时：disconnect 旧 WS → POST /load-game → connect 新 WS。
 * 期间 conn.status 短暂为 'disconnected'——若按原逻辑显示"已断开"，会让用户误以为
 * 连接异常断开。覆盖为"加载中…"让 UI 与切换动作语义一致。
 *
 * 同时 status-dot 颜色按"进行中"语义显示黄色（与 connecting/reconnecting 同色），
 * 而非红色——避免红色"错误"暗示。
 */
const displayStatus = computed(() => deriveDisplayStatus(game.reloadStatus, conn.status));
const displayStatusText = computed(() =>
  displayStatus.value === 'loading' ? '加载中…' : statusText.value[displayStatus.value],
);
const statusDotClass = computed(() => {
  const s = displayStatus.value;
  if (s === 'loading' || s === 'connecting' || s === 'reconnecting') return 'yellow';
  if (s === 'connected') return 'green';
  return 'red';
});
</script>

<template>
  <div class="conn-panel">
    <span
      class="status-dot"
      :class="statusDotClass"
      :title="displayStatusText"
    />
    <span class="status-text">{{ displayStatusText }}</span>
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
      class="btn-outline"
      @click="onConnect"
    >
      连接
    </button>
    <button
      v-else-if="conn.status === 'disconnected' && conn.reconnectFailed"
      class="btn-outline"
      @click="onRetryConnect"
    >
      重新连接
    </button>
    <button
      v-else-if="conn.status === 'reconnecting'"
      class="btn-outline"
      @click="onRetryConnect"
    >
      立即重连
    </button>
    <button v-else class="btn-outline" @click="onDisconnect">断开</button>
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
  gap: var(--space-2);
  font-size: var(--font-size-md);
  flex-wrap: wrap;
  color: var(--color-text);
}
.status-dot {
  width: 10px;
  height: 10px;
  border-radius: 50%;
  display: inline-block;
  flex-shrink: 0;
  transition: background var(--motion-fast);
}
.status-dot.green {
  background: var(--color-success);
}
.status-dot.yellow {
  background: var(--color-warning);
}
.status-dot.red {
  background: var(--color-error);
}
.status-text {
  min-width: 50px;
  color: var(--color-text-muted);
}
.url-input {
  background: var(--color-bg);
  color: var(--color-text);
  border: 1px solid var(--color-border);
  padding: 3px var(--space-2);
  border-radius: var(--radius-control);
  font-family: var(--font-mono);
  font-size: var(--font-size-sm);
  min-width: 240px;
}
.url-input:disabled {
  opacity: 0.6;
}
/* 连接按钮——统一实心控件（DESIGN.md），连接状态由 status-dot / status-text 表达 */
.proto-version {
  color: var(--color-success);
  font-family: var(--font-mono);
  font-size: var(--font-size-sm);
}
.close-reason {
  color: var(--color-error);
  font-family: var(--font-mono);
  font-size: var(--font-size-sm);
}
</style>
