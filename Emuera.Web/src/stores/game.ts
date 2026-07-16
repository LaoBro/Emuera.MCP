import { defineStore } from 'pinia';
import { ref } from 'vue';

/**
 * useGameStore — 游戏帧数据。
 *
 * Issue 01 范围：只保留原始 JSON 字符串（最薄端到端通路，验证协议正确性）。
 * Issue 02 引入协议层纯函数（parseTurnRecord / applySnapshot / applyOps）后会扩展
 * 为结构化 snapshot + ops 历史。
 */
export const useGameStore = defineStore('game', () => {
  /** 最新 WS 帧的原始 JSON 字符串（未解析），调试视图直接 <pre> 展示。 */
  const lastTurnJson = ref<string | null>(null);
  /** 所有收到的 WS 帧原始 JSON（最新在末尾），调试用。Issue 01 不限制大小，v1 调试面板够用。 */
  const turnHistory = ref<string[]>([]);
  /** 最近一次错误信息（如 WS 帧解析失败）。 */
  const lastError = ref<string | null>(null);

  /**
   * 接收一个 WS 帧（裸 JSON 字符串）。
   *
   * Issue 01 不解析 JSON，只做存储；issue 02 起会调用 parseTurnRecord 等 seam。
   * 若传入字符串不是合法 JSON，写入 lastError 但仍存入历史（调试时仍可见原文）。
   */
  function applyTurn(rawJson: string): void {
    lastTurnJson.value = rawJson;
    turnHistory.value.push(rawJson);
    try {
      JSON.parse(rawJson);
    } catch (e) {
      lastError.value = e instanceof Error ? e.message : String(e);
    }
  }

  return { lastTurnJson, turnHistory, lastError, applyTurn };
});
