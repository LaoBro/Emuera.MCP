<script setup lang="ts">
import { onMounted } from 'vue';
import { useUiStore } from './stores/ui';
import { useGameStore, readGameDirFromStorage } from './stores/game';
import { useConnectionStore } from './stores/connection';
import ConnectionPanel from './components/ConnectionPanel.vue';
import GamePicker from './components/GamePicker.vue';
import GamePickerMobile from './components/GamePickerMobile.vue';
import DebugView from './views/DebugView.vue';
import TerminalView from './views/TerminalView.vue';

const ui = useUiStore();
const game = useGameStore();
const conn = useConnectionStore();

/**
 * Issue 05：App 挂载自动加载逻辑（spec L36）。
 *
 * 流程：
 * 1. GET /state → 拿 server 当前 gameDir
 * 2. 比对 server gameDir vs localStorage `emuera.gameDir`
 *   - 同 / localStorage 无值 → conn.connect()（不重启当前局；server 已有 session 直接重连）
 *   - 异 → game.loadGame(localStorage.emuera.gameDir)（disconnect → /load-game → connect）
 *
 * 失败容错：GET /state 失败（server 未启动）→ 不自动连，让用户用 ConnectionPanel 手动连
 *
 * Issue 12：GET /state 响应携带 windowWidth/fontSize/lineHeight——同步写入 store，
 * 驱动 TerminalDisplay 固定宽度布局。
 */
onMounted(async () => {
  const httpBase = conn.deriveHttpBase(conn.serverUrl);
  let serverGameDir: string | null = null;
  try {
    const resp = await fetch(`${httpBase}/state`);
    if (resp.status === 200) {
      const body = await resp.json();
      if (typeof body?.gameDir === 'string') serverGameDir = body.gameDir;
      // Issue 12：写入窗口布局元信息——server 始终返回这 3 个 int 字段
      game.setGameLayout({
        windowWidth: typeof body?.windowWidth === 'number' ? body.windowWidth : null,
        fontSize: typeof body?.fontSize === 'number' ? body.fontSize : null,
        lineHeight: typeof body?.lineHeight === 'number' ? body.lineHeight : null,
      });
    }
  } catch {
    // server 未启动——不自动连
    return;
  }

  const localGameDir = readGameDirFromStorage();

  if (localGameDir && localGameDir !== serverGameDir) {
    // localStorage 目录与 server 当前不同——切到 localStorage
    await game.loadGame(localGameDir);
  } else {
    // 同目录 / localStorage 无值——直接 connect
    await conn.connect();
  }
});
</script>

<template>
  <div class="app-root">
    <header class="app-header">
      <ConnectionPanel />
      <!-- Issue 05：游戏选择器，按平台条件渲染 -->
      <GamePickerMobile v-if="ui.platform === 'android'" />
      <GamePicker v-else />
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
.app-main {
  flex: 1;
  overflow: hidden;
  display: flex;
}
</style>
