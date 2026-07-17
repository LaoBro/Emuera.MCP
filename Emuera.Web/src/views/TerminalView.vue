<script setup lang="ts">
import { useConnectionStore } from '../stores/connection';
import { useGameStore } from '../stores/game';
import TerminalDisplay from '../components/TerminalDisplay.vue';

const conn = useConnectionStore();
const game = useGameStore();
</script>

<template>
  <section class="terminal-view">
    <TerminalDisplay />
    <div v-if="game.lastError" class="terminal-status-bar error">
      <span>解析错误：{{ game.lastError }}</span>
    </div>
    <div
      v-else-if="conn.status === 'reconnecting'"
      class="terminal-status-bar warn"
    >
      <span>连接异常断开（{{ conn.closeReason }}）。v1 不自动重连——请手动重连。</span>
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
