<script setup lang="ts">
import { ref, computed } from 'vue';
import { useGameStore, mapLoadGameErrorCode } from '../stores/game';

/**
 * GamePicker（桌面版）——路径文本输入框 + "加载"按钮。
 *
 * 浏览器安全限制不暴露真实文件系统路径，故不能用 <input type="file" webkitdirectory>
 * 获取真实路径。用户手动输入路径（如 `D:\games\mygame`），提交后调 loadGame(dir)。
 *
 * 状态：
 * - 输入框默认显示当前 gameDir（若已加载）
 * - reloadStatus='loading' 时禁用输入框 + 按钮，显示"加载中…"
 * - loadGameError 存在时显示结构化错误提示，可关闭
 */
const game = useGameStore();
const input = ref<string>(game.gameDir ?? '');

const isLoading = computed(() => game.reloadStatus === 'loading');
const errorText = computed(() => {
  if (!game.loadGameError) return null;
  const base = mapLoadGameErrorCode(game.loadGameError.code);
  // LOAD_FAILED：追加 server 返回的具体 message，便于定位（Preload.Load 异常、未预期 catch 等）
  if (game.loadGameError.code === 'LOAD_FAILED' && game.loadGameError.message) {
    return `${base}：${game.loadGameError.message}`;
  }
  return base;
});

async function onLoad(): Promise<void> {
  const dir = input.value.trim();
  if (!dir || isLoading.value) return;
  await game.loadGame(dir);
}

function onDismissError(): void {
  game.clearLoadGameError();
}
</script>

<template>
  <div class="game-picker">
    <label class="picker-label" for="game-dir-input">游戏目录：</label>
    <input
      id="game-dir-input"
      v-model="input"
      class="dir-input"
      type="text"
      placeholder="例如：D:\games\mygame 或 ./test_game"
      :disabled="isLoading"
      @keyup.enter="onLoad"
    />
    <button class="load-btn" :disabled="isLoading || !input.trim()" @click="onLoad">
      {{ isLoading ? '加载中…' : '加载' }}
    </button>
    <span v-if="game.gameDir" class="current-dir">当前：{{ game.gameDir }}</span>
    <div v-if="errorText" class="error-banner">
      <span class="error-text">{{ errorText }}</span>
      <button class="dismiss-btn" @click="onDismissError">×</button>
    </div>
  </div>
</template>

<style scoped>
.game-picker {
  display: flex;
  align-items: center;
  gap: 8px;
  font-size: 13px;
  flex-wrap: wrap;
}
.picker-label {
  color: #ccc;
  white-space: nowrap;
}
.dir-input {
  background: #1e1e1e;
  color: #e0e0e0;
  border: 1px solid #3c3c3c;
  padding: 3px 8px;
  border-radius: 3px;
  font-family: ui-monospace, Consolas, monospace;
  font-size: 12px;
  min-width: 280px;
}
.dir-input:disabled {
  opacity: 0.6;
}
.load-btn {
  background: #0e639c;
  color: #fff;
  border: none;
  padding: 4px 12px;
  border-radius: 3px;
  cursor: pointer;
  font-size: 13px;
}
.load-btn:disabled {
  background: #3a3a3a;
  cursor: not-allowed;
}
.current-dir {
  color: #4ec9b0;
  font-family: ui-monospace, Consolas, monospace;
  font-size: 12px;
}
.error-banner {
  display: flex;
  align-items: center;
  gap: 8px;
  background: #5a1d1d;
  color: #f48771;
  border: 1px solid #7a2a2a;
  padding: 4px 8px;
  border-radius: 3px;
  width: 100%;
}
.error-text {
  flex: 1;
  font-size: 12px;
}
.dismiss-btn {
  background: transparent;
  color: #f48771;
  border: none;
  cursor: pointer;
  font-size: 16px;
  line-height: 1;
  padding: 0 4px;
}
.dismiss-btn:hover {
  color: #ffaaaa;
}
</style>
