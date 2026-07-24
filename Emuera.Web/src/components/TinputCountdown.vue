<script setup lang="ts">
import { computed } from 'vue';
import { useGameStore } from '../stores/game';

/**
 * TinputCountdown.vue — TINPUT 实时倒计时轻量组件（ADR-0016）。
 *
 * 仅依赖 game store 的 tinputRemainingMs / tinputTimeLimit / showTinputCountdown，
 * 不接触 displayState，避免因 displayState 变化触发不必要的重渲染。
 */
const game = useGameStore();

/** 倒计时进度条 max 值——总毫秒数。null 时不渲染。 */
const tinputProgressMax = computed<number | null>(() => game.tinputTimeLimit);

/** 倒计时进度条 value 值——剩余毫秒数。null 时不渲染。 */
const tinputProgressValue = computed<number | null>(() => game.tinputRemainingMs);

/** 倒计时显示文案——剩余秒数（保留 1 位小数）。null 时不显示。 */
const tinputCountdownText = computed<string | null>(() => {
  const remaining = game.tinputRemainingMs;
  if (remaining === null) return null;
  const seconds = remaining / 1000;
  return `${seconds.toFixed(1)}s`;
});
</script>

<template>
  <div v-if="game.showTinputCountdown" class="tinput-countdown">
    <progress
      class="tinput-progress"
      :max="tinputProgressMax ?? 1"
      :value="tinputProgressValue ?? 0"
    ></progress>
    <span class="tinput-countdown-text">⏱ {{ tinputCountdownText ?? '' }}</span>
  </div>
</template>

<style scoped>
.tinput-countdown {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 4px 8px;
  background: #1e3a5f;
  border-radius: 3px;
  border: 1px solid #2a5a8f;
  font-size: 12px;
  color: #9cdcfe;
}
.tinput-progress {
  flex: 1;
  height: 6px;
  background: #1e1e1e;
  border: 1px solid #3c3c3c;
  border-radius: 2px;
  /* progress 元素原生外观重置 */
  -webkit-appearance: none;
  appearance: none;
}
.tinput-progress::-webkit-progress-bar {
  background: #1e1e1e;
  border-radius: 2px;
}
.tinput-progress::-webkit-progress-value {
  background: #0e639c;
  border-radius: 2px;
  transition: width 0.1s linear;
}
.tinput-progress::-moz-progress-bar {
  background: #0e639c;
  border-radius: 2px;
}
.tinput-countdown-text {
  font-family: ui-monospace, Consolas, monospace;
  min-width: 60px;
  text-align: right;
}
</style>
