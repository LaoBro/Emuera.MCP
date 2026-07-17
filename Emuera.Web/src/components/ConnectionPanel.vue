<script setup lang="ts">
import { ref } from 'vue';
import { useConnectionStore, type ConnectionStatus } from '../stores/connection';
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

// v1 不做自动重连（issue 06 引入指数退避）。'reconnecting' 仅标记"曾异常断开"，
// 文案诚实显示"已断开（异常）"，避免误导用户以为正在重连。
const statusText: Record<ConnectionStatus, string> = {
  disconnected: '已断开',
  connecting: '连接中…',
  connected: '已连接',
  reconnecting: '已断开（异常）',
};
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
    <button
      v-if="conn.status === 'disconnected' || conn.status === 'reconnecting'"
      class="conn-btn"
      @click="onConnect"
    >
      连接
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
