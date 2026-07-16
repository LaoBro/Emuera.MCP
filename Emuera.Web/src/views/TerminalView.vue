<script setup lang="ts">
// Issue 01 placeholder：Terminal 视图在 issue 03 实现（TerminalDisplay.vue + 按钮渲染）。
// 此处仅占位，让 view-switch 能切换；默认视图为 Debug。
import { useGameStore } from '../stores/game';

const game = useGameStore();
</script>

<template>
  <section class="terminal-placeholder">
    <p>Terminal 视图将在 issue 03 实现。</p>
    <p v-if="game.lastTurnJson">当前已收到 {{ game.turnHistory.length }} 个 WS 帧。</p>
    <p v-else>尚未收到 WS 帧，请先连接服务器。</p>
  </section>
</template>

<style scoped>
.terminal-placeholder {
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  height: 100%;
  width: 100%;
  color: #888;
  font-style: italic;
  gap: 8px;
}
</style>
