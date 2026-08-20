<script setup lang="ts">
import { ref, computed } from 'vue';
import { useGameStore } from '../stores/game';
import { useUiStore } from '../stores/ui';
import { mauiGameLibrarySource } from '../lib/gameLibrary';
import GameLibraryView from './GameLibraryView.vue';
import PopupMenu, { type PopupMenuItem } from './PopupMenu.vue';

/**
 * MauiGameList — MAUI 游戏选择页（PickerShell）。
 *
 * 现在只是 GameLibraryView（与 Web 共用一份设计）的薄 wrapper——
 * 平台差异只剩右上角 ⋮ 菜单（更改目录 / 重新扫描 / 主题）：
 * - 数据获取（扫描/浏览/加载）经 mauiGameLibrarySource（C# 桥接）
 * - 「更改主目录」走原生 FolderPicker（Windows）/ SAF（Android）
 */
const game = useGameStore();
const ui = useUiStore();

/** 顶部 ⋮ 菜单展开状态。 */
const showMenu = ref(false);

/** 目录操作菜单项——扫描中禁用；点击后由组件自动关闭。 */
const dirMenuItems = computed<PopupMenuItem[]>(() => [
  {
    type: 'item',
    id: 'change-dir',
    label: isScanning.value ? '扫描中…' : '更改目录...',
    disabled: isScanning.value,
    onClick: onChangeMainDir,
  },
  {
    type: 'item',
    id: 'rescan',
    label: '重新扫描游戏',
    disabled: isScanning.value,
    onClick: onRescan,
  },
  { type: 'separator' },
  {
    type: 'item',
    id: 'theme',
    icon: ui.theme === 'dark' ? 'sun' : 'moon',
    label: ui.theme === 'dark' ? '亮色主题' : '暗色主题',
    onClick: () => ui.toggleTheme(),
  },
]);

/** 当前是否扫描中——菜单项禁用依据。 */
const isScanning = computed(() => game.scanStatus === 'scanning');

/** 点击「更改目录」——经 source 走原生选择器（Windows FolderPicker / Android SAF）。 */
function onChangeMainDir(): void {
  game.clearMauiError();
  void mauiGameLibrarySource.pickMainDir();
}

/** 重新扫描——以当前主目录为起点重扫（未选目录时退化为更改目录）。 */
function onRescan(): void {
  const dir = game.scanRootDir ?? game.mainGameDir;
  if (!dir) {
    onChangeMainDir();
    return;
  }
  game.setMainGameDir(dir);
  void mauiGameLibrarySource.scan(dir);
}
</script>

<template>
  <GameLibraryView :source="mauiGameLibrarySource">
    <template #appbar-actions>
      <button
        type="button"
        class="appbar-menu-btn"
        :aria-label="showMenu ? '关闭目录操作菜单' : '目录操作菜单'"
        aria-haspopup="true"
        :aria-expanded="showMenu"
        @mousedown.stop
        @click="showMenu = !showMenu"
      >
        <svg viewBox="0 0 24 24" aria-hidden="true">
          <path d="M12 5v.01M12 12v.01M12 19v.01" />
        </svg>
      </button>
      <Transition name="popup">
        <PopupMenu
          v-if="showMenu"
          :items="dirMenuItems"
          @close="showMenu = false"
        />
      </Transition>
    </template>
  </GameLibraryView>
</template>

<style scoped>
/* 48px 圆形图标按钮（原 MauiGameList 样式，保留为插槽内容） */
.appbar-menu-btn {
  width: 48px;
  height: 48px;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  background: transparent;
  color: var(--color-text);
  border: none;
  border-radius: 9999px;
  cursor: pointer;
  transition: background-color var(--motion-fast);
}
.appbar-menu-btn:hover {
  background: var(--state-layer-hover);
}
.appbar-menu-btn:active {
  background: var(--state-layer-active);
}
.appbar-menu-btn:focus-visible {
  background: var(--state-layer-focus);
  outline: 2px solid var(--color-focus);
  outline-offset: -2px;
}
.appbar-menu-btn svg {
  width: 24px;
  height: 24px;
  fill: none;
  stroke: currentColor;
  stroke-width: 2;
  stroke-linecap: round;
  stroke-linejoin: round;
}
</style>
