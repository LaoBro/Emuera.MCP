<script setup lang="ts">
/**
 * GameRow.vue — 游戏选择页列表项（ui-redesign-spec §4 组件清单 / §5.3 + DESIGN.md）。
 * 安卓文件管理器式整行布局：左侧文件夹图标、中间游戏名、右侧进入箭头。
 * 上次游玩项使用 --color-surface-raised 与 left-indicator-bar（4px 天蓝，DESIGN.md），
 * 不使用强烈彩色背景。
 */
defineProps<{
  /** 游戏名。 */
  name: string;
  /** 是否上次游玩——列表高亮（left-indicator-bar）。 */
  lastPlayed?: boolean;
}>();

defineEmits<{
  (e: 'click'): void;
}>();
</script>

<template>
  <li class="game-row" :class="{ 'last-played': lastPlayed }">
    <button type="button" class="game-row-btn" @click="$emit('click')">
      <span class="game-row-icon" aria-hidden="true">
        <svg viewBox="0 0 24 24" role="img">
          <path d="M3 6.5A2.5 2.5 0 0 1 5.5 4H10l2 2h6.5A2.5 2.5 0 0 1 21 8.5v9A2.5 2.5 0 0 1 18.5 20h-13A2.5 2.5 0 0 1 3 17.5z" />
        </svg>
      </span>
      <span class="game-row-main">
        <span class="game-row-name">{{ name }}</span>
        <span v-if="lastPlayed" class="game-row-meta">上次游玩</span>
      </span>
      <span class="game-row-arrow" aria-hidden="true">
        <svg viewBox="0 0 24 24" role="img">
          <path d="m9 18 6-6-6-6" />
        </svg>
      </span>
    </button>
  </li>
</template>

<style scoped>
.game-row {
  position: relative;
  list-style: none;
  display: grid;
  grid-template-columns: 1fr;
  align-items: center;
  overflow: hidden;
  border-radius: 12px;
  transition: background-color 0.2s ease;
}
.game-row.last-played {
  background: transparent;
}
.game-row.last-played::before {
  content: "";
  position: absolute;
  left: 0;
  top: 10px;
  bottom: 10px;
  width: 4px;
  background: #a8c7fa;
  border-radius: 2px;
  z-index: 2;
}
.game-row-btn {
  display: grid;
  grid-template-columns: 48px minmax(0, 1fr) 48px;
  align-items: center;
  width: 100%;
  min-height: 60px;
  text-align: left;
  background: transparent;
  color: #e8eaed;
  border: none;
  padding: 0;
  border-radius: 12px;
  cursor: pointer;
  font-size: 15px;
  font-family: Roboto, "Noto Sans SC", "Segoe UI", system-ui, -apple-system, sans-serif;
  transition: background-color 0.15s ease;
}
.game-row-btn:hover {
  background: color-mix(in srgb, #e8eaed 8%, transparent);
}
.game-row-btn:focus-visible {
  background: color-mix(in srgb, #e8eaed 12%, transparent);
  outline: 2px solid #a8c7fa;
  outline-offset: -2px;
}
.game-row-btn:active {
  background: color-mix(in srgb, #e8eaed 14%, transparent);
}
.game-row-icon {
  color: #bdc1c6;
  justify-self: center;
  display: flex;
  align-items: center;
}
.game-row-icon svg,
.game-row-arrow svg {
  width: 24px;
  height: 24px;
  fill: none;
  stroke: currentColor;
  stroke-width: 2;
  stroke-linecap: round;
  stroke-linejoin: round;
}
.game-row-main {
  display: flex;
  align-items: center;
  justify-content: flex-start;
  gap: 6px;
  min-width: 0;
  height: 100%;
}
.game-row-name {
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  line-height: 24px;
  color: #e8eaed;
}
.game-row-meta {
  flex-shrink: 0;
  margin-left: 8px;
  color: #bdc1c6;
  opacity: 0.65;
  font-size: 12px;
  white-space: nowrap;
}
.game-row-meta::before {
  content: "|";
  margin-right: 8px;
  opacity: 0.4;
}
.game-row-arrow {
  color: #bdc1c6;
  justify-self: center;
  display: flex;
  align-items: center;
  opacity: 0.7;
}
</style>
