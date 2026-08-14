<script setup lang="ts">
import { computed, ref, watch } from 'vue';
import { useConnectionStore, MAX_RECONNECT_ATTEMPTS } from '../stores/connection';
import { useGameStore } from '../stores/game';
import { useUiStore } from '../stores/ui';
import { deriveDisplayStatus } from '../lib/loadingStatus';
import TerminalDisplay from '../components/TerminalDisplay.vue';
import InputBar from '../components/InputBar.vue';
import TinputCountdown from '../components/TinputCountdown.vue';
import StatusBanner from '../components/StatusBanner.vue';
import SpectatorBanner from '../components/SpectatorBanner.vue';

const conn = useConnectionStore();
const game = useGameStore();
const ui = useUiStore();

/**
 * Issue 11 D5：连接状态条派生显示状态。
 * reloadStatus='loading' 时（loadGame 进行中）强制显示「加载中…」，
 * 屏蔽旧 WS 断开瞬间的 'disconnected' 闪烁（game.reloadStatus 是单一真相源）。
 */
const displayStatus = computed(() => deriveDisplayStatus(game.reloadStatus, conn.status));

/** 解析错误提示条本地可关闭标记——lastError 变化时重置。 */
const dismissedError = ref(false);
watch(
  () => game.lastError,
  () => {
    dismissedError.value = false;
  },
);

/**
 * 输入栏最终可见性（ui-redesign-spec §6.3 / §6.4）：
 * - WaitInput：⌨ 手动唤出恒显示；自动时当前回合无按钮即显示
 *   （文本/数字类型 → 输入行；AnyKey/EnterKey → 「按任意键/回车继续」提示行，spec §6.3）
 * - Quit / Error：终端底部显示非模态状态行（spec §6.4），不常驻大条幅
 * - Running / 空闲：不显示（连接状态由应用栏/顶部提示条表达）
 */
const showInputBar = computed<boolean>(() => {
  const st = game.displayState.state;
  if (st === 'WaitInput') {
    if (ui.manualInputVisible) return true;
    return !game.hasActiveButtons;
  }
  if (st === 'Quit' || st === 'Error') return true;
  return false;
});
</script>

<template>
  <section class="terminal-view">
    <!-- 终端内容撑满整屏；InputBar / TinputCountdown / 状态条为 absolute 覆盖层 -->
    <div class="terminal-area">
      <TerminalDisplay />
    </div>

    <!-- TINPUT 倒计时——时间关键，独立于输入栏（顶部居中窄状态行） -->
    <div class="overlay-top">
      <TinputCountdown />
    </div>

    <!-- 顶部状态提示条（spec §6.4：重连 / 加载 / 错误用顶部窄条，不常驻大条幅） -->
    <div class="overlay-status">
      <StatusBanner
        v-if="game.lastError && !dismissedError"
        kind="error"
        closable
        @close="dismissedError = true"
      >
        解析错误：{{ game.lastError }}
      </StatusBanner>
      <StatusBanner v-else-if="displayStatus === 'loading'" kind="warning">
        加载中…
      </StatusBanner>
      <StatusBanner
        v-else-if="displayStatus === 'reconnecting'"
        kind="warning"
      >
        连接异常断开，正在自动重连…（尝试 {{ conn.retryCount }}/{{ MAX_RECONNECT_ATTEMPTS }}）{{ conn.closeReason ? ` — ${conn.closeReason}` : '' }}
      </StatusBanner>
      <StatusBanner
        v-else-if="displayStatus === 'disconnected' && conn.reconnectFailed"
        kind="error"
      >
        连接失败：自动重连已耗尽，请检查服务器后点击「重新连接」按钮。{{ conn.closeReason ? ` — ${conn.closeReason}` : '' }}
      </StatusBanner>
      <StatusBanner v-else-if="conn.controlError" kind="warning">
        {{ conn.controlError }}
      </StatusBanner>
      <SpectatorBanner />
    </div>

    <!-- 输入栏——自动弹出（文本/数字输入且无按钮）或 ⌨ 手动唤出；
         出现/消失仅做高度与 opacity 短过渡（spec §6.3） -->
    <Transition name="inputbar">
      <InputBar v-if="showInputBar" class="overlay-bottom" />
    </Transition>
  </section>
</template>

<style scoped>
.terminal-view {
  height: 100%;
  width: 100%;
  background: var(--color-bg);
  position: relative;
}
.terminal-area {
  position: absolute;
  inset: 0;
  display: flex;
  justify-content: flex-start;
  overflow: hidden;
  background: var(--color-bg);
}
/* 覆盖层：不占布局高度，悬浮在终端之上 */
.overlay-top {
  position: absolute;
  top: var(--space-2);
  left: 0;
  right: 0;
  display: flex;
  justify-content: center;
  pointer-events: none;
  z-index: 5;
}
.overlay-status {
  position: absolute;
  top: var(--space-2);
  left: var(--space-3);
  right: var(--space-3);
  display: flex;
  flex-direction: column;
  gap: var(--space-1);
  z-index: 6;
  pointer-events: none;
}
.overlay-status :deep(.status-banner) {
  pointer-events: auto;
}
.overlay-bottom {
  position: absolute;
  left: 0;
  right: 0;
  bottom: 0;
  z-index: 5;
}
</style>
