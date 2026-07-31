<script setup lang="ts">
import { computed } from 'vue';
import { useConnectionStore, MAX_RECONNECT_ATTEMPTS } from '../stores/connection';
import { useGameStore } from '../stores/game';
import { useUiStore } from '../stores/ui';
import { deriveDisplayStatus } from '../lib/loadingStatus';
import TerminalDisplay from '../components/TerminalDisplay.vue';
import InputBar from '../components/InputBar.vue';
import TinputCountdown from '../components/TinputCountdown.vue';

const conn = useConnectionStore();
const game = useGameStore();
const ui = useUiStore();

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

/**
 * 输入栏自动弹出条件——全屏 UI 状态驱动：
 * 仅当处于 WaitInput、需要文本/数字输入（IntValue/StrValue/AnyValue）、
 * 且当前回合没有可用按钮（有按钮时按钮即输入手段）才自动弹出。
 * `⌨` 手动唤出（ui.manualInputVisible）与自动条件正交，随时可显示。
 */
const shouldShowInputBar = computed<boolean>(() => {
  const st = game.displayState.state;
  if (st !== 'WaitInput') return false;
  const t = game.displayState.inputType;
  if (t !== 'IntValue' && t !== 'StrValue' && t !== 'AnyValue') return false;
  return !game.hasActiveButtons;
});

/** 输入栏最终可见性：自动（文本/数字输入且无按钮）或 ⌨ 手动唤出（仅 WaitInput 时生效）。 */
const showInputBar = computed<boolean>(() => {
  if (ui.manualInputVisible) return game.displayState.state === 'WaitInput';
  return shouldShowInputBar.value;
});
</script>

<template>
  <section class="terminal-view">
    <!--
      .terminal-area 撑满整屏（全屏 UI）。TerminalDisplay 内部 .terminal 填满父容器宽度
      （flex: 1），内容按游戏宽度约束居中。背景由 .terminal 的游戏 bgColor 铺满全宽。
      InputBar / TinputCountdown / 状态条都是 position:absolute 覆盖层，不占布局高度。
    -->
    <div class="terminal-area">
      <TerminalDisplay />
    </div>

    <!-- TINPUT 倒计时——状态驱动，触发才显示（时间关键，独立于输入栏） -->
    <div class="overlay-top">
      <TinputCountdown />
    </div>

    <!-- 输入栏——自动弹出（文本/数字输入且无可用按钮）或 ⌨ 手动唤出 -->
    <InputBar v-if="showInputBar" class="overlay-bottom" />

    <!-- 状态提示——overlay 非常驻（加载中 / 连接异常 / 解析错误） -->
    <div v-if="game.lastError" class="terminal-status-bar error">
      <span>解析错误：{{ game.lastError }}</span>
    </div>
    <div v-else-if="displayStatus === 'loading'" class="terminal-status-bar warn">
      <span>加载中…</span>
    </div>
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
  height: 100%;
  width: 100%;
  background: #1e1e1e;
  position: relative;
}
/* terminal-area——撑满整屏（全屏 UI），overflow 委托给 TerminalDisplay 内部虚拟滚动。 */
.terminal-area {
  position: absolute;
  inset: 0;
  display: flex;
  justify-content: flex-start;
  overflow-x: hidden;
  overflow-y: hidden;
  background: #1e1e1e;
}
/* 覆盖层：不占布局高度，悬浮在终端之上。 */
.overlay-top {
  position: absolute;
  top: 8px;
  left: 0;
  right: 0;
  display: flex;
  justify-content: center;
  pointer-events: none;
  z-index: 5;
}
.overlay-bottom {
  position: absolute;
  left: 0;
  right: 0;
  bottom: 0;
  z-index: 5;
}
.terminal-status-bar {
  position: absolute;
  left: 0;
  right: 0;
  bottom: 0;
  padding: 6px 12px;
  font-family: ui-monospace, Consolas, monospace;
  font-size: 12px;
  display: flex;
  align-items: center;
  gap: 8px;
  z-index: 6;
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
