<script setup lang="ts">
import { useGameStore } from '../stores/game';

const game = useGameStore();
</script>

<template>
  <section class="settings-view">
    <div class="settings-content">
      <div class="section">
        <h3>基本配置（emuera.config）</h3>
        <div class="setting-row">
          <span class="setting-label">履歴ログの行数</span>
          <span class="setting-value">{{ game.maxLog ?? '—' }}</span>
        </div>
      </div>
      <div class="section">
        <h3>その他</h3>
        <p class="placeholder-text">
          この他の設定項目は順次追加予定。
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
  text-align: left;
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
