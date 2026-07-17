import { defineStore } from 'pinia';
import { ref } from 'vue';
import { parseTurnRecord, ParseTurnRecordError } from '../lib/parseTurnRecord';
import { applyDiff } from '../lib/opsApplier';
import { applySnapshot } from '../lib/snapshotReducer';
import { EMPTY_DISPLAY_STATE } from '../types/protocol';
import type { DisplayState, DisplaySnapshot, TurnRecord } from '../types/protocol';

/**
 * useGameStore — 游戏帧数据与显示状态（issue 03 升级 / issue 04 加 TINPUT 检测）。
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
 * Issue 04 TINPUT 检测：
 *   C# 协议（ADR-0013/0014 锁定）不携带 timelimit / timeUpMessage / defaultResult 等
 *   结构化字段——T-004 显式暂缓 server timer 数据契约。故前端只能**启发式检测**：
 *   若上一帧 state='WaitInput' 且 needValue=true（即正在等待值输入），而本帧 turn 到达时
 *   期间用户未提交任何输入（`userInputSinceLastTurn === false`），则视为 TINPUT 超时——
 *   C# `AgentJsonlProtocol.RunLoopAsync` 内 `linkedCts.CancelAfter(InputTimeoutMs)` 触发，
 *   自动调 `console.SubmitTimeout()` 让游戏以默认值继续。前端置 `timeoutNotice` 通知 UI 显示
 *   "TINPUT 超时" 提示；用户下次提交输入时清空 notice。
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
   * 用户自上次 turn 以来是否提交过输入。
   *
   * - `markUserInput()` 调用时置 true（由 connection store 的 sendInput 触发）
   * - `applyTurn()` 末尾置 false（重置，为下一轮 turn 计数）
   *
   * 用途：检测 TINPUT 超时——若上一帧正在 WaitInput+needValue，本帧到达时此 flag 仍为
   * false，说明没有用户输入 → C# 端 CancelAfter 触发了 SubmitTimeout。
   */
  const userInputSinceLastTurn = ref<boolean>(false);

  /**
   * 最近一次 TINPUT 超时通知文案。null 表示无通知。
   *
   * 启发式检测条件（issue 04）：
   * 1. 上一帧 `displayState.state === 'WaitInput'` 且 `needValue === true`
   * 2. 本帧到达时 `userInputSinceLastTurn === false`
   *
   * 满足两条 → 置为「TINPUT 超时，游戏以默认值继续」。
   * 用户下次 `markUserInput()` 时清空。
   *
   * **协议局限**：C# 当前协议（v5）不携带 timelimit / timeUpMessage / defaultResult
   * 字段（见 spec.md「不涉及的 C# 改动」+ T-004「server timer 数据契约暂缓」）。
   * 故无法做「实时倒计时显示」或「精准 timeUpMessage 文本」——只能检测超时已发生。
   * T-004 落地协议扩展后此字段可升级为展示实际 timeUpMessage。
   */
  const timeoutNotice = ref<string | null>(null);

  /**
   * 接收一个 WS 帧（裸 JSON 字符串）并更新内部状态。
   *
   * 流程：
   * 1. 存入 lastTurnJson + turnHistory（无论解析成功与否，调试视图都要看到原文）
   * 2. 调 parseTurnRecord 解析为 TurnRecord
   *    - 失败（ParseTurnRecordError 或其他）：写 lastError，displayState 不变
   *    - 成功：清 lastError，继续步骤 3
   * 3. **TINPUT 超时检测**：若上一帧 state='WaitInput' + needValue=true，且本帧期间
   *    用户未提交输入 → 置 timeoutNotice；否则保持/清空（用户输入路径下不清空——
   *    由 markUserInput 主动清空，避免在等待 server 回应的窗口期闪烁）
   * 4. 若 turn.diff 非空：调 applyDiff(displayState, turn.diff) 得到新 lines/bgColor
   * 5. 用 turn 顶层字段覆盖 state/inputType/needValue（applyDiff 不动这些字段——
   *    它们由 TurnRecord 顶层携带，不是 diff 的一部分）
   * 6. 写 lastTurn，重置 userInputSinceLastTurn=false
   *
   * 注意：v5 协议中 diff=null 表示本回合显示未变（如纯状态切换），
   * 此时仍需更新 state/inputType/needValue（步骤 5），但不调 applyDiff。
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

    // TINPUT 超时检测：上一帧 WaitInput + needValue + 本帧期间无用户输入
    // → C# AgentJsonlProtocol linkedCts.CancelAfter 触发 SubmitTimeout
    if (
      displayState.value.state === 'WaitInput' &&
      displayState.value.needValue === true &&
      !userInputSinceLastTurn.value
    ) {
      timeoutNotice.value = 'TINPUT 超时，游戏以默认值继续';
    }
    // 用户主动输入路径：userInputSinceLastTurn=true → 不清空 timeoutNotice
    // （清空动作由 markUserInput 主动完成，确保 UI 上"已超时"提示在用户输入瞬间消失，
    //   而不是在等待 server 回应的窗口期闪烁）

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

    // 重置用户输入 flag——为下一轮 turn 计数
    userInputSinceLastTurn.value = false;
  }

  /**
   * 标记用户已提交输入——由 connection store 的 sendInput 调用。
   *
   * 作用：
   * 1. 置 `userInputSinceLastTurn=true`——下次 applyTurn 时不会被误判为 TINPUT 超时
   * 2. 清空 `timeoutNotice`——用户主动输入意味着上一回合结束，超时提示不再需要展示
   */
  function markUserInput(): void {
    userInputSinceLastTurn.value = true;
    timeoutNotice.value = null;
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
    userInputSinceLastTurn.value = false;
    timeoutNotice.value = null;
  }

  /**
   * 用全量快照重建 displayState——WS 升级后立即调用，处理晚加入者场景。
   *
   * 与 C# `GET /snapshot` 端点对称：server 不在首帧 diff 重放历史 PRINT 输出，
   * 故前端必须在 WS onopen 之前先调 `GET /snapshot` 拿当前全屏状态。
   *
   * - 用 lib/snapshotReducer.applySnapshot 深拷贝 snapshot.lines 到 displayState
   * - 同步更新 protocolVersion（snapshot.protocolVersion 与 TurnRecord 一致，v5）
   * - **不更新 lastTurn / lastTurnJson / turnHistory**——snapshot 不是 WS 帧，
   *   调试视图继续展示真实 WS 帧历史
   *
   * Issue 03 就地修复（spec 盲点）：原计划 issue 06 接入，但 issue 03 手测被
   * 「首帧无 diff」阻塞，提前实现 setSnapshot 部分；issue 06 剩余的断线重连 +
   * 指数退避才真正属于其范围。
   */
  function setSnapshot(snapshot: DisplaySnapshot): void {
    displayState.value = applySnapshot(displayState.value, snapshot);
    if (typeof snapshot.protocolVersion === 'number') {
      protocolVersion.value = snapshot.protocolVersion;
    }
  }

  return {
    lastTurnJson,
    turnHistory,
    lastError,
    lastTurn,
    displayState,
    protocolVersion,
    userInputSinceLastTurn,
    timeoutNotice,
    applyTurn,
    markUserInput,
    setSnapshot,
    reset,
  };
});
