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
  const currentView = ref<UiView>('debug');

  function switchView(view: UiView): void {
    currentView.value = view;
  }

  return { currentView, switchView };
});
