<script setup lang="ts">
import { ref, computed, watch, onMounted, onUnmounted } from 'vue';
import {
  useGameStore,
  mapLoadGameErrorCode,
  formatMainGameDirForDisplay,
} from '../stores/game';
import {
  isMauiEnvironment,
  pickGameFolder,
  sendBridgeUrl,
  scanGames as scanGamesBridge,
  loadGameFromPath,
  MAUI_WINDOWS_VIRTUAL_HOST,
  MAUI_GAME_VIRTUAL_HOST,
} from '../lib/mauiBridge';
import DirectoryBrowser from './DirectoryBrowser.vue';
import GameRow from './GameRow.vue';
import StatusBanner from './StatusBanner.vue';

/**
 * MauiGameList — MAUI 游戏选择页（PickerShell，ui-redesign-spec §4.2 / §5 + 原型3）。
 *
 * 结构：顶部应用栏（⋮ 菜单：更改目录 / 重新扫描）→ hero 大标题「选择游戏」
 * → 路径行（path-prefix + path-value）→ 列表 / 空状态 / 扫描状态。
 * - Windows：更改目录投递 pickFolder → C# 原生 FolderPicker
 * - Android：投递 pickSafDirectory（SAF 原生目录选择器）
 * 错误使用顶部可关闭 StatusBanner（5 秒自动消失）；空状态提供唯一主操作按钮。
 */
const game = useGameStore();

/** 顶部 ⋮ 菜单展开状态。 */
const showMenu = ref(false);
const isScrolled = ref(false);
const menuRoot = ref<HTMLElement | null>(null);

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

/** 是否在 MAUI Windows 环境——「更改目录」走原生 FolderPicker。 */
const isWindowsMaui = computed(() => {
  if (!isMauiEnvironment()) return false;
  if (typeof window === 'undefined') return false;
  const { protocol, hostname } = window.location;
  // Windows packaged: ms-appx-web:  /  Windows unpackaged: https://app.local/
  // 不能用 protocol==='https:' 判定 Windows——Android 新页面形态也是 https（game.local）。
  return protocol === 'ms-appx-web:'
    || (protocol === 'https:' && hostname === MAUI_WINDOWS_VIRTUAL_HOST);
});

/** 是否在 MAUI Android 环境——走 SAF 原生目录选择器。 */
const isAndroidMaui = computed(() => {
  if (!isMauiEnvironment()) return false;
  if (typeof window === 'undefined') return false;
  const { protocol, hostname } = window.location;
  return protocol === 'file:'
    || (protocol === 'https:' && hostname === MAUI_GAME_VIRTUAL_HOST);
});

/** 主目录展示文案——优先 scanRootDir（最近扫描的目录），fallback mainGameDir。 */
const mainDirDisplay = computed(() =>
  formatMainGameDirForDisplay(game.scanRootDir ?? game.mainGameDir),
);

/** 是否尚未选择目录——用于应用栏「未选择目录」占位。 */
const hasMainDir = computed(() => !!(game.scanRootDir ?? game.mainGameDir));

/** 列表是否为空——scanStatus='idle' + scannedGames 为空才算空状态。 */
const isEmpty = computed(
  () => game.scanStatus === 'idle' && game.scannedGames.length === 0,
);

/** 扫描中——菜单项禁用 + 轻量进度文案。 */
const isScanning = computed(() => game.scanStatus === 'scanning');

/** 重新扫描——以当前主目录为起点重扫（未选目录时退化为更改目录）。 */
function onRescan(): void {
  const dir = game.scanRootDir ?? game.mainGameDir;
  if (!dir) {
    onChangeMainDir();
    return;
  }
  game.setMainGameDir(dir);
  game.scanStatus = 'scanning';
  scanGamesBridge(dir);
}

/** 点击「更改目录」——按平台分流。 */
function onChangeMainDir(): void {
  if (isWindowsMaui.value) {
    game.clearMauiError();
    pickGameFolder();
    return;
  }
  if (isAndroidMaui.value) {
    game.clearMauiError();
    sendBridgeUrl('pickSafDirectory');
    return;
  }
  console.warn('[MauiGameList] onChangeMainDir: not in MAUI environment');
}

/** DirectoryBrowser 确认——更新主目录并重扫。 */
function onDirectoryConfirm(path: string): void {
  console.log('[MauiGameList] directory confirmed:', path);
  game.setMainGameDir(path);
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
  game.setLastPlayedGame(name);
  game.setGameDir(fullPath);
  game.reset();
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

watch(currentError, () => scheduleErrorBannerAutoDismiss(), { immediate: true });

function onDocumentPointerDown(event: PointerEvent): void {
  if (!showMenu.value) return;
  const target = event.target;
  if (!(target instanceof Node) || !menuRoot.value?.contains(target)) {
    showMenu.value = false;
  }
}

function onDocumentKeyDown(event: KeyboardEvent): void {
  if (event.key === 'Escape' && showMenu.value) {
    showMenu.value = false;
  }
}

function onWindowScroll(): void {
  isScrolled.value = window.scrollY > 24;
}

onMounted(() => {
  document.addEventListener('pointerdown', onDocumentPointerDown);
  document.addEventListener('keydown', onDocumentKeyDown);
  window.addEventListener('scroll', onWindowScroll, { passive: true });
  onWindowScroll();
});

onUnmounted(() => {
  document.removeEventListener('pointerdown', onDocumentPointerDown);
  document.removeEventListener('keydown', onDocumentKeyDown);
  window.removeEventListener('scroll', onWindowScroll);
});
</script>

<template>
  <div ref="menuRoot" class="maui-game-list" :class="{ 'is-scrolled': isScrolled }">
    <!-- 原型3：顶部仅保留右侧三点菜单，菜单不改变页面布局。 -->
    <header class="picker-appbar">
      <span class="compact-title" aria-hidden="true">选择游戏</span>
      <button
        type="button"
        class="appbar-menu-btn"
        :aria-label="showMenu ? '关闭目录操作菜单' : '目录操作菜单'"
        aria-haspopup="true"
        :aria-expanded="showMenu"
        @click="showMenu = !showMenu"
      >
        <svg viewBox="0 0 24 24" aria-hidden="true">
          <path d="M12 5v.01M12 12v.01M12 19v.01" />
        </svg>
      </button>
      <div v-if="showMenu" class="directory-menu" role="menu">
        <button type="button" role="menuitem" :disabled="isScanning" @click="showMenu = false; onChangeMainDir()">
          {{ isScanning ? '扫描中…' : '更改目录...' }}
        </button>
        <button type="button" role="menuitem" :disabled="isScanning" @click="showMenu = false; onRescan()">
          重新扫描游戏
        </button>
      </div>
    </header>

    <!-- hero 大标题（原型3）：功能标题，不显示产品名 -->
    <div class="hero-title">选择游戏</div>

    <!-- 路径行独立为 sticky 层，滚动后与紧凑标题一起覆盖列表。 -->
    <div class="path-line">
      <span class="path-prefix">当前目录</span>
      <span class="path-value" :title="mainDirDisplay">
        {{ hasMainDir ? mainDirDisplay : '未选择目录' }}
      </span>
    </div>

    <div class="mgl-body">
      <!-- 错误 banner（spec §5.4：顶部可关闭提示条，5s 自动消失） -->
      <div v-if="currentError" class="error-slot">
        <StatusBanner kind="error" closable @close="onDismissError">
          {{ currentError }}
        </StatusBanner>
      </div>

      <!-- 扫描状态（spec §5.4：轻量 spinner + 文案，避免大面积 loading） -->
      <div v-if="isScanning" class="scanning">
        <span class="spinner" aria-hidden="true" />
        <span>正在扫描游戏列表…</span>
      </div>

      <!-- 空状态（spec §5.4：居中紧凑 + 唯一主操作） -->
      <div v-else-if="isEmpty" class="empty-state">
        <template v-if="game.scanRootDirExists === false">
          <div class="empty-title">主目录不存在</div>
          <div class="empty-hint">请手动创建以下目录并把游戏放进去：</div>
        </template>
        <template v-else>
          <div class="empty-title">未找到游戏</div>
          <div class="empty-hint">请把游戏放到以下目录：</div>
        </template>
        <div class="empty-path" :title="mainDirDisplay">{{ mainDirDisplay }}</div>
        <div class="empty-hint">游戏目录需要包含 csv/ 和 erb/ 两个子目录</div>
        <button
          type="button"
          class="empty-primary-btn"
          @click="onChangeMainDir"
        >
          更改主目录
        </button>
      </div>

      <!-- 游戏列表（spec §5.3：文件管理器式整行） -->
      <div v-else class="game-list-wrapper">
        <ul class="game-list">
          <GameRow
            v-for="g in game.scannedGames"
            :key="g.fullPath"
            :name="g.name"
            :last-played="g.name === game.lastPlayedGame"
            @click="onPickGame(g.name, g.fullPath)"
          />
        </ul>
      </div>
    </div>

    <!-- Android 目录浏览器弹窗（SAF 不可用的兜底） -->
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
  --prototype-bg: #171717;
  --prototype-surface: #1f1f1f;
  --prototype-surface-menu: #2a2b2e;
  --prototype-text: #e8eaed;
  --prototype-muted: #bdc1c6;
  --prototype-border: #303134;
  --prototype-primary: #a8c7fa;
  width: 100%;
  min-height: 100dvh;
  margin: 0;
  display: flex;
  flex-direction: column;
  background: var(--prototype-bg);
  color: var(--prototype-text);
  font-family: Roboto, "Noto Sans SC", "Segoe UI", system-ui, -apple-system, sans-serif;
  font-size: 14px;
  border: 0;
  border-radius: 0;
  box-shadow: none;
  overflow: visible;
}

/* PickerShell 顶部应用栏（原型3）——56px 高、surface 背景，菜单按钮右对齐 */
.picker-appbar {
  position: sticky;
  top: 0;
  z-index: 20;
  flex-shrink: 0;
  display: flex;
  align-items: center;
  justify-content: flex-end;
  min-height: 56px;
  padding: 8px 12px 0 16px;
  background: color-mix(in srgb, var(--prototype-bg) 92%, transparent);
  backdrop-filter: blur(10px);
  transition: background-color 0.18s ease;
}
.maui-game-list.is-scrolled .picker-appbar {
  background: color-mix(in srgb, var(--prototype-bg) 92%, transparent);
  border-bottom-color: transparent;
}
.compact-title {
  position: absolute;
  left: 50%;
  top: 50%;
  transform: translate(-50%, calc(-50% + 8px));
  color: var(--prototype-text);
  font-size: 16px;
  line-height: 24px;
  opacity: 0;
  pointer-events: none;
  transition: opacity 0.18s ease, transform 0.18s ease;
}
.maui-game-list.is-scrolled .compact-title {
  opacity: 1;
  transform: translate(-50%, -50%);
}
/* 原型3：48px 圆形图标按钮和 8/12px Material state layer。 */
.appbar-menu-btn {
  width: 48px;
  height: 48px;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  background: transparent;
  color: var(--prototype-text);
  border: none;
  border-radius: 9999px;
  cursor: pointer;
  transition: background-color 0.15s ease;
}
.appbar-menu-btn:hover {
  background: color-mix(in srgb, var(--prototype-text) 8%, transparent);
}
.appbar-menu-btn:active {
  background: color-mix(in srgb, var(--prototype-text) 14%, transparent);
}
.appbar-menu-btn:focus-visible {
  background: color-mix(in srgb, var(--prototype-text) 12%, transparent);
  outline: 2px solid var(--prototype-primary);
  outline-offset: -2px;
}
.appbar-menu-btn svg {
  width: 24px;
  height: 24px;
  fill: none;
  stroke: currentColor;
  stroke-width: 2;
  stroke-linecap: round;
  stroke-linejoin: round;
}
.directory-menu {
  position: absolute;
  top: 56px;
  right: 12px;
  z-index: 10;
  width: 200px;
  padding: 8px 0;
  background: var(--prototype-surface-menu);
  border: 1px solid var(--prototype-border);
  border-radius: 12px;
  box-shadow: 0 4px 12px rgba(0, 0, 0, 0.4);
}
.directory-menu button {
  display: flex;
  align-items: center;
  width: 100%;
  min-height: 44px;
  padding: 0 16px;
  border: 0;
  background: transparent;
  color: var(--prototype-text);
  font-size: 14px;
  text-align: left;
  cursor: pointer;
}
.directory-menu button:hover:not(:disabled) {
  background: color-mix(in srgb, var(--prototype-text) 8%, transparent);
}
.directory-menu button:focus-visible {
  background: color-mix(in srgb, var(--prototype-text) 12%, transparent);
  outline: 2px solid var(--prototype-primary);
  outline-offset: -2px;
}
.directory-menu button:disabled {
  color: var(--prototype-muted);
  cursor: not-allowed;
  opacity: 0.6;
}

/* hero 大标题（原型3）：功能标题，不显示产品名 */
.hero-title {
  padding: 12px 24px;
  font-size: 32px;
  font-weight: 400;
  line-height: 40px;
  color: var(--prototype-text);
  opacity: 1;
  transition: opacity 0.32s ease;
  animation: picker-hero-in 0.52s ease both;
}
.maui-game-list.is-scrolled .hero-title {
  opacity: 0;
}

/* 路径行（原型3）：prefix + mono value，可换行不截断 */
.path-line {
  position: sticky;
  top: 56px;
  z-index: 25;
  width: 100%;
  margin: 0 0 18px;
  padding: 0 24px 12px;
  border-bottom: 1px solid var(--prototype-border);
  background: var(--prototype-bg);
  display: flex;
  align-items: baseline;
  gap: 10px;
  min-width: 0;
}
@keyframes picker-hero-in {
  from { opacity: 0; }
  to { opacity: 1; }
}
.path-prefix {
  color: var(--prototype-muted);
  font-size: 13px;
  line-height: 20px;
  flex-shrink: 0;
}
.path-value {
  min-width: 0;
  word-break: break-all; /* 完整路径允许换行，不用省略号截断（spec §8 窄屏） */
  font-family: var(--font-mono);
  font-size: 14px;
  line-height: 22px;
  color: var(--prototype-text);
  opacity: 0.9;
}
.mgl-body {
  flex: 1;
  overflow: visible;
  display: flex;
  flex-direction: column;
  padding: 0 0 24px;
  padding-bottom: env(safe-area-inset-bottom); /* B5：底部安全区 */
}
.error-slot {
  padding-bottom: var(--space-2);
}
.scanning {
  flex: 1;
  display: flex;
  align-items: center;
  justify-content: center;
  gap: var(--space-2);
  padding: var(--space-4);
  text-align: center;
  color: var(--color-text-muted);
  font-size: var(--font-size-md);
}
.spinner {
  width: 16px;
  height: 16px;
  border: 2px solid var(--color-border);
  border-top-color: var(--color-indicator);
  border-radius: 50%;
  animation: mgl-spin 0.8s linear infinite;
  flex-shrink: 0;
}
@keyframes mgl-spin {
  from { transform: rotate(0deg); }
  to { transform: rotate(360deg); }
}

/* 空状态（spec §5.4：居中紧凑，主按钮唯一） */
.empty-state {
  flex: 1;
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  gap: var(--space-2);
  padding: var(--space-5) var(--space-4);
  text-align: center;
  max-width: var(--picker-list-max-width);
  margin: 0 auto;
  width: 100%;
}
.empty-title {
  font-size: var(--font-size-title);
  font-weight: 600;
  color: var(--color-text);
}
.empty-hint {
  font-size: var(--font-size-sm);
  color: var(--color-text-muted);
}
.empty-path {
  font-family: var(--font-mono);
  font-size: var(--font-size-sm);
  color: var(--color-success);
  word-break: break-all;
  max-width: 100%;
  padding: var(--space-1) var(--space-2);
  background: var(--color-surface);
  border: 1px solid var(--color-border);
  border-radius: var(--radius-control);
}
.empty-primary-btn {
  margin-top: var(--space-3);
  background: color-mix(in srgb, var(--color-indicator) 18%, var(--color-surface));
  color: var(--color-indicator);
  border: 1px solid color-mix(in srgb, var(--color-indicator) 55%, var(--color-border));
  padding: var(--space-2) var(--space-5);
  border-radius: var(--radius-control);
  cursor: pointer;
  font-size: var(--font-size-base);
  font-family: var(--font-ui);
  min-height: var(--touch-target); /* §12：触控目标 ≥48px */
  transition: background var(--motion-fast);
}
.empty-primary-btn:hover {
  background: color-mix(in srgb, var(--color-indicator) 26%, var(--color-surface));
}
.empty-primary-btn:active {
  background: color-mix(in srgb, var(--color-indicator) 32%, var(--color-surface));
}

/* 游戏列表（spec §5.3：文件管理器式整行，无卡片） */
.game-list-wrapper {
  flex: 1;
  display: flex;
  flex-direction: column;
}
.game-list {
  list-style: none;
  margin: 0;
  padding: 0;
  flex: 1;
  display: flex;
  flex-direction: column;
  gap: 4px;
  overflow: visible;
  max-width: none;
  width: 100%;
  margin-left: auto;
  margin-right: auto;
}

</style>
