<script setup lang="ts">
import { useConnectionStore, MAX_RECONNECT_ATTEMPTS } from '../stores/connection';
import { useGameStore } from '../stores/game';
import TerminalDisplay from '../components/TerminalDisplay.vue';
import InputBar from '../components/InputBar.vue';

const conn = useConnectionStore();
const game = useGameStore();
</script>

<template>
  <section class="terminal-view">
    <TerminalDisplay />
    <InputBar />
    <div v-if="game.lastError" class="terminal-status-bar error">
      <span>解析错误：{{ game.lastError }}</span>
    </div>
    <!--
      issue 06：连接状态条文案。
      - 'reconnecting'：自动重连进行中，告诉用户尝试次数 + closeReason（如有）
      - 'disconnected' + reconnectFailed=true：自动重连耗尽，提示用户手动重连
    -->
    <div
      v-else-if="conn.status === 'reconnecting'"
      class="terminal-status-bar warn"
    >
      <span>连接异常断开，正在自动重连…（尝试 {{ conn.retryCount }}/{{ MAX_RECONNECT_ATTEMPTS }}）{{ conn.closeReason ? ` — ${conn.closeReason}` : '' }}</span>
    </div>
    <div
      v-else-if="conn.status === 'disconnected' && conn.reconnectFailed"
      class="terminal-status-bar error"
    >
      <span>连接失败：自动重连已耗尽，请检查服务器后点击"重新连接"按钮。{{ conn.closeReason ? ` — ${conn.closeReason}` : '' }}</span>
    </div>
  </section>
</template>

<style scoped>
.terminal-view {
  display: flex;
  flex-direction: column;
  height: 100%;
  width: 100%;
  background: #1e1e1e;
  position: relative;
}
.terminal-status-bar {
  flex-shrink: 0;
  padding: 6px 12px;
  font-family: ui-monospace, Consolas, monospace;
  font-size: 12px;
  display: flex;
  align-items: center;
  gap: 8px;
}
.terminal-status-bar.error {
  background: #5a1d1d;
  color: #f48771;
}
.terminal-status-bar.warn {
  background: #5a4a1d;
  color: #dcdcaa;
}
</style>
