<script setup lang="ts">
import { onMounted, onUnmounted, ref, computed, watch, defineAsyncComponent } from 'vue';
import { useUiStore } from './stores/ui';
import { useGameStore } from './stores/game';
import { useConnectionStore } from './stores/connection';
import { initAppState } from './composables/useAppInit';
import { startGameStatusMonitor } from './composables/useGameStatusMonitor';
import { isMauiEnvironment, loadGameFromPath, exitGame as exitGameBridge } from './lib/mauiBridge';
import AppShell from './components/AppShell.vue';
import AppBar from './components/AppBar.vue';
import PopupMenu, { type PopupMenuItem } from './components/PopupMenu.vue';
import ConfirmDialog from './components/ConfirmDialog.vue';
import SegmentedNav from './components/SegmentedNav.vue';
import ConnectionPanel from './components/ConnectionPanel.vue';
import GamePicker from './components/GamePicker.vue';
import GamePickerMobile from './components/GamePickerMobile.vue';
import MauiGameList from './components/MauiGameList.vue';
import TerminalView from './views/TerminalView.vue';

const DebugView = defineAsyncComponent(() => import('./views/DebugView.vue'));
const SettingsView = defineAsyncComponent(() => import('./views/SettingsView.vue'));

const ui = useUiStore();
const game = useGameStore();
const conn = useConnectionStore();

function onControlHotkey(e: KeyboardEvent): void {
  if (!(e.ctrlKey || e.metaKey) || e.key.toLowerCase() !== 't') return;
  if (isMauiEnvironment() || !conn.isSpectator) return;
  e.preventDefault();
  void conn.acquireControl();
}

/**
 * T-025 D9 rev：App 挂载初始化——逻辑提取到 initAppState() 便于单测。
 * MAUI 额外启动游戏静默监控（useGameStatusMonitor）。
 */
onMounted(() => {
  initAppState();
  startGameStatusMonitor();
  window.addEventListener('keydown', onControlHotkey);
});

onUnmounted(() => {
  window.removeEventListener('keydown', onControlHotkey);
});

const isMaui = isMauiEnvironment();

/**
 * MAUI 全屏游戏选择界面可见性——游戏未加载时（gameDir 为 null + serverState 空闲）
 * 显示全屏 MauiGameList；游戏运行中或 HTTP 模式显示标准 AppShell 布局。
 */
const showMauiGameList = computed(() =>
  isMaui && !game.gameDir && game.serverState === 'Idle',
);

/** 「快速重开」可见性：HTTP 非 Idle / MAUI 已选目录。 */
const canQuickRestart = computed(() =>
  isMaui ? !!game.gameDir : game.serverState !== 'Idle',
);
const isRestarting = computed(() => game.reloadStatus === 'loading');

/** MAUI 模式「退出游戏」可见性。 */
const canExitGame = computed(
  () => isMaui && (game.serverState !== 'Idle' || !!game.gameDir),
);
const isExiting = computed(() => game.exitStatus === 'exiting');

/** 退出确认对话框可见性。 */
const showExitConfirm = ref(false);

/** MAUI 全屏「更多」（⋮）菜单展开状态。 */
const showFloatMenu = ref(false);

/** Android 物理返回键（backButtonPressed 由 C# 投递）——层级语义（spec §6.1）：
 *  更多菜单打开 → 关闭菜单；退出确认打开 → 取消；游戏运行中 → 弹退出确认。 */
watch(() => game.backButtonPressedTick, () => {
  if (!isMaui) return;
  if (showFloatMenu.value) {
    showFloatMenu.value = false;
    return;
  }
  if (showExitConfirm.value) {
    onExitCancel();
    return;
  }
  if (isExiting.value) return;
  if (game.serverState === 'Idle' && !game.gameDir) return;
  showExitConfirm.value = true;
});

/** 快速重开——HTTP 走 game.quickRestart()；MAUI 投递 loadGameFromPath。 */
async function onQuickRestart(): Promise<void> {
  if (isRestarting.value) return;
  if (!isMaui && !conn.canMutateLifecycle) return;
  if (isMaui) {
    if (!game.gameDir) return;
    game.reset();
    loadGameFromPath(game.gameDir);
    return;
  }
  await game.quickRestart();
}

/** 点击「退出游戏」——弹确认对话框，不直接退出。 */
function onExitClick(): void {
  if (isExiting.value) return;
  showExitConfirm.value = true;
}

/** 确认退出——投递 exitGame 消息，C# Dispose + 重建 host。 */
function onExitConfirm(): void {
  showExitConfirm.value = false;
  if (!game.beginExitGame()) return; // 二次进入保护
  exitGameBridge();
}

/** 取消退出——关闭对话框，无副作用。 */
function onExitCancel(): void {
  showExitConfirm.value = false;
}

/**
 * spec §6.1：⌨ 键盘按钮 active 状态必须与实际输入栏状态一致，不得只按手动开关表现。
 * 与 TerminalView.showInputBar 同语义：手动唤出时 WaitInput 即显示；
 * 否则当前回合无按钮时显示（文本→输入行；AnyKey/EnterKey→提示行）。
 */
const keyboardActive = computed<boolean>(() => {
  const st = game.displayState.state;
  if (st !== 'WaitInput') return false;
  if (ui.manualInputVisible) return true;
  return !game.hasActiveButtons;
});

/** 「更多」菜单项（MAUI 游戏页）——快速重开、缩放、视图切换、退出（spec §6.1）。 */
const popupItems = computed<PopupMenuItem[]>(() => [
  {
    type: 'item',
    id: 'restart',
    label: isRestarting.value ? '重开中…' : '快速重开',
    icon: '⟳',
    disabled: !canQuickRestart.value || isRestarting.value,
    onClick: () => {
      void onQuickRestart();
    },
  },
  {
    type: 'zoom',
    percent: Math.round(game.effectiveScale * 100),
    canZoomOut: game.isMinScale,
    canZoomIn: game.isMaxScale,
    onZoomOut: () => game.setScale(game.effectiveScale - 0.1),
    onZoomReset: () => game.setScale(1),
    onZoomIn: () => game.setScale(game.effectiveScale + 0.1),
  },
  { type: 'separator' },
  {
    type: 'item',
    id: 'terminal',
    label: 'Terminal',
    icon: '▣',
    active: ui.currentView === 'terminal',
    onClick: () => {
      ui.switchView('terminal');
    },
  },
  {
    type: 'item',
    id: 'debug',
    label: 'Debug',
    icon: '{}',
    active: ui.currentView === 'debug',
    onClick: () => {
      ui.switchView('debug');
    },
  },
  {
    type: 'item',
    id: 'settings',
    label: 'Settings',
    icon: '⚙',
    active: ui.currentView === 'settings',
    onClick: () => {
      ui.switchView('settings');
    },
  },
  { type: 'separator' },
  {
    type: 'item',
    id: 'exit',
    label: isExiting.value ? '退出中…' : '退出游戏',
    icon: '⏻',
    danger: true,
    disabled: !canExitGame.value || isExiting.value,
    onClick: () => {
      onExitClick();
    },
  },
]);
</script>

<template>
  <AppShell :scrollable="showMauiGameList">
    <!-- 页面过渡动画（方案一+四）：out-in 模式 + scale(0.97) + fade -->
    <Transition name="page" mode="out-in">
      <MauiGameList v-if="showMauiGameList" key="game-list" />
      <div v-else class="game-view-wrapper" key="game-view">
        <!-- 桌面：应用栏（连接状态 + 缩放/快速重开 + SegmentedNav） -->
        <AppBar v-if="!isMaui">
          <template #left>
            <ConnectionPanel />
          </template>
          <template #actions>
            <div class="zoom-controls">
              <button
                class="icon-btn sm"
                :disabled="game.isMinScale"
                aria-label="缩小"
                title="缩小"
                @click="game.setScale(game.effectiveScale - 0.1)"
              >−</button>
              <span class="zoom-label tabular-nums">{{ Math.round(game.effectiveScale * 100) }}%</span>
              <button
                class="icon-btn sm"
                :disabled="game.isMaxScale"
                aria-label="放大"
                title="放大"
                @click="game.setScale(game.effectiveScale + 0.1)"
              >+</button>
            </div>
            <button
              v-if="canQuickRestart"
              class="btn-outline action-btn"
              :disabled="isRestarting || !conn.canMutateLifecycle"
              :title="!conn.canMutateLifecycle ? '旁观中，请先接管再重开' : `重开当前游戏：${game.gameDir ?? ''}`"
              @click="onQuickRestart"
            >
              {{ isRestarting ? '重开中…' : '快速重开' }}
            </button>
          </template>
          <template #nav>
            <SegmentedNav />
          </template>
        </AppBar>

        <!-- 桌面：目录选择条（spec §5.5——路径输入行不塞进应用栏） -->
        <div v-if="!isMaui" class="picker-bar">
          <GamePickerMobile v-if="ui.platform === 'android'" />
          <GamePicker v-else />
        </div>

        <!-- MAUI 全屏游戏页：两个常驻无边框图标（spec §6.1），fixed 不占布局。
             DOM 顺序在 main 之前——满足 §9 Tab 顺序「更多 → 主要内容」。 -->
        <div v-if="isMaui" class="game-shell-controls">
          <button
            class="icon-btn"
            :class="{ active: keyboardActive }"
            aria-label="手动输入"
            title="手动输入"
            @click="ui.toggleManualInput()"
          >⌨</button>
          <button
            class="icon-btn"
            :class="{ active: showFloatMenu }"
            aria-label="更多操作"
            title="更多操作"
            @mousedown.stop
            @click="showFloatMenu = !showFloatMenu"
          >⋮</button>
        </div>

        <Transition name="popup">
          <PopupMenu
            v-if="isMaui && showFloatMenu"
            :items="popupItems"
            @close="showFloatMenu = false"
          />
        </Transition>

        <main class="app-main">
          <DebugView v-if="ui.currentView === 'debug'" />
          <SettingsView v-else-if="ui.currentView === 'settings'" />
          <TerminalView v-else />
        </main>
      </div>
    </Transition>

    <!-- 退出确认（Android 风格 Alert Dialog，spec §6.1）——挂载于 AppShell #dialogs -->
    <template #dialogs>
      <ConfirmDialog
        :visible="showExitConfirm"
        title="退出游戏？"
        message="退出后未保存的进度将丢失。"
        confirm-label="退出"
        cancel-label="取消"
        :danger="true"
        @confirm="onExitConfirm"
        @cancel="onExitCancel"
      />
    </template>

    <!-- 游戏静默状态提示（MAUI）——纯展示、pointer-events:none 不挡交互 -->
    <div
      v-if="isMaui && game.gameStatusHint !== null"
      class="status-hint"
      :class="game.gameStatusHint"
    >
      {{ game.gameStatusHint === 'running' ? '游戏运行中…' : '游戏已停止' }}
    </div>
  </AppShell>
</template>

<style scoped>
.app-main {
  flex: 1;
  overflow: hidden;
  display: flex;
  flex-direction: column;
}

/* 桌面目录选择条——路径输入 + 加载按钮成组（spec §5.5） */
.picker-bar {
  flex-shrink: 0;
  display: flex;
  align-items: center;
  gap: var(--space-3);
  padding: var(--space-2) var(--space-4);
  background: var(--color-bg);
  border-bottom: 1px solid var(--color-border);
}

/* 通用图标按钮（spec §3.3）：方形触控目标、无边框、无背景 */
.icon-btn {
  width: var(--touch-target);
  height: var(--touch-target);
  display: inline-flex;
  align-items: center;
  justify-content: center;
  background: transparent;
  color: var(--color-text);
  border: none;
  border-radius: var(--radius-control);
  cursor: pointer;
  font-size: 18px;
  font-family: var(--font-ui);
  transition: background var(--motion-fast), color var(--motion-fast);
}
.icon-btn.sm {
  width: 32px;
  height: 32px;
  font-size: 14px;
}
.icon-btn:hover:not(:disabled) {
  background: var(--color-surface-raised);
}
.icon-btn:active:not(:disabled) {
  background: var(--color-surface);
}
.icon-btn.active {
  color: var(--color-indicator);
}
.icon-btn:disabled {
  opacity: 0.4;
  cursor: not-allowed;
}

.zoom-controls {
  display: flex;
  align-items: center;
  gap: var(--space-1);
}
.zoom-label {
  font-size: var(--font-size-sm);
  color: var(--color-text-muted);
  min-width: 40px;
  text-align: center;
}

/* 桌面「快速重开」——.btn-outline 基础上略降高度，与应用栏紧凑 */
.action-btn {
  min-height: 32px;
}

/* MAUI 全屏游戏页：两个常驻无边框图标（spec §6.1）——透明度一致、固定位置、≥48px 点击区 */
.game-shell-controls {
  position: fixed;
  top: calc(var(--space-2) + env(safe-area-inset-top));
  right: var(--space-2);
  display: flex;
  gap: var(--space-1);
  z-index: 50;
}
.game-shell-controls .icon-btn {
  border-radius: 9999px;
  background: transparent;
  color: var(--color-text);
}
.game-shell-controls .icon-btn:hover:not(:disabled),
.game-shell-controls .icon-btn:active:not(:disabled) {
  background: var(--color-control-hover);
  color: var(--color-text);
}
.game-shell-controls .icon-btn.active {
  color: var(--color-text);
}

/* 游戏静默状态提示（MAUI）——顶部半透明窄条，pointer-events:none */
.status-hint {
  position: fixed;
  top: calc(var(--space-3) + env(safe-area-inset-top));
  left: 50%;
  transform: translateX(-50%);
  z-index: 60;
  padding: var(--space-1) var(--space-3);
  border-radius: var(--radius-control);
  font-size: var(--font-size-sm);
  color: var(--color-text);
  background: color-mix(in srgb, var(--color-surface) 80%, transparent);
  border: none;
  pointer-events: none;
  white-space: nowrap;
}
.status-hint.stopped {
  color: var(--color-error);
}

/* 游戏视图包裹层——在 AppShell flex 布局中撑满剩余空间，与 MauiGameList 平级 */
.game-view-wrapper {
  display: flex;
  flex-direction: column;
  flex: 1;
  overflow: hidden;
}

/* 页面过渡动画（方案一+四）：opacity + scale + --page-duration 时长 */
.page-enter-active,
.page-leave-active {
  transition: opacity var(--page-duration) var(--fx-curve),
              transform var(--page-duration) var(--fx-curve);
}
.page-enter-from {
  opacity: 0;
  transform: scale(var(--page-scale));
}
.page-leave-to {
  opacity: 0;
  transform: scale(var(--page-scale));
}
</style>
