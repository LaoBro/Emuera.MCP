<script setup lang="ts">
import { useGameStore } from '../stores/game';
import { useConnectionStore } from '../stores/connection';
import type { ButtonValue, PrintSegment, DisplayLine } from '../types/protocol';

/**
 * TerminalDisplay.vue — Emuera 终端渲染器（issue 03）。
 *
 * 输入：`game.displayState`（由 `applyDiff` 累积更新的不可变结构化状态）。
 * 输出：可视化终端——按 `lines[].entries[].segments[]` 渲染文本片段，
 *      颜色 / 对齐 / 背景色正确应用；按钮 entry 渲染为可点击元素。
 *
 * 渲染策略（与 C# HeadlessConsole 渲染模型不同）：
 * - DOM 流式布局而非绝对定位——`entries[]` 按顺序内联排列，由浏览器盒模型自动算位置。
 *   按钮的 `ButtonRef.col`/`width` 在 CLI 终端是字符列几何（供 SGR mouse hit-test），
 *   Web 端用原生 click 事件做 hit-test，故 col/width 不参与 DOM 定位。
 *   monospace 字体 + `white-space: pre-wrap` 保证 CJK 双宽字符与 C# 计算一致——
 *   多按钮行中非按钮 entry 的字符数正好填补按钮间空隙，自然对齐。
 * - segment 颜色（`color` 字段，hex 形如 "#FF0000"）直接映射 CSS `color`。
 *   bold/italic 同理。`fontname` 暂不映射（Emuera 字体名与系统字体名不一致，
 *   issue 03 不做字体替换；issue 08 字体面板会处理）。
 * - 行对齐 `align`（left/center/right）映射 CSS `text-align`。
 * - 容器背景色 `bgColor` 设置在 `.terminal` 根上。
 *
 * 按钮点击：调 `conn.sendInput(String(value))`——C# HandleWsInput 期望 string，
 * integer 按钮的 value 转 string 后发送，C# 端按 inputType 自行解析回 long。
 */
const game = useGameStore();
const conn = useConnectionStore();

/**
 * 把 ButtonValue 转 wire 字符串。
 * - number → String(num)（如 1 → "1"）
 * - string → 原值（如 "cancel" → "cancel"）
 *
 * 与 C# BuildInputJsonl 对称：value 字段始终序列化为 JSON string。
 */
function valueToWire(v: ButtonValue): string {
  return typeof v === 'number' ? String(v) : v;
}

function onButtonClick(v: ButtonValue): void {
  if (conn.status !== 'connected') return;
  conn.sendInput(valueToWire(v));
}

/**
 * 构造单个 segment 的内联 style 对象。
 * 返回 Partial<CSSStyleDeclaration> 风格的对象——Vue :style 接受驼峰键。
 *
 * `color` 是 hex 字符串（C# EmuColor.ToHex），直接赋值。
 * `bold` / `italic` 是 boolean，映射 fontWeight / fontStyle。
 * `fontname` 暂忽略——见组件头注释。
 */
function segmentStyle(s: PrintSegment): Record<string, string> {
  const style: Record<string, string> = {};
  if (s.color) style.color = s.color;
  if (s.bold) style.fontWeight = 'bold';
  if (s.italic) style.fontStyle = 'italic';
  return style;
}

/** 行 align 映射 CSS text-align。null / undefined 默认 'left'。 */
function lineAlign(line: DisplayLine): 'left' | 'center' | 'right' {
  return line.align ?? 'left';
}
</script>

<template>
  <div
    class="terminal"
    :style="{ backgroundColor: game.displayState.bgColor || undefined }"
  >
    <div v-if="game.displayState.lines.length === 0" class="terminal-empty">
      <p v-if="conn.status === 'connected'">已连接，等待游戏输出…</p>
      <p v-else>未连接服务器。请在顶部连接栏输入 WS URL 并点击「连接」。</p>
      <p v-if="game.lastError" class="terminal-error">解析错误：{{ game.lastError }}</p>
    </div>
    <div
      v-for="(line, lineIdx) in game.displayState.lines"
      :key="lineIdx"
      class="term-line"
      :style="{ textAlign: lineAlign(line) }"
    >
      <template v-for="(entry, entryIdx) in line.entries" :key="entryIdx">
        <button
          v-if="entry.button"
          class="term-btn"
          :title="`点击提交: ${valueToWire(entry.button.value)}`"
          :disabled="conn.status !== 'connected'"
          @click="onButtonClick(entry.button.value)"
        >
          <span
            v-for="(seg, segIdx) in entry.segments"
            :key="segIdx"
            class="term-seg"
            :style="segmentStyle(seg)"
          >{{ seg.text }}</span>
        </button>
        <span v-else class="term-entry">
          <span
            v-for="(seg, segIdx) in entry.segments"
            :key="segIdx"
            class="term-seg"
            :style="segmentStyle(seg)"
          >{{ seg.text }}</span>
        </span>
      </template>
    </div>
  </div>
</template>

<style scoped>
.terminal {
  /* monospace 字体——CJK 字符在浏览器中通常以 2x ASCII 宽度渲染，
     与 C# HeadlessConsole 的 display-width 计算一致，保证多按钮行对齐。 */
  font-family: ui-monospace, 'Cascadia Mono', Consolas, 'Courier New', monospace;
  font-size: 14px;
  line-height: 1.5;
  color: #d4d4d4;
  background-color: #000000;
  /* pre-wrap：保留 PRINT 输出中的空格 / 缩进，长行自动换行。
     C# 端的换行已结构化为 DisplayLine——这里不再做语义换行。 */
  white-space: pre-wrap;
  word-break: break-word;
  overflow: auto;
  /* flex: 1 + min-height: 0：在 TerminalView 的 flex column 中占据剩余空间，
     同时允许内部滚动（issue 04 起下方有 InputBar，需要让出空间）。 */
  flex: 1;
  min-height: 0;
  width: 100%;
  padding: 8px 12px;
  box-sizing: border-box;
}
.term-line {
  display: block;
  min-height: 1.5em;
  /* 行内 inline 元素从左到右流式排列——按钮 entry 与文本 entry 自然交替。 */
}
.term-entry {
  display: inline;
}
.term-seg {
  /* 纯文本片段——颜色 / 粗体 / 斜体由 :style 内联应用 */
}
.term-btn {
  /* 按钮视觉：保持文本流内联，加下划线 + 边框以便辨识。
     颜色继承父行——不破坏 segment 自定义颜色。 */
  display: inline;
  background: transparent;
  color: inherit;
  border: 1px solid #5a5a5a;
  border-radius: 2px;
  padding: 0 2px;
  margin: 0 1px;
  cursor: pointer;
  font: inherit;
  text-decoration: underline;
  /* 与文本基线对齐，避免按钮盒子顶起行高 */
  vertical-align: baseline;
}
.term-btn:hover:not(:disabled) {
  background: #0e639c;
  border-color: #0e639c;
  color: #fff;
}
.term-btn:active:not(:disabled) {
  background: #0a4a78;
  border-color: #0a4a78;
}
.term-btn:disabled {
  cursor: not-allowed;
  opacity: 0.5;
}
.terminal-empty {
  color: #888;
  font-style: italic;
  padding: 24px;
  display: flex;
  flex-direction: column;
  gap: 8px;
}
.terminal-error {
  color: #f48771;
  font-style: normal;
  font-size: 12px;
}
</style>
