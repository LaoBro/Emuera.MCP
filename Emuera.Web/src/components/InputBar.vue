<script setup lang="ts">
import { ref, computed, watch, nextTick, onMounted, onUnmounted } from 'vue';
import { useGameStore } from '../stores/game';
import { useConnectionStore } from '../stores/connection';
import { useUiStore } from '../stores/ui';

/**
 * InputBar.vue — 输入栏 + 游戏状态显示 + TINPUT 倒计时（issue 04 / ADR-0016 /
 * 虚拟滚动 + sticky 守卫扩展）。
 *
 * 职责：
 * 1. 根据 `snapshot.inputType` 分支渲染 5 种输入 UI：
 *    - `IntValue`：数字输入框 + 提交按钮，`needValue=true` 时禁止空提交
 *    - `StrValue`：文本输入框 + 提交按钮，`needValue=true` 时禁止空提交
 *    - `AnyKey`：显示"按任意键继续"提示，点击页面任意位置或按任意键提交空 input
 *    - `EnterKey`：显示"按回车继续"提示，按 Enter 提交空 input
 *    - `AnyValue`：文本输入框，允许空提交
 * 2. 显示游戏状态：
 *    - `WaitInput`：显示输入 UI（按 inputType 分支）
 *    - `Running`：显示"游戏运行中..."
 *    - `Quit`：显示"游戏结束"
 *    - `Error`：显示错误提示（`lastTurn.error` 优先于 `lastError`）
 * 3. ADR-0016：TINPUT 超时通知——`game.timeoutNotice` 非空时显示提示文案
 *    （派生自 `turn.timedOut`，超时发生时显示，下一帧 turn.timedOut=false 时自动清空）
 * 4. ADR-0016：TINPUT 实时倒计时——`<TinputCountdown />` 轻量组件渲染
 *    进度条 + 剩余秒数，由 game store 的本地钟表驱动（setInterval 500ms）
 * 5. 输入框聚焦：`WaitInput` 状态时自动聚焦输入框
 * 6. 虚拟滚动 sticky 守卫——AnyKey 模式下 `isStickyToBottom=false` 时拒绝全局 click 推进，
 *    让触屏用户向上滑动翻看历史时不会误触发"点击推进游戏"（spec.md决策三）。
 *    按钮自身 @click 不受影响（按钮区域由 `closest('button')` 跳过）。
 */
const game = useGameStore();
const conn = useConnectionStore();
const ui = useUiStore();

/** 输入框当前值——`v-model` 绑定。每次 turn 切换后清空。 */
const inputValue = ref<string>('');
/** 输入框 DOM 引用——用于 auto-focus。 */
const inputEl = ref<HTMLInputElement | null>(null);

/** 当前是否处于可输入状态——检查游戏状态 + 连接状态 + inputInFlight。 */
const canSubmit = computed<boolean>(
  () => conn.status === 'connected' && game.displayState.state === 'WaitInput' && !game.inputInFlight,
);

/** 当前是否需要值输入——IntValue/StrValue/AnyValue 三类有输入框。 */
const hasTextInput = computed<boolean>(() => {
  const t = game.displayState.inputType;
  return t === 'IntValue' || t === 'StrValue' || t === 'AnyValue';
});

/**
 * 是否显示文本输入框——自动（IntValue/StrValue/AnyValue）或手动（`⌨` 唤出）。
 * 手动模式在任意 WaitInput 下都显示输入框，覆盖"按钮之外只能键入数字的隐藏选项"。
 */
const showTextField = computed<boolean>(() => hasTextInput.value || ui.manualInputVisible);

/** 是否禁止空提交——IntValue/StrValue 在 needValue=true 时禁止空，AnyValue 始终允许空。 */
const blockEmpty = computed<boolean>(() => {
  const t = game.displayState.inputType;
  return (t === 'IntValue' || t === 'StrValue') && game.displayState.needValue;
});

/**
 * 提交当前输入。
 *
 * - 有输入框：取 inputValue，空串 + blockEmpty 时拒绝
 * - 无输入框（AnyKey/EnterKey）：直接提交空串——server 端 inputType 决定是否接受
 *
 * 提交后清空 inputValue（为下轮输入做准备）。
 */
function submit(): void {
  if (!canSubmit.value) return;

  if (hasTextInput.value) {
    const v = inputValue.value;
    if (v === '' && blockEmpty.value) return; // 禁止空提交
    conn.sendInput(v);
    inputValue.value = '';
  } else {
    // AnyKey / EnterKey：提交空串
    conn.sendInput('');
  }
}

/**
 * 监听 displayState.state 变化——进入新的 WaitInput 时：
 * 1. 清空 inputValue（避免上一轮残留）
 * 2. 等下一帧渲染后 auto-focus 输入框
 *
 * 用 watch + nextTick：displayState 是 ref，state 变化触发 watch；nextTick 等 DOM 更新
 * 后 focus 才有效（inputEl 此时已渲染）。
 */
watch(
  () => game.displayState.state,
  (newState, oldState) => {
    // 状态切换到 WaitInput（首次或后续）都清空输入框
    if (newState === 'WaitInput') {
      inputValue.value = '';
      // 只在有文本输入框的 inputType 下尝试 focus
      if (hasTextInput.value) {
        void nextTick(() => {
          inputEl.value?.focus();
        });
      }
    }
    // 状态从 WaitInput 切走时也清空（避免残留显示在下一次 WaitInput）
    if (oldState === 'WaitInput' && newState !== 'WaitInput') {
      inputValue.value = '';
    }
  },
);

/**
 * 监听 inputType 变化——同一 WaitInput 内若 inputType 改变（罕见），
 * 也需要重新 focus 输入框。
 */
watch(
  () => game.displayState.inputType,
  () => {
    if (game.displayState.state === 'WaitInput' && hasTextInput.value) {
      void nextTick(() => {
        inputEl.value?.focus();
      });
    }
  },
);

// ---------- AnyKey / EnterKey 全局键盘监听 ----------
//
// issue 04 spec：
// - AnyKey：点击页面任意位置或按任意键提交空 input
// - EnterKey：按 Enter 提交空 input
//
// 用 window-level keydown + click 监听。仅在 state=WaitInput 且 inputType 匹配时挂载。
// 用 watch 切换挂载/卸载，避免长生命周期 listener 误触发。

function isAnyKeyMode(): boolean {
  return (
    canSubmit.value &&
    game.displayState.inputType === 'AnyKey'
  );
}

/** 全局 keydown 处理——EnterKey 监听 Enter，AnyKey 监听任意键。 */
function onGlobalKeydown(e: KeyboardEvent): void {
  if (!canSubmit.value) return;
  const t = game.displayState.inputType;
  if (t === 'EnterKey') {
    if (e.key === 'Enter') {
      e.preventDefault();
      submit();
    }
  } else if (t === 'AnyKey') {
    // 任意键——但不包括修饰键（Shift/Ctrl/Alt/Meta 单独按下时不应触发）
    const modifierOnly = ['Shift', 'Control', 'Alt', 'Meta'].includes(e.key);
    if (modifierOnly) return;
    e.preventDefault();
    submit();
  }
}

/** 全局 click 处理——仅 AnyKey 模式下，点击页面任意位置触发提交。
 *  注意：当用户点击按钮 / 链接等可交互元素时不应被劫持——交由具体元素 stopPropagation。
 *
 *  虚拟滚动 sticky 守卫（spec.md决策三）：`isStickyToBottom=false` 时拒绝推进——
 *  用户翻看历史时（向上滚过），触屏滑动可能误触发 click 事件，此时不应推进游戏。
 *  滚回底部后 `isStickyToBottom=true`，click 推进恢复。
 *  按钮区域由 `closest('button')` 跳过——按钮走自身 @click，受 generation/inputInFlight 守卫。 */
function onGlobalClick(e: MouseEvent): void {
  if (!isAnyKeyMode()) return;
  // sticky 守卫——翻看历史时不推进游戏
  if (!ui.isStickyToBottom) return;
  // 让点击 Terminal 中的按钮（如有）自然走 button.onclick——不在这里 submit
  // 简单策略：若点击 target 是 <button> 元素，跳过（让按钮自身处理）
  const target = e.target as HTMLElement | null;
  if (target && target.closest('button')) return;
  e.preventDefault();
  submit();
}

onMounted(() => {
  window.addEventListener('keydown', onGlobalKeydown);
  window.addEventListener('click', onGlobalClick);
  // 首次挂载时若已在 WaitInput + 有输入框 → focus
  if (game.displayState.state === 'WaitInput' && hasTextInput.value) {
    void nextTick(() => inputEl.value?.focus());
  }
});

onUnmounted(() => {
  window.removeEventListener('keydown', onGlobalKeydown);
  window.removeEventListener('click', onGlobalClick);
});

/** 输入框 placeholder——按 inputType 提示。 */
const inputPlaceholder = computed<string>(() => {
  const t = game.displayState.inputType;
  if (t === 'IntValue') return '输入数字…';
  if (t === 'StrValue') return '输入文本…';
  if (t === 'AnyValue') return '输入文本（可空）…';
  return '';
});

/** 输入框 inputmode——IntValue 用 numeric 提示移动端键盘。 */
const inputMode = computed<'text' | 'numeric'>(() =>
  game.displayState.inputType === 'IntValue' ? 'numeric' : 'text',
);

/** 错误显示文案——lastTurn.error 优先（协议层错误），其次 lastError（解析层错误）。 */
const errorText = computed<string | null>(() => {
  if (game.lastTurn?.error) return game.lastTurn.error;
  if (game.lastError) return game.lastError;
  return null;
});

/** 提交按钮 disabled——空 + blockEmpty 时禁用。 */
const submitDisabled = computed<boolean>(() => {
  if (!canSubmit.value) return true;
  if (!hasTextInput.value) return false; // AnyKey/EnterKey 始终可点
  return inputValue.value === '' && blockEmpty.value;
});

/** 提交按钮文案——按 inputType 区分。 */
const submitLabel = computed<string>(() => {
  const t = game.displayState.inputType;
  if (t === 'IntValue' || t === 'StrValue') return '提交';
  if (t === 'AnyValue') return '提交（可空）';
  // AnyKey / EnterKey 都用"继续"
  return '继续';
});

/** 当前 inputType 中文显示——用于状态栏提示。 */
const inputTypeLabel = computed<string>(() => {
  const t = game.displayState.inputType;
  if (!t) return '';
  const labels: Record<string, string> = {
    IntValue: '数字输入',
    StrValue: '文本输入',
    AnyKey: '任意键',
    EnterKey: '回车',
    AnyValue: '任意值（可空）',
  };
  return labels[t] ?? t;
});

// ---------- ADR-0016：TINPUT 倒计时 UI 派生 ----------



</script>

<template>
  <div class="input-bar">
    <!-- TINPUT 超时通知 -->
    <div v-if="game.timeoutNotice" class="tinput-notice">
      <span class="tinput-icon">⏱</span>
      <span>{{ game.timeoutNotice }}</span>
    </div>

    <!-- 状态显示：按 displayState.state 分支 -->
    <template v-if="game.displayState.state === 'WaitInput'">
      <!-- 输入 UI：自动（文本/数字输入态）或手动（⌨ 唤出）都显示输入框 -->
      <div v-if="showTextField" class="input-row">
        <input
          ref="inputEl"
          v-model="inputValue"
          class="text-input"
          type="text"
          :inputmode="inputMode"
          :placeholder="inputPlaceholder"
          :disabled="!canSubmit"
          @keyup.enter="submit"
        />
        <button
          class="submit-btn"
          :disabled="submitDisabled"
          @click="submit"
        >
          {{ submitLabel }}
        </button>
        <span class="input-hint">{{ inputTypeLabel }}</span>
      </div>

      <div v-else-if="game.displayState.inputType === 'AnyKey'" class="anykey-prompt">
        <span>按任意键继续</span>
      </div>

      <div v-else-if="game.displayState.inputType === 'EnterKey'" class="enterkey-prompt">
        <span>按回车继续</span>
      </div>

      <div v-else class="unknown-input-type">
        <span>未知输入类型：{{ game.displayState.inputType ?? '(null)' }}</span>
      </div>
    </template>

    <div v-else-if="game.displayState.state === 'Running'" class="state-running">
      <span class="state-icon">⟳</span>
      <span>游戏运行中…</span>
    </div>

    <div v-else-if="game.displayState.state === 'Quit'" class="state-quit">
      <span class="state-icon">■</span>
      <span>游戏结束</span>
    </div>

    <div v-else-if="game.displayState.state === 'Error'" class="state-error">
      <span class="state-icon">✕</span>
      <span>错误：{{ errorText ?? '未知错误' }}</span>
    </div>

    <div v-else-if="game.displayState.state === ''" class="state-idle">
      <span>等待连接…</span>
    </div>

    <div v-else class="state-unknown">
      <span>状态：{{ game.displayState.state }}</span>
    </div>
  </div>
</template>

<style scoped>
.input-bar {
  flex-shrink: 0;
  padding: 8px 12px;
  background: #252526;
  border-top: 1px solid #3c3c3c;
  font-family: ui-monospace, Consolas, monospace;
  font-size: 13px;
  display: flex;
  flex-direction: column;
  gap: 6px;
  min-height: 40px;
}

/* TINPUT 超时通知——黄色背景 + 图标 */
.tinput-notice {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 4px 8px;
  background: #5a4a1d;
  color: #dcdcaa;
  border-radius: 3px;
  border: 1px solid #7a6a2d;
  font-size: 12px;
}
.tinput-icon {
  font-size: 14px;
}



/* 输入行：input + submit + hint */
.input-row {
  display: flex;
  align-items: center;
  gap: 8px;
}
.text-input {
  flex: 1;
  background: #1e1e1e;
  color: #e0e0e0;
  border: 1px solid #3c3c3c;
  padding: 4px 8px;
  border-radius: 3px;
  font-family: inherit;
  font-size: 13px;
  min-width: 0;
}
.text-input:focus {
  border-color: #0e639c;
  outline: none;
}
.text-input:disabled {
  opacity: 0.5;
  cursor: not-allowed;
}

.anykey-prompt,
.enterkey-prompt {
  color: #dcdcaa;
}

.submit-btn {
  background: #0e639c;
  color: #fff;
  border: none;
  padding: 4px 14px;
  border-radius: 3px;
  cursor: pointer;
  font-size: 13px;
  font-family: inherit;
}
.submit-btn:hover:not(:disabled) {
  background: #1177bb;
}
.submit-btn:active:not(:disabled) {
  background: #0a4a78;
}
.submit-btn:disabled {
  opacity: 0.4;
  cursor: not-allowed;
}

.input-hint {
  color: #888;
  font-size: 11px;
  flex-shrink: 0;
}

/* 状态显示 */
.state-running,
.state-quit,
.state-error,
.state-idle,
.state-unknown,
.unknown-input-type {
  display: flex;
  align-items: center;
  gap: 8px;
  color: #b0b0b0;
  padding: 4px 0;
}
.state-icon {
  font-size: 14px;
}
.state-running .state-icon {
  color: #dcdcaa;
  animation: spin 1.4s linear infinite;
  display: inline-block;
}
.state-quit .state-icon {
  color: #f48771;
}
.state-error .state-icon {
  color: #f48771;
}
.state-error {
  color: #f48771;
}
.unknown-input-type {
  color: #dcdcaa;
}

@keyframes spin {
  from { transform: rotate(0deg); }
  to { transform: rotate(360deg); }
}
</style>
