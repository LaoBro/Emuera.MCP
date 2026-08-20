<script setup lang="ts">
import { ref, computed } from 'vue';
import { useGameStore, mapLoadGameErrorCode } from '../stores/game';
import { useConnectionStore } from '../stores/connection';
import { useGameDirInput } from '../composables/useGameDirInput';

/**
 * GamePickerMobile（安卓版）——"选择目录"按钮 → POST /native/pick-directory → loadGame(dir)。
 *
 * Issue 05 只做 stub 接线：C# `/native/pick-directory` 当前返
 *   `{platform:"web", supported:false, message:"Not implemented on this platform"}`
 * MAUI 阶段替换为 Android Storage Access Framework 实现后，前端代码无需修改。
 *
 * 当前 stub 行为：
 * - 点击"选择目录" → POST /native/pick-directory
 * - supported=true → 用返回的 path 调 loadGame(path)
 * - supported=false → 显示提示"此平台暂不支持原生选择，请手动输入路径"，回退到路径输入框
 * - 用户也可直接在路径输入框输入（fallback 路径）
 *
 * T-025 D14：输入框预填 + gameDir 同步由 useGameDirInput composable 统一处理。
 */
const game = useGameStore();
const conn = useConnectionStore();
const { input } = useGameDirInput();
const pickerMessage = ref<string | null>(null);

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

async function onPickDirectory(): Promise<void> {
  if (loadBlocked.value) return;
  pickerMessage.value = null;

  const httpBase = conn.deriveHttpBase(conn.serverUrl);
  try {
    const resp = await fetch(`${httpBase}/native/pick-directory`, { method: 'POST' });
    if (resp.status !== 200) {
      pickerMessage.value = `原生选择器返回 HTTP ${resp.status}`;
      return;
    }
    const body = await resp.json();
    if (body?.supported === true && typeof body?.path === 'string' && body.path) {
      // MAUI 实现后——拿到真实路径直接 loadGame
      input.value = body.path;
      await game.loadGame(body.path);
    } else {
      // stub 路径——显示提示，回退到手动输入
      pickerMessage.value =
        body?.message ?? '此平台暂不支持原生选择，请手动输入路径';
    }
  } catch (e) {
    pickerMessage.value = `原生选择器请求失败：${e instanceof Error ? e.message : String(e)}`;
  }
}

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
  <div class="game-picker-mobile">
    <button
      class="btn-primary pick-btn"
      :disabled="loadBlocked"
      :title="!conn.canMutateLifecycle ? '旁观中，请先接管再换游戏' : undefined"
      @click="onPickDirectory"
    >
      📁 选择目录
    </button>
    <span class="separator">或手动输入：</span>
    <input
      v-model="input"
      class="dir-input"
      type="text"
      placeholder="/sdcard/games/mygame"
      :disabled="loadBlocked"
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
    <div v-if="pickerMessage" class="picker-message">
      <span class="message-text">{{ pickerMessage }}</span>
      <button class="dismiss-btn" @click="pickerMessage = null">×</button>
    </div>
    <div v-if="errorText" class="error-banner">
      <span class="error-text">{{ errorText }}</span>
      <button class="dismiss-btn" @click="onDismissError">×</button>
    </div>
  </div>
</template>

<style scoped>
.game-picker-mobile {
  display: flex;
  align-items: center;
  gap: var(--space-2);
  font-size: var(--font-size-md);
  flex-wrap: wrap;
  color: var(--color-text);
}
/* pick-btn 触控略高于基准按钮（36px） */
.pick-btn {
  min-height: 36px;
}
.separator {
  color: var(--color-text-muted);
  font-size: var(--font-size-sm);
}
.spectator-hint {
  color: var(--color-warning);
  font-size: var(--font-size-sm);
}
.dir-input {
  background: var(--color-bg);
  color: var(--color-text);
  border: 1px solid var(--color-border);
  padding: 3px var(--space-2);
  border-radius: var(--radius-control);
  font-family: var(--font-mono);
  font-size: var(--font-size-sm);
  min-width: 220px;
  flex: 1;
}
.dir-input:disabled {
  opacity: 0.6;
}
.current-dir {
  color: var(--color-success);
  font-family: var(--font-mono);
  font-size: var(--font-size-sm);
  width: 100%;
}
.picker-message {
  display: flex;
  align-items: center;
  gap: var(--space-2);
  background: color-mix(in srgb, var(--color-warning) 14%, var(--color-surface));
  color: var(--color-warning);
  border: 1px solid color-mix(in srgb, var(--color-warning) 45%, var(--color-border));
  padding: var(--space-1) var(--space-2);
  border-radius: var(--radius-control);
  width: 100%;
}
.message-text {
  flex: 1;
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
