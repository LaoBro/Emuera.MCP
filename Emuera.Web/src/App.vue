<script setup lang="ts">
import { useUiStore } from './stores/ui';
import ConnectionPanel from './components/ConnectionPanel.vue';
import DebugView from './views/DebugView.vue';
import TerminalView from './views/TerminalView.vue';

const ui = useUiStore();
</script>

<template>
  <div class="app-root">
    <header class="app-header">
      <ConnectionPanel />
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
