<script setup lang="ts">
import { watch, computed } from 'vue';
import { useGameStore } from '../stores/game';
import { listDirectories } from '../lib/mauiBridge';

/**
 * game-library spec ID8：Android 目录浏览器弹窗。
 *
 * 用法：用户点击「更改主目录」按钮时打开。Vue 端投递 listDirectories(currentMainDir) →
 * C# 回复 directoriesListed → store.directoryList 写入 → 此组件渲染子目录列表。
 *
 * 交互：
 * - 点击子目录项 → listDirectories(selectedPath) 进入下一级
 * - 「返回上级」按钮 → listDirectories(parentPath)
 * - 「选择此目录」按钮 → 确认当前路径为主目录（emit confirm + 关闭弹窗）
 * - 「取消」按钮 → emit cancel + 关闭弹窗
 *
 * 起点：当前 mainGameDir（通常为 /storage/emulated/0/emuera 或类似）。
 * 不允许返回超过根（C# DirectoryLister.ComputeParentPath 在根处置 null）。
 */

const props = defineProps<{
  /** 弹窗可见性——v-model:visible 控制。 */
  visible: boolean;
}>();

const emit = defineEmits<{
  /** 用户确认选择当前目录为主目录。 */
  (e: 'confirm', path: string): void;
  /** 用户取消——关闭弹窗不更改主目录。 */
  (e: 'cancel'): void;
  /** v-model:visible 更新——关闭弹窗用。 */
  (e: 'update:visible', v: boolean): void;
}>();

const game = useGameStore();

/** 当前目录路径——store.directoryList.currentPath 或弹窗刚打开时的 mainGameDir。 */
const currentPath = computed(() => game.directoryList?.currentPath ?? game.mainGameDir ?? '');

/** 父目录路径——null 表示已在根，不可再上。 */
const parentPath = computed(() => game.directoryList?.parentPath ?? null);

/** 子目录名列表。 */
const subDirectories = computed(() => game.directoryList?.subDirectories ?? []);

/** 弹窗打开时拉取初始目录列表——visible 从 false→true 时触发。 */
watch(
  () => props.visible,
  (v) => {
    if (v) {
      // 弹窗打开——清旧数据并拉取子目录列表
      game.clearDirectoryList();
      // game-library spec ID8：Android 起点默认为 /storage/emulated/0/
      // （首次启动 mainGameDir 可能为 null）
      const startPath = game.mainGameDir || '/storage/emulated/0/';
      listDirectories(startPath);
    }
  },
  { immediate: true },
);

/** 点击子目录项——进入下一级。 */
function onSelectSub(name: string): void {
  const next = `${currentPath.value}/${name}`.replace(/\/+/g, '/');
  listDirectories(next);
}

/** 点击「返回上级」。 */
function onGoUp(): void {
  if (parentPath.value) {
    listDirectories(parentPath.value);
  }
}

/** 点击「选择此目录」——确认当前路径为主目录。 */
function onConfirm(): void {
  if (currentPath.value) {
    emit('confirm', currentPath.value);
  }
  close();
}

/** 点击「取消」。 */
function onCancel(): void {
  emit('cancel');
  close();
}

/** 关闭弹窗——清数据 + emit update:visible=false。 */
function close(): void {
  game.clearDirectoryList();
  emit('update:visible', false);
}
</script>

<template>
  <div v-if="visible" class="directory-browser-overlay" @click.self="onCancel">
    <div class="directory-browser-modal">
      <header class="db-header">
        <h2 class="db-title">选择主目录</h2>
        <div class="db-current" :title="currentPath">当前: {{ currentPath || '(空)' }}</div>
      </header>

      <div class="db-body">
        <button
          v-if="parentPath"
          class="db-up"
          @click="onGoUp"
        >
          📁 ..
        </button>
        <div v-if="subDirectories.length === 0" class="db-empty">
          （无子目录）
        </div>
        <ul class="db-list">
          <li v-for="name in subDirectories" :key="name">
            <button class="db-item" @click="onSelectSub(name)">
              📁 {{ name }}
            </button>
          </li>
        </ul>
      </div>

      <footer class="db-footer">
        <button class="btn-outline db-btn cancel" @click="onCancel">取消</button>
        <button class="btn-outline db-btn confirm" @click="onConfirm">选择此目录</button>
      </footer>
    </div>
  </div>
</template>

<style scoped>
.directory-browser-overlay {
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
.directory-browser-modal {
  background: var(--color-surface);
  border: 1px solid var(--color-border);
  border-radius: var(--radius-surface);
  box-shadow: var(--elevation-menu);
  display: flex;
  flex-direction: column;
  max-width: 560px;
  width: 100%;
  max-height: 80vh;
  overflow: hidden;
  animation: dialog-in var(--motion-fast) ease-out;
}
.db-header {
  padding: var(--space-3) var(--space-4);
  border-bottom: 1px solid var(--color-border);
  background: var(--color-surface);
}
.db-title {
  margin: 0 0 4px 0;
  font-size: var(--font-size-base);
  font-weight: 600;
  color: var(--color-text);
}
.db-current {
  font-size: var(--font-size-sm);
  color: var(--color-text-muted);
  font-family: var(--font-mono);
  word-break: break-all;
}
.db-body {
  flex: 1;
  overflow-y: auto;
  padding: var(--space-2) 0;
}
.db-up {
  display: block;
  width: 100%;
  text-align: left;
  background: transparent;
  color: var(--color-indicator);
  border: none;
  padding: var(--space-2) var(--space-4);
  cursor: pointer;
  font-size: var(--font-size-base);
  font-family: var(--font-mono);
  transition: background var(--motion-fast);
}
.db-up:hover {
  background: var(--color-surface-raised);
}
.db-empty {
  padding: var(--space-4);
  text-align: center;
  color: var(--color-text-muted);
  font-size: var(--font-size-md);
}
.db-list {
  list-style: none;
  margin: 0;
  padding: 0;
}
.db-item {
  display: block;
  width: 100%;
  text-align: left;
  background: transparent;
  color: var(--color-text);
  border: none;
  padding: var(--space-2) var(--space-4);
  cursor: pointer;
  font-size: var(--font-size-base);
  font-family: var(--font-mono);
  transition: background var(--motion-fast);
}
.db-item:hover {
  background: var(--color-surface-raised);
  color: var(--color-text);
}
.db-footer {
  display: flex;
  justify-content: flex-end;
  gap: var(--space-2);
  padding: var(--space-3) var(--space-4);
  border-top: 1px solid var(--color-border);
  background: var(--color-surface);
}
/* 基于 .btn-outline——取消用中性色，确认用主色填充 */
.db-btn.cancel {
  color: var(--color-text-muted);
  border-color: var(--color-border);
}
.db-btn.confirm {
  background: color-mix(in srgb, var(--color-indicator) 18%, var(--color-surface));
  color: var(--color-indicator);
  border-color: color-mix(in srgb, var(--color-indicator) 55%, var(--color-border));
}
.db-btn.confirm:hover {
  background: color-mix(in srgb, var(--color-indicator) 26%, var(--color-surface));
}
</style>
