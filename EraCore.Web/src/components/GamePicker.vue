<script setup lang="ts">
import { computed } from 'vue';
import { useGameStore, mapLoadGameErrorCode } from '../stores/game';
import { useConnectionStore } from '../stores/connection';
import { useGameDirInput } from '../composables/useGameDirInput';

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
 *
 * T-025 D14：输入框预填 + gameDir 同步由 useGameDirInput composable 统一处理——
 * 快速重开失败清空 gameDir 时输入框自动清空。
 */
const game = useGameStore();
const conn = useConnectionStore();
const { input } = useGameDirInput();

const isLoading = computed(() => game.reloadStatus === 'loading');
const loadBlocked = computed(() => isLoading.value || !conn.canMutateLifecycle);
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
  if (!dir || loadBlocked.value) return;
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
      :disabled="loadBlocked"
      :title="!conn.canMutateLifecycle ? '旁观中，请先接管再换游戏' : undefined"
      @keyup.enter="onLoad"
    />
    <button
      class="btn-primary"
      :disabled="loadBlocked || !input.trim()"
      :title="!conn.canMutateLifecycle ? '旁观中，请先接管再换游戏' : undefined"
      @click="onLoad"
    >
      {{ isLoading ? '加载中…' : '加载' }}
    </button>
    <span v-if="!conn.canMutateLifecycle" class="spectator-hint">旁观中，请先接管再换游戏</span>
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
  gap: var(--space-2);
  font-size: var(--font-size-md);
  flex-wrap: wrap;
  color: var(--color-text);
}
.picker-label {
  color: var(--color-text-muted);
  white-space: nowrap;
}
.dir-input {
  background: var(--color-bg);
  color: var(--color-text);
  border: 1px solid var(--color-border);
  padding: 3px var(--space-2);
  border-radius: var(--radius-control);
  font-family: var(--font-mono);
  font-size: var(--font-size-sm);
  min-width: 280px;
}
.dir-input:disabled {
  opacity: 0.6;
}
.spectator-hint {
  color: var(--color-warning);
  font-size: var(--font-size-sm);
}
.current-dir {
  color: var(--color-success);
  font-family: var(--font-mono);
  font-size: var(--font-size-sm);
}
.error-banner {
  display: flex;
  align-items: center;
  gap: var(--space-2);
  background: color-mix(in srgb, var(--color-error) 14%, var(--color-surface));
  color: var(--color-error);
  border: 1px solid color-mix(in srgb, var(--color-error) 45%, var(--color-border));
  padding: var(--space-1) var(--space-2);
  border-radius: var(--radius-control);
  width: 100%;
}
.error-text {
  flex: 1;
  font-size: var(--font-size-sm);
  word-break: break-all;
}
.dismiss-btn {
  background: transparent;
  color: inherit;
  border: none;
  cursor: pointer;
  font-size: 16px;
  line-height: 1;
  padding: 0 var(--space-1);
}
.dismiss-btn:hover {
  opacity: 0.8;
}
</style>
