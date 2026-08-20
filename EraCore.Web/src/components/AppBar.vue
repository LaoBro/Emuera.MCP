<script setup lang="ts">
/**
 * AppBar.vue — 桌面共用应用栏（ui-redesign-spec §4.1 / §4.3 / §7）。
 * 56px 高（--appbar-height），--color-surface 背景 + 底部 1px 边界；
 * 左侧插槽：连接状态 / 标题 / 路径；actions 插槽：缩放 / 快速重开 / 退出；
 * nav 插槽：SegmentedNav（视图切换），位于应用栏下方。
 * MAUI 全屏游戏页不使用本组件（使用游戏壳层两个常驻无边框图标）。
 */
</script>

<template>
  <header class="app-bar">
    <div class="app-bar-main">
      <div class="app-bar-left">
        <slot name="left" />
      </div>
      <div class="app-bar-actions">
        <slot name="actions" />
      </div>
    </div>
    <div v-if="$slots.nav" class="app-bar-nav">
      <slot name="nav" />
    </div>
  </header>
</template>

<style scoped>
.app-bar {
  flex-shrink: 0;
  background: var(--color-surface);
  border-bottom: 1px solid var(--color-border);
}
.app-bar-main {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: var(--space-4);
  min-height: var(--appbar-height);
  padding: var(--space-2) var(--space-4);
}
.app-bar-left {
  display: flex;
  align-items: center;
  gap: var(--space-3);
  min-width: 0;
  flex: 1;
}
.app-bar-actions {
  display: flex;
  align-items: center;
  gap: var(--space-2);
  flex-shrink: 0;
}
.app-bar-nav {
  display: flex;
  align-items: center;
  padding: 0 var(--space-4) var(--space-2);
}
</style>
