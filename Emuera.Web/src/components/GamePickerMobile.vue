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
  if (isLoading.value) return;
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
  if (!dir || isLoading.value) return;
  await game.loadGame(dir);
}

function onDismissError(): void {
  game.clearLoadGameError();
}
</script>

<template>
  <div class="game-picker-mobile">
    <button class="pick-btn" :disabled="isLoading" @click="onPickDirectory">
      📁 选择目录
    </button>
    <span class="separator">或手动输入：</span>
    <input
      v-model="input"
      class="dir-input"
      type="text"
      placeholder="/sdcard/games/mygame"
      :disabled="isLoading"
      @keyup.enter="onLoad"
    />
    <button class="load-btn" :disabled="isLoading || !input.trim()" @click="onLoad">
      {{ isLoading ? '加载中…' : '加载' }}
    </button>
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
  gap: 8px;
  font-size: 13px;
  flex-wrap: wrap;
}
.pick-btn {
  background: #0e639c;
  color: #fff;
  border: none;
  padding: 6px 14px;
  border-radius: 3px;
  cursor: pointer;
  font-size: 13px;
}
.pick-btn:disabled {
  background: #3a3a3a;
  cursor: not-allowed;
}
.separator {
  color: #888;
  font-size: 12px;
}
.dir-input {
  background: #1e1e1e;
  color: #e0e0e0;
  border: 1px solid #3c3c3c;
  padding: 3px 8px;
  border-radius: 3px;
  font-family: ui-monospace, Consolas, monospace;
  font-size: 12px;
  min-width: 220px;
  flex: 1;
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
  width: 100%;
}
.picker-message {
  display: flex;
  align-items: center;
  gap: 8px;
  background: #3a3a1d;
  color: #dcdcaa;
  border: 1px solid #5a5a2a;
  padding: 4px 8px;
  border-radius: 3px;
  width: 100%;
}
.message-text {
  flex: 1;
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
  color: inherit;
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
