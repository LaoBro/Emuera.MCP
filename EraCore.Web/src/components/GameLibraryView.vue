<script setup lang="ts">
import { ref, computed, watch, onMounted, onUnmounted } from 'vue';
import { useGameStore, mapLoadGameErrorCode, formatMainGameDirForDisplay } from '../stores/game';
import { useConnectionStore } from '../stores/connection';
import type { GameLibrarySource } from '../lib/gameLibrary';
import GameRow from './GameRow.vue';
import StatusBanner from './StatusBanner.vue';

/**
 * GameLibraryView — MAUI 与 Web/HTTP **共用的游戏选择页**（单一设计源）。
 *
 * 背景：浏览器沙箱不能枚举本地文件系统，MAUI 走 C# 桥接、Web 走 C# server HTTP 端点，
 * 但两者底层都调 Core 的 GameScanner/DirectoryLister，store 状态完全共享。
 * 故本组件只认 <see cref="GameLibrarySource"/> 抽象（扫描/浏览/选目录/加载），
 * 页面结构、样式、空状态、目录浏览全部只维护这一份——改设计只需动本文件。
 *
 * 差异由 source 能力驱动：
 * - source.supportsNativePicker=true（MAUI）：「更改主目录」→ 原生 FolderPicker/SAF
 * - false（Web）：「浏览目录」→ 页面内目录浏览（source.listDirs）
 *
 * 顶部应用栏右侧内容由父组件经 `#appbar-actions` 插槽注入
 * （MAUI：⋮ 菜单 = 更改目录/重新扫描/主题；Web：连接设置按钮）。
 */
const props = defineProps<{
  /** 游戏库数据源（maui 桥接 或 http）。 */
  source: GameLibrarySource;
}>();

const game = useGameStore();
const conn = useConnectionStore();

/** 原生选择器可用 → 走原生；否则走页面内目录浏览。 */
const isNative = computed(() => props.source.supportsNativePicker);

/** 主目录展示文案——优先 scanRootDir（最近扫描的目录），fallback mainGameDir。 */
const mainDirDisplay = computed(() =>
  formatMainGameDirForDisplay(game.scanRootDir ?? game.mainGameDir),
);

/** 是否已设置主目录。 */
const hasMainDir = computed(() => !!(game.scanRootDir ?? game.mainGameDir));

/** 扫描中——展示 loading 占位。 */
const isScanning = computed(() => game.scanStatus === 'scanning');

/** 加载游戏中——禁用交互。 */
const isLoading = computed(() => game.reloadStatus === 'loading');

/** 旁观中——禁点游戏（与 GamePicker 语义一致）。 */
const loadBlocked = computed(() => isLoading.value || !conn.canMutateLifecycle);

/** 列表为空——scanStatus='idle' + 无游戏才算空状态。 */
const isEmpty = computed(
  () => game.scanStatus === 'idle' && game.scannedGames.length === 0,
);

// ---------- 错误条（合并 mauiError / loadGameError / 本地传输错误） ----------
/** 本地错误文案——scan / browse / load 传输失败时写入。 */
const localError = ref<string | null>(null);
const currentError = computed<string | null>(() => {
  if (localError.value) return localError.value;
  if (game.mauiError) return game.mauiError;
  if (game.loadGameError) {
    return `${mapLoadGameErrorCode(game.loadGameError.code)}：${game.loadGameError.message}`;
  }
  return null;
});
let errorBannerTimer: ReturnType<typeof setTimeout> | null = null;

function onDismissError(): void {
  localError.value = null;
  game.clearMauiError();
  game.clearLoadGameError();
  if (errorBannerTimer) {
    clearTimeout(errorBannerTimer);
    errorBannerTimer = null;
  }
}

/** 错误条 5 秒自动消失——watch currentError 变化时重启定时器。 */
function scheduleErrorBannerAutoDismiss(): void {
  if (errorBannerTimer) {
    clearTimeout(errorBannerTimer);
    errorBannerTimer = null;
  }
  if (currentError.value) {
    errorBannerTimer = setTimeout(onDismissError, 5000);
  }
}
watch(currentError, () => scheduleErrorBannerAutoDismiss(), { immediate: true });

// ---------- 目录浏览（仅 Web：supportsNativePicker=false） ----------
const browsing = ref(false);
const browsePath = ref<string>('');
const browseDirs = ref<string[]>([]);
const browseParent = ref<string | null>(null);
const browseInput = ref<string>('');

/** 主目录输入框（仅 Web 设置行）——预填 mainGameDir。 */
const mainDirInput = ref<string>(game.mainGameDir ?? '');

/** 扫描主目录——输入框/浏览选中的路径作为主目录，经 source.scan 请求列表。 */
async function doScan(dir?: string): Promise<void> {
  const target = (dir ?? mainDirInput.value).trim();
  if (!target) return;
  mainDirInput.value = target;
  localError.value = null;
  const ok = await props.source.scan(target);
  if (!ok) {
    localError.value = '无法连接服务器——请确认 C# server 正在运行（检查右侧连接设置）。';
  }
}

/** 切换目录浏览模式（Web）——首次打开以当前主目录为起点加载子目录。 */
async function toggleBrowse(): Promise<void> {
  localError.value = null;
  if (browsing.value) {
    browsing.value = false;
    return;
  }
  browsing.value = true;
  await enterBrowse(game.scanRootDir ?? game.mainGameDir ?? '');
}

/** 进入目录浏览指定路径。 */
async function enterBrowse(dir: string): Promise<void> {
  const target = dir.trim();
  browsePath.value = target;
  browseInput.value = target;
  const result = await props.source.listDirs(target);
  if (result === null) {
    browseDirs.value = [];
    browseParent.value = null;
    localError.value = `无法访问目录：${target || '(空)'}`;
    return;
  }
  browsePath.value = result.currentPath;
  browseInput.value = result.currentPath;
  browseDirs.value = result.dirs;
  browseParent.value = result.parentPath;
}

/** 前往目录浏览器输入框中的路径。 */
async function onBrowseGo(): Promise<void> {
  await enterBrowse(browseInput.value);
}

/** 点击子目录——进入该目录继续浏览。 */
async function onPickDir(name: string): Promise<void> {
  const base = browsePath.value.replace(/[\\/]+$/, '');
  const sep = browsePath.value.includes('\\') ? '\\' : '/';
  await enterBrowse(name ? `${base}${sep}${name}` : base);
}

/** 回到上级目录。 */
async function onBrowseUp(): Promise<void> {
  if (browseParent.value) await enterBrowse(browseParent.value);
}

/** 使用浏览中的目录作为主目录 → 退出浏览 → 扫描。 */
async function onUseBrowsePath(): Promise<void> {
  if (!browsePath.value.trim()) return;
  browsing.value = false;
  game.setMainGameDir(browsePath.value);
  mainDirInput.value = browsePath.value;
  await doScan(browsePath.value);
}

/** 「更改主目录」——MAUI 原生选择器（结果经事件回 store）。 */
async function onPickMainDir(): Promise<void> {
  localError.value = null;
  game.clearMauiError();
  await props.source.pickMainDir();
}

/** 点击游戏列表项——经 source.pickGame 加载（MAUI hot-swap / HTTP /load-game）。 */
async function onPickGame(name: string, fullPath: string): Promise<void> {
  if (loadBlocked.value) return;
  localError.value = null;
  await props.source.pickGame(name, fullPath);
}

// ---------- hero 滚动效果（原型3：滚动后顶部出现紧凑标题 + 淡出 hero） ----------
const compactTitleScrollThreshold = 24;
const isScrolled = ref(false);
const heroOpacity = ref(1);
let heroIntroTimer: ReturnType<typeof setTimeout> | null = null;
const heroIntro = ref(true);

function onWindowScroll(): void {
  const scrollY = window.scrollY;
  isScrolled.value = scrollY > 0;
  if (scrollY > 0) heroIntro.value = false;
  heroOpacity.value = Math.max(0, 1 - scrollY / compactTitleScrollThreshold);
}

onMounted(() => {
  window.addEventListener('scroll', onWindowScroll, { passive: true });
  onWindowScroll();
  heroIntroTimer = setTimeout(() => {
    heroIntro.value = false;
  }, 540);
  // 启动自动扫描：MAUI 由 useAppInit 首扫（bridge 事件驱动），Web 由视图首扫（避免双扫）。
  if (props.source.kind === 'http') {
    const dir = game.mainGameDir;
    if (dir) {
      mainDirInput.value = dir;
      void doScan(dir);
    }
  }
});

onUnmounted(() => {
  window.removeEventListener('scroll', onWindowScroll);
  if (heroIntroTimer) clearTimeout(heroIntroTimer);
  if (errorBannerTimer) clearTimeout(errorBannerTimer);
});
</script>

<template>
  <div class="game-library-view" :class="{ 'is-scrolled': isScrolled }">
    <!-- 顶部应用栏：紧凑标题居中 + 右侧插槽（⋮ 菜单 / 连接设置） -->
    <header class="picker-appbar">
      <span class="compact-title" aria-hidden="true">选择游戏</span>
      <div class="appbar-actions">
        <slot name="appbar-actions" :is-scanning="isScanning" />
      </div>
    </header>

    <!-- hero 大标题（原型3）：功能标题，不显示产品名 -->
    <div
      class="hero-title"
      :class="{ 'hero-intro': heroIntro }"
      :style="{ opacity: heroOpacity }"
    >选择游戏</div>

    <!-- 路径行：主目录 + 更改主目录/浏览目录入口 -->
    <div class="path-line">
      <span class="path-prefix">主目录</span>
      <span class="path-value" :title="mainDirDisplay">
        {{ hasMainDir ? mainDirDisplay : '未选择目录' }}
      </span>
      <button
        v-if="isNative"
        type="button"
        class="btn-outline path-btn"
        :disabled="isLoading"
        @click="onPickMainDir"
      >更改主目录</button>
      <button
        v-else
        type="button"
        class="btn-outline path-btn"
        :disabled="isLoading"
        @click="toggleBrowse"
      >{{ browsing ? '返回列表' : '浏览目录' }}</button>
    </div>

    <div class="glv-body">
      <!-- 错误条（顶部可关闭提示条，5s 自动消失） -->
      <div v-if="currentError" class="error-slot">
        <StatusBanner kind="error" closable @close="onDismissError">
          {{ currentError }}
        </StatusBanner>
      </div>

      <!-- 目录浏览模式（Web） -->
      <div v-if="browsing" class="browser">
        <div class="browser-path-row">
          <button
            type="button"
            class="btn-outline"
            :disabled="!browseParent"
            @click="onBrowseUp"
          >⬆ 上级</button>
          <input
            v-model="browseInput"
            class="dir-input"
            type="text"
            placeholder="输入目录路径后回车"
            spellcheck="false"
            @keyup.enter="onBrowseGo"
          />
          <button type="button" class="btn-primary" @click="onBrowseGo">前往</button>
        </div>
        <div class="browser-dirs">
          <ul v-if="browseDirs.length" class="dir-list">
            <li v-for="d in browseDirs" :key="d">
              <button type="button" class="dir-row" @click="onPickDir(d)">
                <span class="dir-icon" aria-hidden="true">📁</span>
                <span class="dir-name">{{ d }}</span>
                <span class="dir-arrow" aria-hidden="true">›</span>
              </button>
            </li>
          </ul>
          <div v-else class="browser-empty">此目录下没有子目录</div>
        </div>
        <div class="browser-footer">
          <span class="browser-path" :title="browsePath">{{ browsePath || '(空路径)' }}</span>
          <button
            type="button"
            class="btn-primary"
            :disabled="!browsePath.trim()"
            @click="onUseBrowsePath"
          >使用此目录</button>
        </div>
      </div>

      <!-- 扫描状态（轻量 spinner + 文案） -->
      <div v-else-if="isScanning" class="scanning">
        <span class="spinner" aria-hidden="true" />
        <span>正在扫描游戏列表…</span>
      </div>

      <!-- 主目录输入 + 扫描（仅 Web：尚无主目录时） -->
      <div v-else-if="!isNative && !hasMainDir" class="setup-row">
        <input
          v-model="mainDirInput"
          class="dir-input setup-input"
          type="text"
          placeholder="输入游戏主目录，例如 D:\games"
          spellcheck="false"
          @keyup.enter="doScan()"
        />
        <button
          type="button"
          class="btn-primary"
          :disabled="!mainDirInput.trim() || isLoading"
          @click="doScan()"
        >扫描</button>
      </div>

      <!-- 空状态 -->
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
          v-if="isNative"
          type="button"
          class="btn-primary btn-lg empty-primary-btn"
          @click="onPickMainDir"
        >更改主目录</button>
        <div v-else class="empty-actions">
          <button type="button" class="btn-primary btn-lg" @click="toggleBrowse">浏览目录</button>
          <button type="button" class="btn-outline" @click="doScan()">重新扫描</button>
        </div>
      </div>

      <!-- 游戏列表（文件管理器式整行） -->
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
  </div>
</template>

<style scoped>
.game-library-view {
  width: 100%;
  min-height: 100dvh;
  display: flex;
  flex-direction: column;
  background: var(--color-bg);
  color: var(--color-text);
  font-family: var(--font-ui);
  font-size: var(--font-size-base);
}

/* 顶部应用栏（原型3）——紧凑标题居中，右侧插槽 */
.picker-appbar {
  position: sticky;
  top: 0;
  z-index: 40;
  flex-shrink: 0;
  display: flex;
  align-items: center;
  justify-content: center;
  min-height: var(--appbar-height);
  padding: var(--space-2) var(--space-4) 0;
  background: var(--color-bg);
  transition: background-color var(--motion-mid) ease;
}
.appbar-actions {
  position: absolute;
  right: var(--space-3);
  top: var(--space-2);
  display: flex;
  align-items: center;
  gap: var(--space-1);
}
.compact-title {
  position: absolute;
  left: 50%;
  top: 50%;
  transform: translate(-50%, calc(-50% + 8px));
  color: var(--color-text);
  font-size: var(--font-size-lg);
  line-height: 24px;
  opacity: 0;
  pointer-events: none;
  transition: opacity var(--motion-mid) ease, transform var(--motion-mid) ease;
}
.game-library-view.is-scrolled .compact-title {
  opacity: 1;
  transform: translate(-50%, -50%);
}

/* hero 大标题（原型3） */
.hero-title {
  padding: var(--space-3) var(--space-5);
  font-size: 32px;
  font-weight: 400;
  line-height: 40px;
  color: var(--color-text);
  transition: opacity var(--motion-slow) cubic-bezier(0.2, 0, 0, 1);
}
.hero-title.hero-intro {
  animation: picker-hero-in 0.52s ease both;
}

/* 路径行（原型3）：prefix + mono value + 更改主目录/浏览目录 */
.path-line {
  position: sticky;
  top: var(--appbar-height);
  z-index: 25;
  width: 100%;
  margin: 0 0 18px;
  padding: 0 var(--space-5) var(--space-3);
  background: var(--color-bg);
  display: flex;
  align-items: baseline;
  gap: 10px;
  min-width: 0;
}
.path-prefix {
  color: var(--color-text-muted);
  font-size: var(--font-size-md);
  line-height: 20px;
  opacity: 0.6;
  flex-shrink: 0;
}
.path-value {
  flex: 1;
  min-width: 0;
  word-break: break-all;
  font-family: var(--font-mono);
  font-size: var(--font-size-base);
  line-height: 22px;
  color: var(--color-text);
  opacity: 0.6;
}
.path-btn {
  flex-shrink: 0;
}

.glv-body {
  flex: 1;
  overflow: visible;
  display: flex;
  flex-direction: column;
  padding: 0 0 var(--space-5);
  padding-bottom: env(safe-area-inset-bottom);
}
.error-slot {
  padding: 0 var(--space-5) var(--space-2);
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
  animation: glv-spin var(--motion-spin) linear infinite;
  flex-shrink: 0;
}
@keyframes glv-spin {
  from { transform: rotate(0deg); }
  to { transform: rotate(360deg); }
}

/* 主目录输入 + 扫描（Web） */
.setup-row {
  display: flex;
  gap: var(--space-2);
  padding: 0 var(--space-5) var(--space-3);
  max-width: var(--picker-list-max-width);
  width: 100%;
  margin: 0 auto;
}
.setup-input {
  flex: 1;
  min-width: 0;
}

/* 目录浏览器（Web） */
.browser {
  display: flex;
  flex-direction: column;
  gap: var(--space-2);
  padding: 0 var(--space-5) var(--space-3);
  max-width: var(--picker-list-max-width);
  width: 100%;
  margin: 0 auto;
}
.browser-path-row {
  display: flex;
  gap: var(--space-2);
  width: 100%;
}
.browser-path-row .dir-input {
  flex: 1;
  min-width: 0;
}
.browser-dirs {
  background: var(--color-surface);
  border: 1px solid var(--color-border);
  border-radius: var(--radius-control);
  overflow: hidden;
}
.dir-list {
  list-style: none;
  margin: 0;
  padding: var(--space-1) 0;
  max-height: 46vh;
  overflow-y: auto;
}
.dir-row {
  display: grid;
  grid-template-columns: 32px minmax(0, 1fr) 24px;
  align-items: center;
  width: 100%;
  min-height: 40px;
  text-align: left;
  background: transparent;
  color: var(--color-text);
  border: none;
  padding: 0 var(--space-3);
  cursor: pointer;
  font-size: var(--font-size-base);
  font-family: var(--font-ui);
}
.dir-row:hover {
  background: var(--state-layer-hover);
}
.dir-icon {
  color: var(--color-text-muted);
}
.dir-name {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.dir-arrow {
  color: var(--color-text-muted);
  justify-self: end;
}
.browser-empty {
  padding: var(--space-4);
  color: var(--color-text-muted);
  font-size: var(--font-size-sm);
  text-align: center;
}
.browser-footer {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: var(--space-2);
}
.browser-path {
  flex: 1;
  min-width: 0;
  color: var(--color-text-muted);
  font-family: var(--font-mono);
  font-size: var(--font-size-sm);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
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
}
.empty-actions {
  display: flex;
  gap: var(--space-2);
  margin-top: var(--space-2);
}

/* 游戏列表 */
.game-list-wrapper {
  flex: 1;
  display: flex;
  flex-direction: column;
  padding-inline: var(--space-5);
}
.game-list {
  list-style: none;
  margin: 0;
  padding: 0;
  flex: 1;
  display: flex;
  flex-direction: column;
  gap: var(--space-1);
  max-width: var(--picker-list-max-width);
  width: 100%;
  margin-left: auto;
  margin-right: auto;
}

.dir-input {
  background: var(--color-bg);
  color: var(--color-text);
  border: 1px solid var(--color-border);
  padding: 6px var(--space-2);
  border-radius: var(--radius-control);
  font-family: var(--font-mono);
  font-size: var(--font-size-sm);
}
.dir-input:disabled {
  opacity: 0.6;
}

@keyframes picker-hero-in {
  from { opacity: 0; }
  to { opacity: 1; }
}
</style>
