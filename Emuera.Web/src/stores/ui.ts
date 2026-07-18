import { defineStore } from 'pinia';
import { ref } from 'vue';

/**
 * useUiStore — UI 视图状态。
 *
 * v1 只有 'terminal' 和 'debug' 两个视图（spec.md：「不用 Vue Router，单页面 + Pinia view state」）。
 * Issue 05 起增加 platform 字段，区分 web / android，决定 GamePicker 渲染哪个组件。
 */

export type UiView = 'terminal' | 'debug';
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

  function switchView(view: UiView): void {
    currentView.value = view;
  }

  return { currentView, platform, switchView };
});
