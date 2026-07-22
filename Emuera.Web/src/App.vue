<script setup lang="ts">
import { onMounted, computed } from 'vue';
import { useUiStore } from './stores/ui';
import { useGameStore } from './stores/game';
import { initAppState } from './composables/useAppInit';
import { isMauiEnvironment, loadGameFromPath } from './lib/mauiBridge';
import ConnectionPanel from './components/ConnectionPanel.vue';
import GamePicker from './components/GamePicker.vue';
import GamePickerMobile from './components/GamePickerMobile.vue';
import MauiGamePicker from './components/MauiGamePicker.vue';
import DebugView from './views/DebugView.vue';
import TerminalView from './views/TerminalView.vue';

const ui = useUiStore();
const game = useGameStore();

/**
 * T-025 D9 rev：App 挂载初始化——逻辑提取到 initAppState() 便于单测。
 * 见 composables/useAppInit.ts 的详细文档。
 */
onMounted(() => initAppState());

/**
 * Issue 07 / spec ID11：MAUI 模式下隐藏 HTTP 模式的连接面板和游戏目录选择器——
 * 改用 MauiGamePicker（原生文件夹选择器按钮）。
 */
const isMaui = isMauiEnvironment();

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
</script>

<template>
  <div class="app-root">
    <header class="app-header">
      <ConnectionPanel v-if="!isMaui" />
      <!-- Issue 05：游戏选择器，按平台条件渲染（MAUI 模式下隐藏——spec ID11） -->
      <GamePickerMobile v-if="!isMaui && ui.platform === 'android'" />
      <GamePicker v-else-if="!isMaui" />
      <!-- issue 09：MAUI 模式下用原生文件夹选择器替代文本输入 + 加载按钮 -->
      <MauiGamePicker v-if="isMaui" />
      <!-- T-025 D14：快速重开按钮——游戏运行/结束时显示，一键重载同目录（MAUI 模式下隐藏） -->
      <button
        v-if="canQuickRestart"
        class="quick-restart-btn"
        :disabled="isRestarting"
        :title="`重开当前游戏：${game.gameDir ?? ''}`"
        @click="onQuickRestart"
      >
        {{ isRestarting ? '重开中…' : '快速重开' }}
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
          :class="{ active: ui.currentView === 'terminal' }"
          @click="ui.switchView('terminal')"
        >
          Terminal
        </button>
      </nav>
    </header>
    <main class="app-main">
      <DebugView v-if="ui.currentView === 'debug'" />
      <TerminalView v-else />
    </main>
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
.app-main {
  flex: 1;
  overflow: hidden;
  display: flex;
}
</style>
