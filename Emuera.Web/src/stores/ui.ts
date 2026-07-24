import { defineStore } from 'pinia';
import { ref } from 'vue';

/**
 * useUiStore — UI 视图状态。
 *
 * v1 只有 'terminal' 和 'debug' 两个视图（spec.md：「不用 Vue Router，单页面 + Pinia view state」）。
 * Issue 05 起增加 platform 字段，区分 web / android，决定 GamePicker 渲染哪个组件。
 */

export type UiView = 'terminal' | 'debug' | 'settings';
/**
 * 运行平台——issue 05 起 GamePicker 按平台渲染不同组件：
 * - 'web'：桌面浏览器，用路径文本输入框（浏览器安全限制不暴露真实 FS 路径）
 * - 'android'：MAUI WebView 壳，调 C# `POST /native/pick-directory` 触发原生 SAF 选择器
 *
 * 检测策略（spec.md L228）：MAUI 壳会在 window 注入 `window.__EMUERA_PLATFORM__='android'`；
 * 浏览器环境无此全局变量，回落 'web'。同时检查 userAgent 含 'Android' 作 fallback
 * （开发期用 Chrome DevTools 模拟移动端调试时也走 android 路径）。
 */
export type Platform = 'web' | 'android';

/**
 * 检测当前运行平台——纯函数，便于单测。
 *
 * MAUI 壳启动时在 WebView 注入：
 *   window.__EMUERA_PLATFORM__ = 'android'
 * 浏览器（含 dev server / 桌面 Chrome / Firefox）无此全局变量，回落 'web'。
 * userAgent 含 'Android' 但无 MAUI 注入时也走 'android'（开发期模拟器 / 第三方壳兼容路径）。
 *
 * 入参显式传入，便于测试 mock；生产代码调 detectPlatform() 不传参，从浏览器环境读。
 */
export function detectPlatform(
  globalObj: any = typeof globalThis !== 'undefined' ? globalThis : {},
  userAgent: string = typeof navigator !== 'undefined' ? navigator.userAgent : '',
): Platform {
  // 优先读 MAUI 壳注入的全局变量
  if (globalObj?.__EMUERA_PLATFORM__ === 'android') return 'android';
  // Fallback：userAgent 匹配（DevTools 模拟 / 第三方壳兼容）
  if (/Android/i.test(userAgent)) return 'android';
  return 'web';
}

export const useUiStore = defineStore('ui', () => {
  // Issue 03 起 Terminal 视图正式可用——默认展示用户视角。
  // Debug 视图仍保留供协议调试（顶部 view-switch 切换）。
  const currentView = ref<UiView>('terminal');

  // Issue 05：平台检测——store 初始化时同步检测一次。
  // 平台在 app 生命周期内不会变化（不会从浏览器变成 MAUI），故 ref 不需要 setter。
  const platform = ref<Platform>(detectPlatform());

  /**
   * 虚拟滚动 Stick to bottom 标志——同时控制自动跟随与点击推进守卫。
   *
   * 初始 true（未滚动过视为在底部）；用户向上滚过后变 false，新输出不再自动拉回；
   * 滚回底部后变 true，自动跟随恢复。同一标志还用作 AnyKey/EnterKey 模式的"点击推进"
   * 守卫——`isStickyToBottom=false` 时 `onTerminalClick`/`onGlobalClick` 拒绝推进，
   * 让用户安心翻看历史。
   *
   * 跨组件共享：`useVirtualScroll` composable 内部维护同一含义的 ref，
   * 通过 `onStickyChange` 回调同步到此 store；`InputBar` 读此 store 实现全局 click 守卫。
   */
  const isStickyToBottom = ref<boolean>(true);

  function switchView(view: UiView): void {
    currentView.value = view;
  }

  /**
   * 设置 isStickyToBottom——由 `useVirtualScroll` 的 onStickyChange 回调写入，
   * 或由 `clear_screen` 等需要强制回到底部的事件主动置 true。
   */
  function setStickyToBottom(v: boolean): void {
    isStickyToBottom.value = v;
  }

  return { currentView, platform, switchView, isStickyToBottom, setStickyToBottom };
});
