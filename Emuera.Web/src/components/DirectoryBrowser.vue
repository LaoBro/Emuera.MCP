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
      // 弹窗打开——清旧数据并拉取当前 mainGameDir 的子目录
      game.clearDirectoryList();
      listDirectories(game.mainGameDir);
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
        <button class="db-btn cancel" @click="onCancel">取消</button>
        <button class="db-btn confirm" @click="onConfirm">选择此目录</button>
      </footer>
    </div>
  </div>
</template>

<style scoped>
.directory-browser-overlay {
  position: fixed;
  inset: 0;
  background: rgba(0, 0, 0, 0.6);
  display: flex;
  align-items: center;
  justify-content: center;
  z-index: 1000;
  padding: 16px;
}
.directory-browser-modal {
  background: #252526;
  border: 1px solid #3c3c3c;
  border-radius: 6px;
  display: flex;
  flex-direction: column;
  max-width: 560px;
  width: 100%;
  max-height: 80vh;
  overflow: hidden;
}
.db-header {
  padding: 12px 16px;
  border-bottom: 1px solid #3c3c3c;
  background: #2d2d30;
}
.db-title {
  margin: 0 0 4px 0;
  font-size: 14px;
  font-weight: 600;
  color: #e0e0e0;
}
.db-current {
  font-size: 12px;
  color: #9aa0a6;
  font-family: ui-monospace, Consolas, monospace;
  word-break: break-all;
}
.db-body {
  flex: 1;
  overflow-y: auto;
  padding: 8px 0;
}
.db-up {
  display: block;
  width: 100%;
  text-align: left;
  background: transparent;
  color: #4ec9b0;
  border: none;
  padding: 8px 16px;
  cursor: pointer;
  font-size: 14px;
  font-family: ui-monospace, Consolas, monospace;
}
.db-up:hover {
  background: #2d2d30;
}
.db-empty {
  padding: 16px;
  text-align: center;
  color: #6a6a6a;
  font-size: 13px;
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
  color: #dcdcdc;
  border: none;
  padding: 8px 16px;
  cursor: pointer;
  font-size: 14px;
  font-family: ui-monospace, Consolas, monospace;
}
.db-item:hover {
  background: #2d2d30;
  color: #fff;
}
.db-footer {
  display: flex;
  justify-content: flex-end;
  gap: 8px;
  padding: 12px 16px;
  border-top: 1px solid #3c3c3c;
  background: #2d2d30;
}
.db-btn {
  background: #333;
  color: #ccc;
  border: 1px solid #444;
  padding: 6px 16px;
  border-radius: 3px;
  cursor: pointer;
  font-size: 13px;
}
.db-btn:hover {
  background: #444;
}
.db-btn.confirm {
  background: #0e639c;
  color: #fff;
  border-color: #0e639c;
}
.db-btn.confirm:hover {
  background: #1177bb;
}
</style>
