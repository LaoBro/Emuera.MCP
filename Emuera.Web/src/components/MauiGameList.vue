<script setup lang="ts">
import { ref, computed, watch } from 'vue';
import { useGameStore, mapLoadGameErrorCode } from '../stores/game';
import {
  isMauiEnvironment,
  pickGameFolder,
  sendBridgeUrl,
  scanGames as scanGamesBridge,
  loadGameFromPath,
} from '../lib/mauiBridge';
import DirectoryBrowser from './DirectoryBrowser.vue';
/**
 * game-library spec ID7 / ID11：MAUI 游戏列表组件——替换旧 MauiGamePicker.vue。
 *
 * 三态布局：
 * 1. scanning（scanStatus='scanning'）→ loading 占位
 * 2. 空状态（scanStatus='idle' + scannedGames 为空）→ 提示 + 「更改主目录」按钮
 * 3. 列表（scanStatus='idle' + scannedGames 非空）→ 游戏列表 + 底部主目录展示
 *
 * 「更改主目录」按钮：
 * - Windows：投递 pickFolder → C# 弹原生 FolderPicker → folderPicked 回 Vue
 * - Android：弹 DirectoryBrowser.vue 模态框 → 用户导航 → 确认后 scanGames
 *
 * 列表项点击：
 * - setLastPlayedGame(name) 持久化高亮
 * - loadGameFromPath(fullPath) 触发 C# hot-swap reload
 *
 * 错误 banner：
 * - mauiError（C# reload 失败）+ loadGameError（HTTP 模式错误，MAUI 不触发但保留兼容）
 * - 5 秒自动消失或用户手动关闭
 */

const game = useGameStore();

/** DirectoryBrowser 弹窗可见性——v-model 控制。 */
const showDirectoryBrowser = ref(false);

/** 错误 banner 自动消失定时器。 */
let errorBannerTimer: ReturnType<typeof setTimeout> | null = null;

/** 当前展示的错误文案——合并 mauiError + loadGameError。 */
const currentError = computed<string | null>(() => {
  if (game.mauiError) return game.mauiError;
  if (game.loadGameError) {
    return `${mapLoadGameErrorCode(game.loadGameError.code)}：${game.loadGameError.message}`;
  }
  return null;
});

/** 是否在 MAUI Windows 环境——决定「更改主目录」按钮走原生 FolderPicker 还是 Vue 弹窗。 */
const isWindowsMaui = computed(() => {
  if (!isMauiEnvironment()) return false;
  if (typeof window === 'undefined') return false;
  // Windows unpackaged: https://app.local/  /  Windows packaged: ms-appx-web:
  return window.location.protocol === 'https:' || window.location.protocol === 'ms-appx-web:';
});

/** 是否在 MAUI Android 环境——走 DirectoryBrowser 弹窗。 */
const isAndroidMaui = computed(() => {
  if (!isMauiEnvironment()) return false;
  if (typeof window === 'undefined') return false;
  return window.location.protocol === 'file:';
});

/** 主目录展示文案——优先 scanRootDir（最近扫描的目录），fallback mainGameDir。 */
const mainDirDisplay = computed(() => game.scanRootDir ?? game.mainGameDir ?? '(未设置)');

/** 列表是否为空——scanStatus='idle' + scannedGames 为空才算空状态。 */
const isEmpty = computed(
  () => game.scanStatus === 'idle' && game.scannedGames.length === 0,
);

/** 点击「更改主目录」按钮——按平台分流。 */
function onChangeMainDir(): void {
  if (isWindowsMaui.value) {
    // Windows：复用 issue 09 pickFolder 流程——C# 弹 WinRT FolderPicker
    // folderPicked 回 Vue 后由 useAppInit 处理（game-library spec ID9 修订）：
    //   setMainGameDir(path) + scanGames(path)——保存为主目录并重扫，不再 loadGameFromPath
    // C# HandleScanGames 收到 rootDir 后同步更新 _mainGameDir + 写 Preferences
    game.clearMauiError();
    pickGameFolder();
    return;
  }
  if (isAndroidMaui.value) {
    // ADR-0019：Android 用 SAF 原生目录选择器替代手写 DirectoryBrowser
    game.clearMauiError();
    sendBridgeUrl('pickSafDirectory');
    return;
  }
  // 兜底——HTTP 模式或平台未识别，不应到此（App.vue 应只在 isMaui 时渲染此组件）
  console.warn('[MauiGameList] onChangeMainDir: not in MAUI environment');
}

/** DirectoryBrowser 确认——用户选了新主目录。 */
function onDirectoryConfirm(path: string): void {
  console.log('[MauiGameList] directory confirmed:', path);
  // 更新 mainGameDir + 持久化
  game.setMainGameDir(path);
  // 重新扫描新主目录——scanStatus='scanning' 让 UI 显示 loading
  game.scanStatus = 'scanning';
  scanGamesBridge(path);
}

/** DirectoryBrowser 取消——no-op（弹窗已自关闭）。 */
function onDirectoryCancel(): void {
  console.log('[MauiGameList] directory browse cancelled');
}

/** 点击列表项——加载该游戏。 */
function onPickGame(name: string, fullPath: string): void {
  console.log(`[MauiGameList] picking game: ${name} -> ${fullPath}`);
  // 记住上次玩过的游戏——列表高亮用
  game.setLastPlayedGame(name);
  // 更新 gameDir + 持久化——loadGameFromPath 触发 C# hot-swap reload
  game.setGameDir(fullPath);
  // 清空旧显示状态——新游戏首帧到达前不残留旧画面
  game.reset();
  // 通知 C# hot-swap reload
  loadGameFromPath(fullPath);
}

/** 关闭错误 banner——用户点击 ×。 */
function onDismissError(): void {
  game.clearMauiError();
  game.clearLoadGameError();
  if (errorBannerTimer) {
    clearTimeout(errorBannerTimer);
    errorBannerTimer = null;
  }
}

/** 错误 banner 5 秒自动消失——watch currentError 变化时重启定时器。 */
function scheduleErrorBannerAutoDismiss(): void {
  if (errorBannerTimer) {
    clearTimeout(errorBannerTimer);
    errorBannerTimer = null;
  }
  if (currentError.value) {
    errorBannerTimer = setTimeout(() => {
      onDismissError();
    }, 5000);
  }
}

// 监听错误变化——currentError 变化时重启 5s 自动消失定时器
watch(currentError, () => scheduleErrorBannerAutoDismiss(), { immediate: true });
</script>

<template>
  <div class="maui-game-list">
    <!-- game-library spec layout fix：全屏选择界面标题栏 -->
    <header class="mgl-header">
      <h1 class="mgl-title">Emuera</h1>
      <span class="mgl-path" :title="mainDirDisplay">{{ mainDirDisplay }}</span>
      <button class="change-dir-btn" @click="onChangeMainDir">更改主目录</button>
    </header>

    <div class="mgl-body">
    <!-- 错误 banner -->
    <div v-if="currentError" class="error-banner">
      <span class="error-text">{{ currentError }}</span>
      <button class="dismiss-btn" @click="onDismissError">×</button>
    </div>

    <!-- scanning 状态 -->
    <div v-if="game.scanStatus === 'scanning'" class="scanning">
      正在扫描游戏列表...
    </div>

    <!-- 空状态 -->
    <div v-else-if="isEmpty" class="empty-state">
      <!-- game-library spec ID13：主目录不存在 vs 存在但无游戏 -->
      <template v-if="game.scanRootDirExists === false">
        <div class="empty-icon">⚠️</div>
        <div class="empty-title">主目录不存在</div>
        <div class="empty-hint">请手动创建以下目录并把游戏放进去:</div>
        <div class="empty-path" :title="mainDirDisplay">{{ mainDirDisplay }}</div>
        <div class="empty-hint">游戏目录需要包含 csv/ 和 erb/ 两个子目录</div>
        <button class="change-dir-btn" @click="onChangeMainDir">更改主目录</button>
      </template>
      <template v-else>
        <div class="empty-icon">⚠️</div>
        <div class="empty-title">未找到游戏</div>
        <div class="empty-hint">请把游戏放到以下目录:</div>
        <div class="empty-path" :title="mainDirDisplay">{{ mainDirDisplay }}</div>
        <div class="empty-hint">游戏目录需要包含 csv/ 和 erb/ 两个子目录</div>
        <button class="change-dir-btn" @click="onChangeMainDir">更改主目录</button>
      </template>
    </div>

    <!-- 游戏列表 -->
    <div v-else class="game-list-wrapper">
      <ul class="game-list">
        <li
          v-for="g in game.scannedGames"
          :key="g.fullPath"
          :class="{ 'game-item': true, 'last-played': g.name === game.lastPlayedGame }"
        >
          <button class="game-btn" @click="onPickGame(g.name, g.fullPath)">
            <span class="game-icon">📁</span>
            <span class="game-name">{{ g.name }}</span>
            <span v-if="g.name === game.lastPlayedGame" class="last-played-tag">上次玩</span>
          </button>
        </li>
      </ul>
    </div>

    </div><!-- /.mgl-body -->

    <!-- Android 目录浏览器弹窗 -->
    <DirectoryBrowser
      v-if="isAndroidMaui"
      v-model:visible="showDirectoryBrowser"
      @confirm="onDirectoryConfirm"
      @cancel="onDirectoryCancel"
    />
  </div>
</template>

<style scoped>
.maui-game-list {
  display: flex;
  flex-direction: column;
  height: 100vh;
  background: #1e1e1e;
  color: #e0e0e0;
  font-size: 13px;
  overflow: hidden;
}

/* 全屏标题栏 */
.mgl-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 12px 16px;
  background: #252526;
  border-bottom: 1px solid #3c3c3c;
  flex-shrink: 0;
}
.mgl-title {
  margin: 0;
  font-size: 16px;
  font-weight: 600;
  color: #e0e0e0;
  flex-shrink: 0;
}
.mgl-path {
  flex: 1;
  margin: 0 12px;
  font-family: ui-monospace, Consolas, monospace;
  font-size: 12px;
  color: #4ec9b0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  text-align: center;
}

/* 滚动内容区 */
.mgl-body {
  flex: 1;
  overflow-y: auto;
  display: flex;
  flex-direction: column;
  padding: 8px 16px 0;
}
.scanning {
  flex: 1;
  display: flex;
  align-items: center;
  justify-content: center;
  padding: 16px;
  text-align: center;
  color: #9aa0a6;
  font-size: 13px;
}
.empty-state {
  flex: 1;
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  gap: 6px;
  padding: 24px 16px;
  text-align: center;
  color: #dcdcdc;
}
.empty-icon {
  font-size: 32px;
  margin-bottom: 4px;
}
.empty-title {
  font-size: 14px;
  font-weight: 600;
  color: #e0e0e0;
}
.empty-hint {
  font-size: 12px;
  color: #9aa0a6;
}
.empty-path {
  font-family: ui-monospace, Consolas, monospace;
  font-size: 12px;
  color: #4ec9b0;
  word-break: break-all;
  max-width: 100%;
  padding: 4px 8px;
  background: #2d2d30;
  border-radius: 3px;
}
.change-dir-btn {
  background: #0e639c;
  color: #fff;
  border: none;
  padding: 6px 16px;
  border-radius: 3px;
  cursor: pointer;
  font-size: 13px;
  margin-top: 8px;
}
.change-dir-btn:hover {
  background: #1177bb;
}
.change-dir-btn.small {
  margin-top: 0;
  padding: 2px 10px;
  font-size: 12px;
}
.game-list-wrapper {
  flex: 1;
  display: flex;
  flex-direction: column;
  gap: 8px;
}
.game-list {
  list-style: none;
  margin: 0;
  padding: 0;
  flex: 1;
  display: flex;
  flex-direction: column;
  gap: 2px;
  overflow-y: auto;
}
.game-item {
  border-left: 3px solid transparent;
}
.game-item.last-played {
  border-left-color: #dcdcaa;
}
.game-btn {
  display: flex;
  align-items: center;
  gap: 8px;
  width: 100%;
  text-align: left;
  background: transparent;
  color: #dcdcdc;
  border: none;
  padding: 8px 12px;
  cursor: pointer;
  font-size: 13px;
  font-family: inherit;
}
.game-btn:hover {
  background: #2d2d30;
  color: #fff;
}
.game-icon {
  font-size: 16px;
  flex-shrink: 0;
}
.game-name {
  flex: 1;
  font-family: ui-monospace, Consolas, monospace;
  word-break: break-all;
}
.last-played-tag {
  font-size: 10px;
  color: #dcdcaa;
  background: #5a4a1d;
  padding: 1px 6px;
  border-radius: 8px;
  flex-shrink: 0;
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
}
.error-text {
  flex: 1;
  font-size: 12px;
  word-break: break-all;
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
