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
    <!--
      Issue 12：外层 .terminal-area 用 flex justify-content: center 把固定宽度的
      TerminalDisplay 水平居中。视口宽度 < windowWidth 时 overflow-x: auto 让外层
      水平滚动，不强行压缩游戏画面。背景 #1e1e1e 与游戏画面黑底区分，两侧留白
      不渲染任何内容。
    -->
    <div class="terminal-area">
      <TerminalDisplay />
    </div>
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
/* Issue 12：terminal-area——外层 flex 居中 + 水平滚动容器。
   flex: 1 + min-height: 0 让此区域占据 TerminalView 剩余高度（InputBar/状态栏在下方）。
   justify-content: center 把固定宽度的 .terminal 水平居中。
   overflow-x: auto 在视口 < windowWidth 时出现水平滚动条，不压缩游戏画面。
   background: #1e1e1e 与游戏画面黑底区分——两侧留白区域可见深灰底色。 */
.terminal-area {
  display: flex;
  justify-content: center;
  overflow-x: auto;
  overflow-y: hidden;
  background: #1e1e1e;
  flex: 1;
  min-height: 0;
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
