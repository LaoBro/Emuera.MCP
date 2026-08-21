<script setup lang="ts">
/**
 * ChangeDirDialog.vue — Web/HTTP 的「更改目录」对话框（手动输入路径）。
 *
 * MAUI 不改目录走这里——其右上角 ⋮ 菜单「更改目录」直接弹原生 FolderPicker/SAF
 * （见 MauiGameList.vue），本对话框仅服务 Web/HTTP：页面体（路径行 / 游戏列表）
 * 不再内联目录浏览，改目录由右上角菜单 → 本对话框承担，与 MAUI 的交互路径对齐。
 *
 * 内容：手动路径输入（Web 验收入口） + 「浏览」页面内目录浏览（source.listDirs）自动填框；
 * 确认（「扫描」）→ `source.scan(inputPath)`（server /game/scan），传输失败时保留框体。
 *
 * 视觉与交互遵循 ConfirmDialog.vue 的 Alert 式对话框约定（半透明遮罩 + 表面容器、
 * 点击遮罩 / Escape 关闭），按钮复用全局 .btn-primary / .btn-outline。
 */
import { ref, watch, nextTick, onMounted, onUnmounted } from 'vue';
import { useGameStore } from '../stores/game';
import type { GameLibrarySource, DirListResult } from '../lib/gameLibrary';

const props = defineProps<{
  /** 是否显示。 */
  visible: boolean;
  /** 游戏库数据源（maui 桥接 或 http）。 */
  source: GameLibrarySource;
}>();

const emit = defineEmits<{
  (e: 'update:visible', v: boolean): void;
}>();

const game = useGameStore();

/** 手动路径输入——打开时预填当前主目录（scanRootDir 优先，fallback mainGameDir）。 */
const inputPath = ref<string>('');

/** 本地传输错误文案——scan / browse 失败时写入，显示在输入框下方。 */
const localError = ref<string | null>(null);

/** 对话框打开前获得焦点的元素——关闭时归还。 */
let previouslyFocused: HTMLElement | null = null;

/** 打开时初始化输入路径 + 记录焦点；关闭时归还焦点。 */
watch(
  () => props.visible,
  async (v) => {
    if (v) {
      inputPath.value = game.scanRootDir ?? game.mainGameDir ?? '';
      browsing.value = false;
      localError.value = null;
      previouslyFocused = document.activeElement instanceof HTMLElement
        ? document.activeElement
        : null;
      await nextTick();
      inputRef.value?.focus();
    } else if (previouslyFocused && previouslyFocused.isConnected) {
      previouslyFocused.focus();
      previouslyFocused = null;
    }
  },
);

function close(): void {
  emit('update:visible', false);
}

/** 确认「扫描」——以输入路径为主目录（统一走 source.scan，传输失败保留框体）。 */
async function onConfirm(): Promise<void> {
  const target = inputPath.value.trim();
  if (!target) return;
  localError.value = null;
  game.clearMauiError();
  game.clearLoadGameError();
  const ok = await props.source.scan(target);
  if (!ok) {
    localError.value = '扫描失败——请确认 C# server 正在运行（检查连接设置）。';
    return;
  }
  close();
}

// ---------- 页面内目录浏览（source.listDirs 自动填框） ----------
const browsing = ref(false);
const browsePath = ref<string>('');
const browseDirs = ref<string[]>([]);
const browseParent = ref<string | null>(null);
const browseInput = ref<string>('');

/** 切换目录浏览——首次打开以当前输入路径为起点加载子目录。 */
async function toggleBrowse(): Promise<void> {
  if (browsing.value) {
    browsing.value = false;
    return;
  }
  browsing.value = true;
  await enterBrowse(inputPath.value.trim());
}

/** 进入目录浏览指定路径。 */
async function enterBrowse(dir: string): Promise<void> {
  const target = dir.trim();
  browsePath.value = target;
  browseInput.value = target;
  const result: DirListResult | null = await props.source.listDirs(target);
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
  localError.value = null;
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

/** 使用浏览中的目录作为主目录 → 退出浏览，填入手动输入框。 */
function onUseBrowsePath(): void {
  if (!browsePath.value.trim()) return;
  inputPath.value = browsePath.value;
  browsing.value = false;
  localError.value = null;
}

/** Escape（桌面键盘等价于 Android 返回键）——关闭对话框。 */
function onDocKeydown(e: KeyboardEvent): void {
  if (e.key === 'Escape' && props.visible) close();
}

const inputRef = ref<HTMLInputElement | null>(null);
onMounted(() => document.addEventListener('keydown', onDocKeydown));
onUnmounted(() => document.removeEventListener('keydown', onDocKeydown));
</script>

<template>
  <Transition name="dialog">
    <div
      v-if="visible"
      class="cd-overlay"
      @click.self="close"
    >
      <div
        class="cd-dialog"
        role="dialog"
        aria-modal="true"
        aria-label="更改目录"
      >
        <h2 class="cd-title">更改目录</h2>
        <p class="cd-hint">输入游戏主目录（可包含多个游戏），或用「浏览」选择目录。</p>

        <!-- 手动路径输入 + 目录浏览辅助 -->
        <div class="cd-path-row">
          <input
            ref="inputRef"
            v-model="inputPath"
            class="cd-dir-input"
            type="text"
            placeholder="输入游戏主目录，例如 D:\games"
            spellcheck="false"
            @keyup.enter="onConfirm"
          />
          <button
            type="button"
            class="btn-outline cd-help-btn"
            @click="toggleBrowse"
          >{{ browsing ? '收起浏览' : '浏览…' }}</button>
        </div>

        <p v-if="localError" class="cd-error">{{ localError }}</p>

        <!-- 页面内目录浏览（source.listDirs） -->
        <div v-if="browsing" class="cd-browser">
          <div class="cd-browser-path-row">
            <button
              type="button"
              class="btn-outline"
              :disabled="!browseParent"
              @click="onBrowseUp"
            >⬆ 上级</button>
            <input
              v-model="browseInput"
              class="cd-dir-input"
              type="text"
              placeholder="输入目录路径后回车"
              spellcheck="false"
              @keyup.enter="onBrowseGo"
            />
            <button type="button" class="btn-primary" @click="onBrowseGo">前往</button>
          </div>
          <div class="cd-browser-dirs">
            <ul v-if="browseDirs.length" class="cd-dir-list">
              <li v-for="d in browseDirs" :key="d">
                <button type="button" class="cd-dir-row" @click="onPickDir(d)">
                  <span class="cd-dir-icon" aria-hidden="true">📁</span>
                  <span class="cd-dir-name">{{ d }}</span>
                  <span class="cd-dir-arrow" aria-hidden="true">›</span>
                </button>
              </li>
            </ul>
            <div v-else class="cd-browser-empty">此目录下没有子目录</div>
          </div>
          <div class="cd-browser-footer">
            <span class="cd-browser-path" :title="browsePath">{{ browsePath || '(空路径)' }}</span>
            <button
              type="button"
              class="btn-primary"
              :disabled="!browsePath.trim()"
              @click="onUseBrowsePath"
            >使用此目录</button>
          </div>
        </div>

        <div class="cd-actions">
          <button type="button" class="btn-outline cd-btn" @click="close">取消</button>
          <button
            type="button"
            class="btn-primary cd-btn"
            :disabled="!inputPath.trim()"
            @click="onConfirm"
          >扫描</button>
        </div>
      </div>
    </div>
  </Transition>
</template>

<style scoped>
.cd-overlay {
  position: fixed;
  inset: 0;
  background: var(--color-overlay);
  display: flex;
  align-items: center;
  justify-content: center;
  z-index: 1000;
  padding: var(--space-4);
}
.cd-dialog {
  background: var(--color-surface);
  border: none;
  border-radius: var(--radius-surface);
  box-shadow: var(--elevation-menu);
  padding: var(--space-5);
  max-width: 460px;
  width: 100%;
  display: flex;
  flex-direction: column;
  gap: var(--space-3);
}
.cd-title {
  margin: 0;
  font-size: var(--font-size-lg);
  font-weight: 600;
  color: var(--color-text);
}
.cd-hint {
  margin: 0;
  font-size: var(--font-size-sm);
  color: var(--color-text-muted);
}
.cd-path-row {
  display: flex;
  gap: var(--space-2);
  width: 100%;
}
.cd-dir-input {
  flex: 1;
  min-width: 0;
  background: var(--color-bg);
  color: var(--color-text);
  border: 1px solid var(--color-border);
  padding: 6px var(--space-2);
  border-radius: var(--radius-control);
  font-family: var(--font-mono);
  font-size: var(--font-size-sm);
}
.cd-dir-input:disabled {
  opacity: 0.6;
}
.cd-help-btn {
  flex-shrink: 0;
}
.cd-error {
  margin: 0;
  font-size: var(--font-size-sm);
  color: var(--color-danger);
}
.cd-browser {
  display: flex;
  flex-direction: column;
  gap: var(--space-2);
}
.cd-browser-path-row {
  display: flex;
  gap: var(--space-2);
  width: 100%;
}
.cd-browser-dirs {
  background: var(--color-surface);
  border: 1px solid var(--color-border);
  border-radius: var(--radius-control);
  overflow: hidden;
}
.cd-dir-list {
  list-style: none;
  margin: 0;
  padding: var(--space-1) 0;
  max-height: 40vh;
  overflow-y: auto;
}
.cd-dir-row {
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
.cd-dir-row:hover {
  background: var(--state-layer-hover);
}
.cd-dir-icon {
  color: var(--color-text-muted);
}
.cd-dir-name {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.cd-dir-arrow {
  color: var(--color-text-muted);
  justify-self: end;
}
.cd-browser-empty {
  padding: var(--space-4);
  color: var(--color-text-muted);
  font-size: var(--font-size-sm);
  text-align: center;
}
.cd-browser-footer {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: var(--space-2);
}
.cd-browser-path {
  flex: 1;
  min-width: 0;
  color: var(--color-text-muted);
  font-family: var(--font-mono);
  font-size: var(--font-size-sm);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.cd-actions {
  display: flex;
  justify-content: flex-end;
  gap: var(--space-2);
  margin-top: var(--space-1);
}
.cd-actions .cd-btn {
  font-weight: 500;
  min-height: 40px;
}
</style>