<script setup lang="ts">
import { ref } from 'vue';
import { useConnectionStore } from '../stores/connection';
import { useGameStore } from '../stores/game';
import { isMauiEnvironment } from '../lib/mauiBridge';

const conn = useConnectionStore();
const game = useGameStore();
const isMaui = isMauiEnvironment();
const inputValue = ref<string>('');

function onSend(): void {
  const v = inputValue.value;
  if (!v) return;
  conn.sendInput(v);
  // 不清空输入框，方便反复发送同一输入调试
}
</script>

<template>
  <section class="debug-view">
    <div class="left-pane">
      <!-- MAUI 模式无 HTTP/WS，无 snapshot 概念——隐藏 snapshot 区，仅保留最新帧区。
           HTTP 模式 snapshot 由 WS onopen 后 GET /snapshot 拉取，晚加入者画面恢复用。 -->
      <div v-if="!isMaui" class="snapshot-section">
        <h3>最近 Snapshot（GET /snapshot）</h3>
        <pre v-if="game.lastSnapshot" class="raw-json snapshot-json">{{
          JSON.stringify(game.lastSnapshot, null, 2)
        }}</pre>
        <div v-else class="empty">尚未拉取 snapshot（连接建立后由 onopen 触发）</div>
      </div>
      <div class="latest-frame-section">
        <h3>最新 WS 帧（原始 JSON）</h3>
        <pre v-if="game.lastTurnJson" class="raw-json">{{ game.lastTurnJson }}</pre>
        <div v-else class="empty">等待 WS 帧…</div>
        <p v-if="game.lastError" class="error">解析错误：{{ game.lastError }}</p>
      </div>
    </div>
    <div class="right-pane">
      <div class="input-section">
        <h3>提交输入</h3>
        <div class="input-row">
          <input
            v-model="inputValue"
            class="text-input"
            type="text"
            placeholder="输入值（如 0）"
            :disabled="conn.status !== 'connected'"
            @keyup.enter="onSend"
          />
          <button
            class="send-btn"
            :disabled="conn.status !== 'connected'"
            @click="onSend"
          >
            发送
          </button>
        </div>
        <p class="hint">帧格式：<code>{"type":"input","value":"..."}</code></p>
      </div>
      <div class="history-section">
        <h3>历史帧（{{ game.turnHistory.length }}）</h3>
        <div class="history-list">
          <div
            v-for="(frame, idx) in game.turnHistory.slice().reverse()"
            :key="idx"
            class="history-item"
          >
            <span class="frame-idx">#{{ game.turnHistory.length - idx }}</span>
            <pre class="frame-json">{{ frame }}</pre>
          </div>
        </div>
      </div>
    </div>
  </section>
</template>

<style scoped>
.debug-view {
  display: flex;
  width: 100%;
  height: 100%;
  gap: 1px;
  background: #3c3c3c;
}
.left-pane,
.right-pane {
  flex: 1;
  background: #1e1e1e;
  display: flex;
  flex-direction: column;
  overflow: hidden;
}
.snapshot-section,
.latest-frame-section {
  display: flex;
  flex-direction: column;
  overflow: hidden;
}
.snapshot-section {
  flex: 0 0 45%;
  border-bottom: 2px solid #3c3c3c;
}
.latest-frame-section {
  flex: 1;
  min-height: 0;
}
h3 {
  margin: 0;
  padding: 8px 12px;
  background: #252526;
  font-size: 13px;
  color: #9cdcfe;
  border-bottom: 1px solid #3c3c3c;
  flex-shrink: 0;
}
.snapshot-section h3 {
  color: #dcdcaa;
}
.raw-json,
.frame-json {
  margin: 0;
  padding: 12px;
  font-family: ui-monospace, Consolas, 'Courier New', monospace;
  font-size: 12px;
  white-space: pre-wrap;
  word-break: break-all;
  overflow: auto;
  color: #d4d4d4;
}
.raw-json {
  flex: 1;
  overflow: auto;
}
.snapshot-json {
  color: #dcdcaa;
  background: #1a1a1a;
}
.empty {
  padding: 16px;
  color: #888;
  font-style: italic;
}
.error {
  margin: 0;
  padding: 8px 12px;
  color: #f48771;
  background: #5a1d1d;
  font-size: 12px;
  flex-shrink: 0;
}
.right-pane {
  display: flex;
  flex-direction: column;
}
.input-section {
  padding: 0;
  border-bottom: 1px solid #3c3c3c;
  flex-shrink: 0;
}
.input-row {
  display: flex;
  gap: 6px;
  padding: 10px 12px;
}
.text-input {
  flex: 1;
  background: #1e1e1e;
  color: #e0e0e0;
  border: 1px solid #3c3c3c;
  padding: 4px 8px;
  border-radius: 3px;
  font-family: ui-monospace, Consolas, monospace;
  font-size: 13px;
}
.text-input:disabled {
  opacity: 0.5;
}
.send-btn {
  background: #0e639c;
  color: #fff;
  border: none;
  padding: 4px 16px;
  border-radius: 3px;
  cursor: pointer;
  font-size: 13px;
}
.send-btn:disabled {
  background: #444;
  cursor: not-allowed;
}
.hint {
  margin: 0;
  padding: 4px 12px 10px;
  color: #888;
  font-size: 11px;
}
.hint code {
  color: #ce9178;
  background: #2d2d2d;
  padding: 1px 4px;
  border-radius: 2px;
}
.history-section {
  flex: 1;
  display: flex;
  flex-direction: column;
  overflow: hidden;
}
.history-list {
  flex: 1;
  overflow: auto;
}
.history-item {
  display: flex;
  flex-direction: column;
  border-bottom: 1px solid #2d2d2d;
}
.frame-idx {
  padding: 2px 12px;
  font-size: 11px;
  color: #569cd6;
  font-family: ui-monospace, Consolas, monospace;
  background: #252526;
}
.frame-json {
  font-size: 11px;
  max-height: 120px;
  overflow: auto;
  padding: 6px 12px;
  color: #888;
}
</style>
