<script lang="ts">
/**
 * PopupMenu.vue — Android 风格 Popup Menu（ui-redesign-spec §6.1）。
 * 高不透明度 --color-surface-raised、单档 --elevation-menu、--radius-surface(12px)；
 * 菜单项最小高度 48px，左侧图标、右侧文字；缩放作为菜单项（当前百分比 + 缩/放）；
 * 退出项与其它项之间用分隔线；点击外部或按 Escape 关闭（spec §6.1）。
 * 普通菜单项点击后自动关闭；出入场缩放动画由父级 <Transition name="popup"> 驱动。
 */
export type PopupMenuItem =
  | {
      type: 'item';
      id: string;
      label: string;
      icon?: string;
      danger?: boolean;
      disabled?: boolean;
      active?: boolean;
      onClick: () => void;
    }
  | {
      type: 'zoom';
      percent: number;
      canZoomOut: boolean;
      canZoomIn: boolean;
      onZoomOut: () => void;
      onZoomReset: () => void;
      onZoomIn: () => void;
    }
  | { type: 'separator' };
</script>

<script setup lang="ts">
import { ref, onMounted, onUnmounted } from 'vue';

defineProps<{ items: PopupMenuItem[] }>();

const emit = defineEmits<{
  (e: 'close'): void;
}>();

const rootEl = ref<HTMLElement | null>(null);

/** 点击菜单外部——关闭。mousedown 早于 click，避免菜单内点击冒泡误判。 */
function onDocPointerDown(e: MouseEvent): void {
  if (rootEl.value && !rootEl.value.contains(e.target as Node)) {
    emit('close');
  }
}

/** Escape——关闭。 */
function onDocKeydown(e: KeyboardEvent): void {
  if (e.key === 'Escape') emit('close');
}

onMounted(() => {
  document.addEventListener('mousedown', onDocPointerDown);
  document.addEventListener('keydown', onDocKeydown);
  // 焦点管理（spec §9）：打开时聚焦第一个可交互项，键盘用户可直接操作菜单
  const firstInteractive = rootEl.value?.querySelector<HTMLElement>(
    'button, [role="menuitem"]',
  );
  firstInteractive?.focus();
});
onUnmounted(() => {
  document.removeEventListener('mousedown', onDocPointerDown);
  document.removeEventListener('keydown', onDocKeydown);
});
</script>

<template>
  <div class="popup-root" role="presentation">
    <div ref="rootEl" class="popup-menu" role="menu">
    <template v-for="(it, i) in items" :key="i">
      <div v-if="it.type === 'separator'" class="popup-separator" role="separator" />

      <div v-else-if="it.type === 'zoom'" class="popup-zoom" role="group" aria-label="缩放">
        <span class="popup-zoom-label tabular-nums">缩放 {{ it.percent }}%</span>
        <div class="popup-zoom-actions">
          <button
            type="button"
            class="popup-zoom-btn"
            :disabled="it.canZoomOut"
            aria-label="缩小"
            @click="it.onZoomOut"
          >
            −
          </button>
          <button
            type="button"
            class="popup-zoom-btn popup-zoom-reset"
            aria-label="恢复 100%"
            @click="it.onZoomReset"
          >
            100%
          </button>
          <button
            type="button"
            class="popup-zoom-btn"
            :disabled="it.canZoomIn"
            aria-label="放大"
            @click="it.onZoomIn"
          >
            +
          </button>
        </div>
      </div>

      <button
        v-else
        type="button"
        class="popup-item"
        :class="{ danger: it.danger, active: it.active }"
        :disabled="it.disabled"
        role="menuitem"
        @click="it.onClick(); emit('close')"
      >
        <span v-if="it.icon" class="popup-item-icon" aria-hidden="true">{{ it.icon }}</span>
        <span class="popup-item-label">{{ it.label }}</span>
      </button>
    </template>
    </div>
  </div>
</template>

<style scoped>
/* 全屏遮罩（scrim）：作为 Transition 根元素，淡入淡出；点击遮罩区域走
   onDocPointerDown 的「rootEl 之外」分支关闭菜单。
   非模态浮层用轻档 --color-scrim-menu，强模态 ConfirmDialog 用 --color-overlay
   （浮层层级：dialog ≥1000 > menu 面板 900 > menu 遮罩 899） */
.popup-root {
  position: fixed;
  inset: 0;
  z-index: 899;
  background: var(--color-scrim-menu);
}
.popup-menu {
  position: fixed;
  top: calc(var(--appbar-height) + var(--space-2));
  right: var(--space-3);
  z-index: 900;
  min-width: 200px;
  background: var(--color-surface-raised);
  border: none;
  border-radius: var(--radius-surface);
  box-shadow: var(--elevation-menu);
  padding: var(--space-1);
  display: flex;
  flex-direction: column;
  gap: 2px;
}
.popup-item {
  display: flex;
  align-items: center;
  gap: var(--space-3);
  min-height: var(--touch-target);
  background: transparent;
  color: var(--color-text);
  border: none;
  text-align: left;
  padding: var(--space-2) var(--space-3);
  border-radius: var(--radius-control);
  font-size: var(--font-size-base);
  font-family: var(--font-ui);
  cursor: pointer;
  transition: background var(--motion-fast);
}
.popup-item:hover:not(:disabled) {
  background: var(--color-control-hover);
}
.popup-item.active {
  color: var(--color-text);
}
.popup-item.danger {
  color: var(--color-text);
}
.popup-item:disabled {
  opacity: 0.4;
  cursor: not-allowed;
}
.popup-item-icon {
  width: 20px;
  text-align: center;
  flex-shrink: 0;
}
.popup-item-label {
  flex: 1;
}
.popup-separator {
  height: 1px;
  background: var(--color-border);
  margin: var(--space-1) var(--space-2);
}
.popup-zoom {
  display: flex;
  flex-direction: column;
  align-items: stretch;
  gap: var(--space-2);
  padding: var(--space-2) var(--space-3);
  color: var(--color-text);
  font-size: var(--font-size-base);
}
.popup-zoom-label {
  line-height: 24px;
}
.popup-zoom-actions {
  display: flex;
  align-items: center;
  gap: var(--space-2);
}
.popup-zoom-btn {
  flex: 1;
  background: var(--color-control);
  color: var(--color-text);
  border: none;
  width: 32px;
  height: 32px;
  border-radius: var(--radius-control);
  cursor: pointer;
  font-size: var(--font-size-base);
  font-family: var(--font-ui);
  transition: background var(--motion-fast);
}
.popup-zoom-btn:hover:not(:disabled) {
  background: var(--color-control-hover);
}
.popup-zoom-btn:disabled {
  opacity: 0.4;
  cursor: not-allowed;
}
/* 「恢复 100%」按钮文字较宽，独立加宽 */
.popup-zoom-reset {
  width: auto;
  min-width: 48px;
  padding: 0 var(--space-2);
  font-size: var(--font-size-sm);
}
</style>
