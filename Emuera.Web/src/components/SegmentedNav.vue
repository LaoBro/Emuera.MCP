<script setup lang="ts">
import { useUiStore, type UiView } from '../stores/ui';

/**
 * SegmentedNav.vue — Debug / Settings / Terminal 视图切换（ui-redesign-spec §7）。
 * 替换原桌面头部三个同权重按钮；MAUI 模式由「更多」菜单承担同语义切换。
 * 支持 ←/→ 方向键循环切换（spec §9 ARIA tablist 模式）。
 */
const ui = useUiStore();

const segments: { value: UiView; label: string }[] = [
  { value: 'terminal', label: 'Terminal' },
  { value: 'debug', label: 'Debug' },
  { value: 'settings', label: 'Settings' },
];

/** ←/→ 方向键循环切换当前视图。 */
function onKeydown(e: KeyboardEvent): void {
  const idx = segments.findIndex((s) => s.value === ui.currentView);
  if (e.key === 'ArrowRight') {
    e.preventDefault();
    ui.switchView(segments[(idx + 1) % segments.length].value);
  } else if (e.key === 'ArrowLeft') {
    e.preventDefault();
    ui.switchView(segments[(idx - 1 + segments.length) % segments.length].value);
  }
}
</script>

<template>
  <nav class="segmented-nav" role="tablist" aria-label="视图切换" @keydown="onKeydown">
    <button
      v-for="s in segments"
      :key="s.value"
      type="button"
      class="seg-btn"
      :class="{ active: ui.currentView === s.value }"
      role="tab"
      :aria-selected="ui.currentView === s.value"
      @click="ui.switchView(s.value)"
    >
      {{ s.label }}
    </button>
  </nav>
</template>

<style scoped>
.segmented-nav {
  display: inline-flex;
  gap: 2px;
  padding: 3px;
  background: var(--color-bg);
  border: 1px solid var(--color-border);
  border-radius: var(--radius-control);
}
.seg-btn {
  background: transparent;
  border: none;
  color: var(--color-text-muted);
  padding: 4px 14px;
  border-radius: calc(var(--radius-control) - 2px);
  font-size: var(--font-size-md);
  font-family: var(--font-ui);
  cursor: pointer;
  transition: background var(--motion-fast), color var(--motion-fast);
}
.seg-btn:hover:not(.active) {
  background: var(--color-surface-raised);
  color: var(--color-text);
}
.seg-btn.active {
  background: var(--color-surface-raised);
  color: var(--color-text);
}
</style>
