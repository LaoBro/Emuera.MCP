<script setup lang="ts">
import { useGameStore } from '../stores/game';

const game = useGameStore();
</script>

<template>
  <section class="settings-view">
    <div class="settings-content">
      <div class="section">
        <h3>显示设置</h3>
        <div class="setting-row">
          <span class="setting-label">历史记录行数</span>
          <span class="setting-value">5000</span>
          <span class="setting-hint">来自游戏配置（emuera.config MaxLog）</span>
        </div>
        <div class="setting-row">
          <span class="setting-label">缩放比例</span>
          <div class="setting-controls">
            <button
              :disabled="game.isMinScale"
              @click="game.setScale(game.effectiveScale - 0.1)"
            >−</button>
            <span class="setting-value">{{ Math.round(game.effectiveScale * 100) }}%</span>
            <button
              :disabled="game.isMaxScale"
              @click="game.setScale(game.effectiveScale + 0.1)"
            >+</button>
          </div>
        </div>
      </div>
      <div class="section">
        <h3>游戏配置（储存后修改 emuera.config）</h3>
        <p class="placeholder-text">
          配置项编辑将在后续版本实现。
        </p>
      </div>
    </div>
  </section>
</template>

<style scoped>
.settings-view {
  width: 100%;
  height: 100%;
  background: #1e1e1e;
  overflow-y: auto;
}
.settings-content {
  max-width: 600px;
  margin: 0 auto;
  padding: 24px;
  display: flex;
  flex-direction: column;
  gap: 24px;
}
.section {
  background: #252526;
  border: 1px solid #3c3c3c;
  border-radius: 4px;
  overflow: hidden;
}
h3 {
  margin: 0;
  padding: 10px 16px;
  font-size: 13px;
  font-weight: 600;
  color: #9cdcfe;
  background: #2d2d2d;
  border-bottom: 1px solid #3c3c3c;
}
.setting-row {
  display: flex;
  align-items: center;
  padding: 12px 16px;
  gap: 12px;
  border-bottom: 1px solid #333;
}
.setting-row:last-child {
  border-bottom: none;
}
.setting-label {
  color: #d4d4d4;
  font-size: 13px;
  min-width: 120px;
}
.setting-value {
  color: #dcdcaa;
  font-size: 14px;
  font-weight: 600;
  font-variant-numeric: tabular-nums;
  min-width: 48px;
  text-align: center;
}
.setting-hint {
  color: #888;
  font-size: 12px;
  margin-left: auto;
}
.setting-controls {
  display: flex;
  align-items: center;
  gap: 8px;
}
.setting-controls button {
  background: #333;
  color: #ccc;
  border: 1px solid #444;
  padding: 2px 10px;
  border-radius: 3px;
  cursor: pointer;
  font-size: 14px;
  font-family: inherit;
  line-height: 1.4;
}
.setting-controls button:disabled {
  opacity: 0.4;
  cursor: not-allowed;
}
.setting-controls button:hover:not(:disabled) {
  background: #444;
}
.placeholder-text {
  color: #888;
  font-size: 13px;
  padding: 24px 16px;
  margin: 0;
  text-align: center;
  font-style: italic;
}
</style>
