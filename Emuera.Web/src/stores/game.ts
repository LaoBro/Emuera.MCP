import { defineStore } from 'pinia';
import { ref } from 'vue';
import { parseTurnRecord, ParseTurnRecordError } from '../lib/parseTurnRecord';
import { applyDiff } from '../lib/opsApplier';
import { EMPTY_DISPLAY_STATE } from '../types/protocol';
import type { DisplayState, TurnRecord } from '../types/protocol';

/**
 * useGameStore — 游戏帧数据与显示状态（issue 03 升级）。
 *
 * 协议消费链路（issue 02 引入纯函数，issue 03 在此 wiring）：
 *   WS 帧原始 JSON
 *     → parseTurnRecord(rawJson)  → TurnRecord
 *     → applyDiff(displayState, turn.diff)  → 新 DisplayState
 *     → 顶层 state/inputType/needValue 用 turn 字段覆盖
 *
 * 与 C# `Emuera.Headless/Server/OutputHub` + `AgentJsonlProtocol.RunLoopAsync` 对称：
 * 每个 WS Text 帧是一个 TurnRecord v5 JSON，diff 字段携带 LineOp[] 增量。
 *
 * Issue 01 保留的 raw json / history / error 字段继续供 DebugView 调试用。
 */
export const useGameStore = defineStore('game', () => {
  /** 最新 WS 帧的原始 JSON 字符串（未解析），调试视图直接 <pre> 展示。 */
  const lastTurnJson = ref<string | null>(null);
  /** 所有收到的 WS 帧原始 JSON（最新在末尾），调试用。Issue 01 不限制大小，v1 调试面板够用。 */
  const turnHistory = ref<string[]>([]);
  /** 最近一次错误信息（如 WS 帧解析失败）。 */
  const lastError = ref<string | null>(null);
  /** 最新解析后的 TurnRecord（调试用，UI 也可读 inputType 等顶层字段）。 */
  const lastTurn = ref<TurnRecord | null>(null);
  /** 当前显示状态——纯函数 applyDiff 累积应用 WS 帧 diff 后的不可变结果。 */
  const displayState = ref<DisplayState>({ ...EMPTY_DISPLAY_STATE });
  /**
   * 当前协议版本（sticky）。
   *
   * C# `AgentJsonlProtocol.BuildTurn` 只在首帧（isInitial=true）带 protocolVersion，
   * 后续帧该字段为 null（见 Emuera.Headless/Agent/AgentJsonlProtocol.cs:233）。
   * 故不能用 `lastTurn.protocolVersion` 直接派生——后续帧会让版本号闪烁归 null。
   *
   * sticky 行为：首帧收到非空版本时写入；后续帧的 null 字段不覆盖。`reset()` 时清空。
   */
  const protocolVersion = ref<number | null>(null);

  /**
   * 接收一个 WS 帧（裸 JSON 字符串）并更新内部状态。
   *
   * 流程：
   * 1. 存入 lastTurnJson + turnHistory（无论解析成功与否，调试视图都要看到原文）
   * 2. 调 parseTurnRecord 解析为 TurnRecord
   *    - 失败（ParseTurnRecordError 或其他）：写 lastError，displayState 不变
   *    - 成功：清 lastError，继续步骤 3
   * 3. 若 turn.diff 非空：调 applyDiff(displayState, turn.diff) 得到新 lines/bgColor
   * 4. 用 turn 顶层字段覆盖 state/inputType/needValue（applyDiff 不动这些字段——
   *    它们由 TurnRecord 顶层携带，不是 diff 的一部分）
   * 5. 写 lastTurn
   *
   * 注意：v5 协议中 diff=null 表示本回合显示未变（如纯状态切换），
   * 此时仍需更新 state/inputType/needValue（步骤 4），但不调 applyDiff。
   */
  function applyTurn(rawJson: string): void {
    lastTurnJson.value = rawJson;
    turnHistory.value.push(rawJson);

    let turn: TurnRecord;
    try {
      turn = parseTurnRecord(rawJson);
    } catch (e) {
      lastError.value = e instanceof ParseTurnRecordError
        ? `协议解析失败：${e.message}`
        : `解析失败：${e instanceof Error ? e.message : String(e)}`;
      return;
    }

    lastError.value = null;
    lastTurn.value = turn;

    // 首帧（isInitial=true）带 protocolVersion；后续帧该字段为 null，不覆盖已 sticky 的版本。
    // 见 C# AgentJsonlProtocol.cs:233：`isInitial ? CurrentProtocolVersion : null`。
    if (turn.protocolVersion != null) {
      protocolVersion.value = turn.protocolVersion;
    }

    // 应用 diff（若存在）。applyDiff 不修改入参——返回新对象。
    const afterDiff = turn.diff
      ? applyDiff(displayState.value, turn.diff)
      : displayState.value;

    // 用 turn 顶层字段覆盖 state/inputType/needValue
    displayState.value = {
      ...afterDiff,
      state: turn.state,
      inputType: turn.inputType ?? null,
      needValue: turn.needValue,
    };
  }

  /**
   * 重置游戏状态到空——断开连接 / 切换游戏目录 / 用户主动清屏时调用。
   * Issue 03 暂不接入；issue 06（重连）+ issue 05（game picker）会用到。
   */
  function reset(): void {
    lastTurnJson.value = null;
    turnHistory.value = [];
    lastError.value = null;
    lastTurn.value = null;
    displayState.value = { ...EMPTY_DISPLAY_STATE };
    protocolVersion.value = null;
  }

  return { lastTurnJson, turnHistory, lastError, lastTurn, displayState, protocolVersion, applyTurn, reset };
});
