<script setup lang="ts">
/**
 * ConfirmDialog.vue — Android 风格 Alert Dialog（ui-redesign-spec §6.1）。
 * 半透明遮罩 + 表面容器；底部「取消」与确认两个文字按钮；
 * 危险确认使用 --color-error 文字（不做红色实心按钮）；
 * 点击遮罩、按 Escape（桌面等价于 Android 返回键）取消；防重复确认由父组件控制 visible。
 */
import { onMounted, onUnmounted } from 'vue';

const props = withDefaults(
  defineProps<{
    visible: boolean;
    title?: string;
    message?: string;
    confirmLabel?: string;
    cancelLabel?: string;
    /** 确认按钮是否使用危险色（默认是）。 */
    danger?: boolean;
  }>(),
  { title: '确认操作', confirmLabel: '确认', cancelLabel: '取消', danger: true },
);

const emit = defineEmits<{
  (e: 'confirm'): void;
  (e: 'cancel'): void;
  (e: 'update:visible', v: boolean): void;
}>();

function onConfirm(): void {
  emit('confirm');
}

function onCancel(): void {
  emit('cancel');
  emit('update:visible', false);
}

/** Escape（桌面键盘等价于 Android 返回键）——对话框打开时默认取消（spec §6.1）。 */
function onDocKeydown(e: KeyboardEvent): void {
  if (e.key === 'Escape' && props.visible) onCancel();
}

onMounted(() => document.addEventListener('keydown', onDocKeydown));
onUnmounted(() => document.removeEventListener('keydown', onDocKeydown));
</script>

<template>
  <div
    v-if="visible"
    class="confirm-overlay"
    @click.self="onCancel"
  >
    <div
      class="confirm-dialog"
      role="alertdialog"
      aria-modal="true"
      :aria-label="title"
    >
      <h2 class="confirm-title">{{ title }}</h2>
      <p v-if="message" class="confirm-message">{{ message }}</p>
      <div class="confirm-actions">
        <button type="button" class="confirm-btn text" @click="onCancel">
          {{ cancelLabel }}
        </button>
        <button
          type="button"
          class="confirm-btn text"
          :class="{ danger }"
          @click="onConfirm"
        >
          {{ confirmLabel }}
        </button>
      </div>
    </div>
  </div>
</template>

<style scoped>
.confirm-overlay {
  position: fixed;
  inset: 0;
  background: var(--color-overlay);
  display: flex;
  align-items: center;
  justify-content: center;
  z-index: 1000;
  padding: var(--space-4);
  animation: fade-in var(--motion-fast) ease-out;
}
.confirm-dialog {
  background: var(--color-surface);
  border: none;
  border-radius: var(--radius-surface);
  box-shadow: var(--elevation-menu);
  padding: var(--space-5) var(--space-5) var(--space-4);
  max-width: 360px;
  width: 100%;
  display: flex;
  flex-direction: column;
  gap: var(--space-2);
  animation: dialog-in var(--motion-fast) ease-out;
}
.confirm-title {
  margin: 0;
  font-size: var(--font-size-lg);
  font-weight: 600;
  color: var(--color-text);
}
.confirm-message {
  margin: 0;
  font-size: var(--font-size-base);
  color: var(--color-text-muted);
  line-height: 1.5;
  word-break: break-all;
}
.confirm-actions {
  display: flex;
  justify-content: flex-end;
  gap: var(--space-2);
  margin-top: var(--space-3);
}
.confirm-btn.text {
  background: #353638;
  color: #ffffff;
  border: none;
  padding: var(--space-2) var(--space-4);
  border-radius: var(--radius-control);
  cursor: pointer;
  font-size: var(--font-size-base);
  font-family: var(--font-ui);
  font-weight: 500;
  min-height: 40px;
  transition: background var(--motion-fast);
}
.confirm-btn.text:hover {
  background: #414247;
}
.confirm-btn.text.danger {
  color: #ffffff;
}
</style>
