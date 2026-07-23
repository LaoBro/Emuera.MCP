<script setup lang="ts">
import { computed } from 'vue';
import { useConnectionStore, MAX_RECONNECT_ATTEMPTS } from '../stores/connection';
import { useGameStore } from '../stores/game';
import { deriveDisplayStatus } from '../lib/loadingStatus';
import TerminalDisplay from '../components/TerminalDisplay.vue';
import InputBar from '../components/InputBar.vue';

const conn = useConnectionStore();
const game = useGameStore();

/**
 * Issue 11 D5：连接状态条派生显示状态。
 *
 * reloadStatus='loading' 时（loadGame 进行中：disconnect 旧 WS → POST /load-game → connect 新 WS），
 * 旧 WS 已断开但新 WS 尚未连上——conn.status 会短暂为 'disconnected'。若直接显示 connection.status
 * 用户会看到"已断开"闪烁，破坏切换游戏时的"加载中"体验。
 *
 * 解决：reloadStatus='loading' 时强制显示"加载中…"，屏蔽 'disconnected' 闪烁；
 * 否则按 connection.status 派生正常状态条文案。
 *
 * 与 GamePicker.vue 的 isLoading computed 一致——reloadStatus 是单一真相源。
 */
const displayStatus = computed(() => deriveDisplayStatus(game.reloadStatus, conn.status));
</script>

<template>
  <section class="terminal-view">
    <!--
      .terminal-area 作为 flex 容器撑满 TerminalView。TerminalDisplay 内部
      .terminal 填满父容器宽度（flex: 1），通过 .terminal-content 约束内容
      为游戏宽度并水平居中。背景由 .terminal 的游戏 bgColor 铺满全宽。
    -->
    <div class="terminal-area">
      <TerminalDisplay />
    </div>
    <InputBar />
    <div v-if="game.lastError" class="terminal-status-bar error">
      <span>解析错误：{{ game.lastError }}</span>
    </div>
    <!--
      Issue 11 D5：reloadStatus='loading' 时强制显示"加载中…"，不显示"已断开"——
      避免 loadGame 切换游戏目录时旧 WS 已断新 WS 未连造成的 UI 闪断。
    -->
    <div v-else-if="displayStatus === 'loading'" class="terminal-status-bar warn">
      <span>加载中…</span>
    </div>
    <!--
      issue 06：连接状态条文案。
      - 'reconnecting'：自动重连进行中，告诉用户尝试次数 + closeReason（如有）
      - 'disconnected' + reconnectFailed=true：自动重连耗尽，提示用户手动重连
    -->
    <div
      v-else-if="displayStatus === 'reconnecting'"
      class="terminal-status-bar warn"
    >
      <span>连接异常断开，正在自动重连…（尝试 {{ conn.retryCount }}/{{ MAX_RECONNECT_ATTEMPTS }}）{{ conn.closeReason ? ` — ${conn.closeReason}` : '' }}</span>
    </div>
    <div
      v-else-if="displayStatus === 'disconnected' && conn.reconnectFailed"
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
/* terminal-area——flex 容器，撑满 TerminalView 剩余高度。
   flex: 1 + min-height: 0 让此区域占据 TerminalView 剩余高度（InputBar/状态栏在下方）。
   TerminalDisplay 内部的 .terminal 填满父容器宽度，内容按游戏宽度约束居中。
   overflow-x: hidden——水平滚动委托给 .terminal 内部。 */
.terminal-area {
  display: flex;
  justify-content: flex-start;
  overflow-x: hidden;
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
