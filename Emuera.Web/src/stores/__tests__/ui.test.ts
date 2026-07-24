import { describe, it, expect, beforeEach } from 'vitest';
import { setActivePinia, createPinia } from 'pinia';
import { useUiStore, detectPlatform } from '../ui';

/**
 * useUiStore / detectPlatform 单测——issue 05 平台检测。
 *
 * detectPlatform 纯函数（spec L228）：
 * - MAUI 壳注入 `window.__EMUERA_PLATFORM__='android'` → 'android'
 * - 浏览器无注入 + userAgent 不含 Android → 'web'
 * - userAgent 含 Android 但无 MAUI 注入（DevTools 模拟 / 第三方壳）→ 'android'
 * - 优先级：MAUI 注入 > userAgent
 *
 * useUiStore.platform：
 * - 默认 = detectPlatform() 结果（浏览器环境通常 'web'）
 * - 平台在 app 生命周期内不变（无 setter）
 */
describe('detectPlatform 纯函数', () => {
  it('MAUI 壳注入 __EMUERA_PLATFORM__=android → android', () => {
    expect(detectPlatform({ __EMUERA_PLATFORM__: 'android' }, 'Mozilla/5.0')).toBe('android');
  });

  it('浏览器无注入 + userAgent 不含 Android → web', () => {
    expect(detectPlatform({}, 'Mozilla/5.0 (Windows NT 10.0; Win64; x64)')).toBe('web');
    expect(detectPlatform({}, 'Mozilla/5.0 (Macintosh)')).toBe('web');
    expect(detectPlatform({}, 'Mozilla/5.0 (X11; Linux x86_64)')).toBe('web');
  });

  it('userAgent 含 Android 但无 MAUI 注入 → android（DevTools 模拟 / 第三方壳）', () => {
    expect(detectPlatform({}, 'Mozilla/5.0 (Linux; Android 13)')).toBe('android');
    expect(detectPlatform({}, 'Mozilla/5.0 (Linux; U; Android 11)')).toBe('android');
  });

  it('MAUI 注入优先于 userAgent（iOS 模拟 Android 边界场景）', () => {
    // 即使 userAgent 不是 Android，MAUI 注入仍优先
    expect(detectPlatform({ __EMUERA_PLATFORM__: 'android' }, 'Mozilla/5.0 (iPhone)')).toBe('android');
  });

  it('MAUI 注入非 android 值 → 忽略，回落到 userAgent 检测', () => {
    // 防御性：MAUI 壳误注入 'ios' 等非支持值时，回落到 userAgent
    expect(detectPlatform({ __EMUERA_PLATFORM__: 'ios' }, 'Mozilla/5.0 (Linux; Android 13)')).toBe('android');
    expect(detectPlatform({ __EMUERA_PLATFORM__: 'ios' }, 'Mozilla/5.0 (Windows)')).toBe('web');
  });

  it('空 globalObj / 空 userAgent → web', () => {
    expect(detectPlatform({}, '')).toBe('web');
    expect(detectPlatform(null, '')).toBe('web');
    expect(detectPlatform(undefined, undefined)).toBe('web');
  });

  it('__EMUERA_PLATFORM__ 为非 string 值（如 number）→ 忽略，回落到 userAgent', () => {
    expect(detectPlatform({ __EMUERA_PLATFORM__: 1 }, 'Mozilla/5.0 (Windows)')).toBe('web');
    expect(detectPlatform({ __EMUERA_PLATFORM__: true }, 'Mozilla/5.0 (Linux; Android 13)')).toBe('android');
  });
});

describe('useUiStore platform', () => {
  beforeEach(() => setActivePinia(createPinia()));

  it('store 初始化时调用 detectPlatform——浏览器环境默认 web', () => {
    // jsdom 环境 navigator.userAgent 不含 Android，也无 MAUI 注入
    const ui = useUiStore();
    expect(ui.platform).toBe('web');
  });

  it('currentView 默认 terminal', () => {
    const ui = useUiStore();
    expect(ui.currentView).toBe('terminal');
  });

  it('switchView 切换到 debug', () => {
    const ui = useUiStore();
    ui.switchView('debug');
    expect(ui.currentView).toBe('debug');
    ui.switchView('terminal');
    expect(ui.currentView).toBe('terminal');
  });

  it('switchView 切换到 settings', () => {
    const ui = useUiStore();
    ui.switchView('settings');
    expect(ui.currentView).toBe('settings');
    ui.switchView('terminal');
    expect(ui.currentView).toBe('terminal');
  });
});

// ---------- isStickyToBottom（虚拟滚动 + sticky 守卫） ----------
//
// spec.md 决策四：isStickyToBottom 跨组件共享——composable 内部维护，
// 通过 onStickyChange 回调同步到 useUiStore；InputBar 读 store 实现全局 click 守卫。
describe('useUiStore isStickyToBottom', () => {
  beforeEach(() => setActivePinia(createPinia()));

  it('初始值为 true（未滚动过视为在底部）', () => {
    const ui = useUiStore();
    expect(ui.isStickyToBottom).toBe(true);
  });

  it('setStickyToBottom(false) 翻看历史', () => {
    const ui = useUiStore();
    ui.setStickyToBottom(false);
    expect(ui.isStickyToBottom).toBe(false);
  });

  it('setStickyToBottom(true) 滚回底部', () => {
    const ui = useUiStore();
    ui.setStickyToBottom(false);
    expect(ui.isStickyToBottom).toBe(false);
    ui.setStickyToBottom(true);
    expect(ui.isStickyToBottom).toBe(true);
  });

  it('setStickyToBottom 幂等——重复设同值不报错', () => {
    const ui = useUiStore();
    ui.setStickyToBottom(true);
    ui.setStickyToBottom(true);
    expect(ui.isStickyToBottom).toBe(true);
    ui.setStickyToBottom(false);
    ui.setStickyToBottom(false);
    expect(ui.isStickyToBottom).toBe(false);
  });
});
