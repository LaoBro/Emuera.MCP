<script setup lang="ts">
import { onMounted, ref, computed, watch, defineAsyncComponent } from 'vue';
import { useUiStore } from './stores/ui';
import { useGameStore } from './stores/game';
import { initAppState } from './composables/useAppInit';
import { isMauiEnvironment, loadGameFromPath, exitGame as exitGameBridge } from './lib/mauiBridge';
import ConnectionPanel from './components/ConnectionPanel.vue';
import GamePicker from './components/GamePicker.vue';
import GamePickerMobile from './components/GamePickerMobile.vue';
import MauiGameList from './components/MauiGameList.vue';
import TerminalView from './views/TerminalView.vue';

const DebugView = defineAsyncComponent(() => import('./views/DebugView.vue'));
const SettingsView = defineAsyncComponent(() => import('./views/SettingsView.vue'));

const ui = useUiStore();
const game = useGameStore();

/**
 * T-025 D9 rev：App 挂载初始化——逻辑提取到 initAppState() 便于单测。
 * 见 composables/useAppInit.ts 的详细文档。
 */
onMounted(() => initAppState());

/**
 * Issue 07 / spec ID11：MAUI 模式下隐藏 HTTP 模式的连接面板和游戏目录选择器——
 * 改用 MauiGameList（游戏列表 + 更改主目录 + 退出按钮）。
 */
const isMaui = isMauiEnvironment();

/**
 * game-library spec layout fix：MAUI 全屏游戏选择界面可见性——
 * 游戏未加载时（gameDir 为 null + serverState 空闲）显示全屏 MauiGameList，
 * 游戏运行中或 HTTP 模式显示标准 header + main 布局。
 */
const showMauiGameList = computed(() =>
  isMaui && !game.gameDir && game.serverState === 'Idle',
);

/**
 * 「快速重开」按钮可见性——按模式分流：
 * - HTTP 模式：serverState 非 Idle 时显示（有活跃 session 才能重开）
 * - MAUI 模式：gameDir 非空时显示（已选过目录才能重开同目录）
 */
const canQuickRestart = computed(() =>
  isMaui ? !!game.gameDir : game.serverState !== 'Idle',
);
const isRestarting = computed(() => game.reloadStatus === 'loading');

/**
 * game-library spec ID10：MAUI 模式「退出游戏」按钮可见性——
 * 游戏运行中（serverState != 'Idle' 或 gameDir 非空）时显示。
 */
const canExitGame = computed(
  () => isMaui && (game.serverState !== 'Idle' || !!game.gameDir),
);
const isExiting = computed(() => game.exitStatus === 'exiting');

/** 退出确认对话框可见性。 */
const showExitConfirm = ref(false);

/**
 * game-library spec ID10：Android 物理返回键——监听 backButtonPressedTick 自增后弹出退出确认。
 * backButtonPressed 消息由 MainPage.OnBackButtonPressed 投递，useAppInit 转发至此计数器。
 */
watch(() => game.backButtonPressedTick, () => {
  if (!isMaui) return;
  if (isExiting.value) return;
  if (game.serverState === 'Idle' && !game.gameDir) return;
  showExitConfirm.value = true;
});

/**
 * 快速重开 click——按模式分流：
 * - HTTP 模式：调 game.quickRestart()（disconnect → DELETE /session → POST /load-game → connect）
 * - MAUI 模式：调 loadGameFromPath(game.gameDir) 投递 {"type":"loadGame","path":...}
 *   让 C# OnReloadGame 重建 BridgeHost + Start
 */
async function onQuickRestart(): Promise<void> {
  if (isRestarting.value) return;
  if (isMaui) {
    if (!game.gameDir) return;
    // 清空旧显示状态——新游戏首帧到达前不残留旧画面
    game.reset();
    loadGameFromPath(game.gameDir);
    return;
  }
  await game.quickRestart();
}

/**
 * game-library spec ID10：用户点击「退出」按钮——弹确认对话框。
 * 不直接退出，避免误点丢失游戏进度。
 */
function onExitClick(): void {
  if (isExiting.value) return;
  showExitConfirm.value = true;
}

/**
 * game-library spec ID10：用户确认退出——投递 exitGame 消息让 C# Dispose + 重建 host。
 * store.beginExitGame 置 exitStatus='exiting'，UI 禁用退出按钮；
 * C# 回复 gameExited 后 useAppInit 调 completeExitGame 重置状态。
 */
function onExitConfirm(): void {
  showExitConfirm.value = false;
  if (!game.beginExitGame()) return; // 二次进入保护
  exitGameBridge();
}

/** 用户取消退出——关闭对话框，无副作用。 */
function onExitCancel(): void {
  showExitConfirm.value = false;
}
</script>

<template>
  <div class="app-root">
    <!-- game-library spec layout fix：MAUI 全屏游戏选择界面——游戏未加载时独占整个页面 -->
    <MauiGameList v-if="showMauiGameList" />

    <!-- 游戏运行中或 HTTP 模式：标准 header + main 布局 -->
    <template v-else>
      <header class="app-header">
        <ConnectionPanel v-if="!isMaui" />
        <!-- Issue 05：游戏选择器，按平台条件渲染（MAUI 模式下隐藏——spec ID11） -->
        <GamePickerMobile v-if="!isMaui && ui.platform === 'android'" />
        <GamePicker v-else-if="!isMaui" />
        <!-- T-025 D14：快速重开按钮——游戏运行/结束时显示，一键重载同目录 -->
        <button
          v-if="canQuickRestart"
        class="quick-restart-btn"
        :disabled="isRestarting"
        :title="`重开当前游戏：${game.gameDir ?? ''}`"
        @click="onQuickRestart"
      >
        {{ isRestarting ? '重开中…' : '快速重开' }}
      </button>
      <!-- game-library spec ID10：退出游戏按钮——MAUI 模式 + 游戏运行时显示 -->
      <button
        v-if="canExitGame"
        class="exit-game-btn"
        :disabled="isExiting"
        @click="onExitClick"
      >
        {{ isExiting ? '退出中…' : '退出' }}
      </button>
      <div class="zoom-controls">
        <button
          :disabled="game.isMinScale"
          title="缩小"
          @click="game.setScale(game.effectiveScale - 0.1)"
        >−</button>
        <span class="zoom-label">{{ Math.round(game.effectiveScale * 100) }}%</span>
        <button
          :disabled="game.isMaxScale"
          title="放大"
          @click="game.setScale(game.effectiveScale + 0.1)"
        >+</button>
      </div>
      <nav class="view-switch">
        <button
          :class="{ active: ui.currentView === 'debug' }"
          @click="ui.switchView('debug')"
        >
          Debug
        </button>
        <button
          :class="{ active: ui.currentView === 'settings' }"
          @click="ui.switchView('settings')"
        >
          Settings
        </button>
        <button
          :class="{ active: ui.currentView === 'terminal' }"
          @click="ui.switchView('terminal')"
        >
          Terminal
        </button>
      </nav>
    </header>
    <main class="app-main">
      <DebugView v-if="ui.currentView === 'debug'" />
      <SettingsView v-else-if="ui.currentView === 'settings'" />
      <TerminalView v-else />
    </main>

    </template>

    <!-- game-library spec ID10：退出确认对话框 -->
    <div v-if="showExitConfirm" class="confirm-overlay" @click.self="onExitCancel">
      <div class="confirm-modal">
        <div class="confirm-text">确认退出？未保存进度会丢失</div>
        <div class="confirm-actions">
          <button class="confirm-btn cancel" @click="onExitCancel">取消</button>
          <button class="confirm-btn ok" @click="onExitConfirm">确认退出</button>
        </div>
      </div>
    </div>
  </div>
</template>

<style scoped>
.app-root {
  display: flex;
  flex-direction: column;
  height: 100vh;
  font-family: system-ui, -apple-system, 'Segoe UI', Roboto, sans-serif;
  background: #1e1e1e;
  color: #e0e0e0;
}
.app-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 16px;
  padding: 8px 12px;
  background: #252526;
  border-bottom: 1px solid #3c3c3c;
  flex-wrap: wrap;
}
.view-switch {
  display: flex;
  gap: 4px;
}
.view-switch button {
  background: #333;
  color: #ccc;
  border: 1px solid #444;
  padding: 4px 12px;
  border-radius: 3px;
  cursor: pointer;
  font-size: 13px;
}
.view-switch button.active {
  background: #0e639c;
  color: #fff;
  border-color: #0e639c;
}
.zoom-controls {
  display: flex;
  align-items: center;
  gap: 4px;
  flex-shrink: 0;
}
.zoom-controls button {
  background: #333;
  color: #ccc;
  border: 1px solid #444;
  padding: 2px 8px;
  border-radius: 3px;
  cursor: pointer;
  font-size: 13px;
  font-family: inherit;
  line-height: 1.4;
}
.zoom-controls button:disabled {
  opacity: 0.4;
  cursor: not-allowed;
}
.zoom-controls button:hover:not(:disabled) {
  background: #444;
}
.zoom-label {
  font-size: 12px;
  color: #aaa;
  min-width: 36px;
  text-align: center;
  font-variant-numeric: tabular-nums;
}
.quick-restart-btn {
  background: #5a4a1d;
  color: #dcdcaa;
  border: 1px solid #6a5a2d;
  padding: 4px 12px;
  border-radius: 3px;
  cursor: pointer;
  font-size: 13px;
  white-space: nowrap;
}
.quick-restart-btn:hover:not(:disabled) {
  background: #6a5a2d;
}
.quick-restart-btn:disabled {
  opacity: 0.6;
  cursor: not-allowed;
}
.exit-game-btn {
  background: #5a1d1d;
  color: #f48771;
  border: 1px solid #7a2a2a;
  padding: 4px 12px;
  border-radius: 3px;
  cursor: pointer;
  font-size: 13px;
  white-space: nowrap;
}
.exit-game-btn:hover:not(:disabled) {
  background: #6a2d2d;
}
.exit-game-btn:disabled {
  opacity: 0.6;
  cursor: not-allowed;
}
.app-main {
  flex: 1;
  overflow: hidden;
  display: flex;
}
/* game-library spec ID10：退出确认对话框 */
.confirm-overlay {
  position: fixed;
  inset: 0;
  background: rgba(0, 0, 0, 0.6);
  display: flex;
  align-items: center;
  justify-content: center;
  z-index: 1000;
  padding: 16px;
}
.confirm-modal {
  background: #252526;
  border: 1px solid #3c3c3c;
  border-radius: 6px;
  padding: 16px 20px;
  max-width: 360px;
  width: 100%;
  display: flex;
  flex-direction: column;
  gap: 16px;
}
.confirm-text {
  font-size: 14px;
  color: #e0e0e0;
  text-align: center;
}
.confirm-actions {
  display: flex;
  justify-content: center;
  gap: 12px;
}
.confirm-btn {
  background: #333;
  color: #ccc;
  border: 1px solid #444;
  padding: 6px 16px;
  border-radius: 3px;
  cursor: pointer;
  font-size: 13px;
  min-width: 88px;
}
.confirm-btn:hover {
  background: #444;
}
.confirm-btn.ok {
  background: #5a1d1d;
  color: #f48771;
  border-color: #7a2a2a;
}
.confirm-btn.ok:hover {
  background: #6a2d2d;
}
</style>
