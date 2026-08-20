<script setup lang="ts">
import { computed } from 'vue';
import { useConnectionStore } from '../stores/connection';
import { httpGameLibrarySource } from '../lib/gameLibrary';
import GameLibraryView from './GameLibraryView.vue';

/**
 * WebGameList — Web/HTTP 模式的游戏选择页。
 *
 * 现在只是 GameLibraryView（与 MAUI 共用一份设计）的薄 wrapper——
 * 平台差异只剩右上角连接状态按钮：
 * - 数据获取（扫描/目录浏览/加载）经 httpGameLibrarySource（C# server /game/scan、/game/dirs）
 * - 「浏览目录」用页面内目录浏览（浏览器沙箱无原生选择器）
 * - 点连接按钮 → 通知 App.vue 回落到标准布局（ConnectionPanel + 手动路径输入逃生口）
 */
const emit = defineEmits<{
  (e: 'open-connection'): void;
}>();

const conn = useConnectionStore();

/** 连接状态文案/圆点——已连接 / 重连中显示绿点，未连接显示提示。 */
const connLabel = computed(() =>
  conn.status === 'connected' ? '已连接' : '连接设置',
);
const connDotClass = computed(() =>
  conn.status === 'connected' || conn.status === 'reconnecting'
    ? 'connected'
    : 'disconnected',
);
</script>

<template>
  <GameLibraryView :source="httpGameLibrarySource">
    <template #appbar-actions>
      <button
        type="button"
        class="appbar-conn-btn"
        aria-label="打开连接设置"
        title="连接设置"
        @click="emit('open-connection')"
      >
        <span class="conn-dot" :class="connDotClass" aria-hidden="true" />
        {{ connLabel }}
      </button>
    </template>
  </GameLibraryView>
</template>

<style scoped>
.appbar-conn-btn {
  display: inline-flex;
  align-items: center;
  gap: var(--space-1);
  background: transparent;
  color: var(--color-text-muted);
  border: 1px solid var(--color-border);
  border-radius: var(--radius-control);
  padding: var(--space-1) var(--space-2);
  font-size: var(--font-size-sm);
  font-family: var(--font-ui);
  cursor: pointer;
}
.appbar-conn-btn:hover {
  background: var(--color-surface-raised);
  color: var(--color-text);
}
.conn-dot {
  width: 8px;
  height: 8px;
  border-radius: 9999px;
  flex-shrink: 0;
}
.conn-dot.connected {
  background: var(--color-success);
}
.conn-dot.disconnected {
  background: var(--color-warning);
}
</style>
