import { ref, watch } from 'vue';
import { useGameStore } from '../stores/game';

/**
 * T-025 D14：游戏目录输入框 composable——GamePicker / GamePickerMobile 共用。
 *
 * 提供 `input` ref（初始值来自 `game.gameDir`）并自动同步 `game.gameDir` 变化——
 * 快速重开失败时 `gameDir` 被清空（null），输入框同步清空以回退到空路径选择器。
 *
 * 提取原因：原 GamePicker.vue 和 GamePickerMobile.vue 各自重复了相同的
 * `ref + watch` 逻辑，逻辑变化时需霰弹式修改两处。
 */
export function useGameDirInput() {
  const game = useGameStore();
  const input = ref<string>(game.gameDir ?? '');

  watch(
    () => game.gameDir,
    (newVal) => {
      input.value = newVal ?? '';
    },
  );

  return { input };
}
