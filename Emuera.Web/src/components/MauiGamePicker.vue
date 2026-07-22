<script setup lang="ts">
import { useGameStore } from '../stores/game';
import { pickGameFolder } from '../lib/mauiBridge';

/**
 * MauiGamePicker——issue 09 文件选择器（MAUI 模式专用）。
 *
 * 类似网页上传文件的按钮：点击 → C# 弹原生 FolderPicker（Windows Win32 dialog / Android SAF）
 * → 选中后 C# 推 `folderPicked` 回 Vue → `useAppInit` 消息处理器调 `loadGameFromPath(path)`
 * 触发 C# hot-swap reload。用户取消时 C# 不推消息，按钮无副作用。
 *
 * 与 HTTP 模式 `GamePicker` 的区别：
 * - 无文本输入框——MAUI WebView 无法访问真实文件系统路径，必须用原生 picker
 * - 无"加载"按钮——选中目录后自动 reload（与网页上传按钮一致的单步交互）
 * - 跨平台——Windows + Android 共用此组件，C# `IJsBridge.PickFolderAsync` 平台分流
 *
 * 状态：
 * - `game.gameDir` 存在 → 显示当前路径 + "重新选择"按钮
 * - `game.gameDir` 为 null → 显示"选择游戏目录"按钮（首启动）
 * - `game.mauiError` 存在 → 显示错误提示，可关闭
 */
const game = useGameStore();

function onPick(): void {
  // 清空旧错误——新一轮选择不应残留上轮错误
  game.clearMauiError();
  pickGameFolder();
}

function onDismissError(): void {
  game.clearMauiError();
}
</script>

<template>
  <div class="maui-game-picker">
    <button class="pick-btn" @click="onPick">
      {{ game.gameDir ? '重新选择' : '选择游戏目录' }}
    </button>
    <span v-if="game.gameDir" class="current-dir">{{ game.gameDir }}</span>
    <div v-if="game.mauiError" class="error-banner">
      <span class="error-text">{{ game.mauiError }}</span>
      <button class="dismiss-btn" @click="onDismissError">×</button>
    </div>
  </div>
</template>

<style scoped>
.maui-game-picker {
  display: flex;
  align-items: center;
  gap: 8px;
  font-size: 13px;
  flex-wrap: wrap;
}
.pick-btn {
  background: #0e639c;
  color: #fff;
  border: none;
  padding: 4px 12px;
  border-radius: 3px;
  cursor: pointer;
  font-size: 13px;
  white-space: nowrap;
}
.pick-btn:hover {
  background: #1177bb;
}
.current-dir {
  color: #4ec9b0;
  font-family: ui-monospace, Consolas, monospace;
  font-size: 12px;
  /* 路径可能很长——超出时省略号 */
  max-width: 360px;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
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
  width: 100%;
}
.error-text {
  flex: 1;
  font-size: 12px;
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
