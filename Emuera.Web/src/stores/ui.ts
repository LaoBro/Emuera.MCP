import { defineStore } from 'pinia';
import { ref } from 'vue';

/**
 * useUiStore — UI 视图状态。
 *
 * v1 只有 'terminal' 和 'debug' 两个视图（spec.md：「不用 Vue Router，单页面 + Pinia view state」）。
 * 后续 Route C 会扩展 showVirtualButtons / platform 等字段。
 */
export type UiView = 'terminal' | 'debug';

export const useUiStore = defineStore('ui', () => {
  // Issue 03 起 Terminal 视图正式可用——默认展示用户视角。
  // Debug 视图仍保留供协议调试（顶部 view-switch 切换）。
  const currentView = ref<UiView>('terminal');

  function switchView(view: UiView): void {
    currentView.value = view;
  }

  return { currentView, switchView };
});
