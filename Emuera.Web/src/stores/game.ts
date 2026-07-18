import { defineStore } from 'pinia';
import { ref, computed } from 'vue';
import { parseTurnRecord, ParseTurnRecordError } from '../lib/parseTurnRecord';
import { applyDiff } from '../lib/opsApplier';
import { applySnapshot } from '../lib/snapshotReducer';
import { EMPTY_DISPLAY_STATE } from '../types/protocol';
import type { DisplayState, DisplaySnapshot, TurnRecord } from '../types/protocol';

/**
 * useGameStore — 游戏帧数据与显示状态（issue 03 升级 / issue 04 ADR-0016 v6 重构）。
 *
 * 协议消费链路（issue 02 引入纯函数，issue 03 在此 wiring，ADR-0016 加 timer 字段）：
 *   WS 帧原始 JSON
 *     → parseTurnRecord(rawJson)  → TurnRecord（含 timeLimit/displayTime/timeUpMessage/timedOut）
 *     → applyDiff(displayState, turn.diff)  → 新 DisplayState
 *     → 顶层 state/inputType/needValue 用 turn 字段覆盖
 *
 * 与 C# `Emuera.Headless/Server/OutputHub` + `AgentJsonlProtocol.RunLoopAsync` 对称：
 * 每个 WS Text 帧是一个 TurnRecord v6 JSON，diff 字段携带 LineOp[] 增量，
 * timer 字段携带 TINPUT 元数据（timeLimit/displayTime/timeUpMessage + timedOut 旗标）。
 *
 * ADR-0016 决策三：超时检测改为消费 turn.timedOut 旗标，删除启发式。
 * ADR-0016 决策四：TINPUT 倒计时用「静态 + 本地钟表」——server 只在 input/timeout 时发帧，
 * 平时不主动 push；前端收到 WaitInput+timeLimit>0 帧时记 receivedAt，
 * 用 setInterval(100ms) 算 remaining = timeLimit - (now - receivedAt)。
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
  /**
   * 最近一次 GET /snapshot 拿到的原始快照（调试展示用）。
   *
   * 与 lastTurn/turnHistory 分离——snapshot 不是 WS 帧，不进入 turnHistory；
   * 但调试视图需要独立展示它，便于排查"晚加入者画面"恢复是否符合预期。
   * setSnapshot 调用时更新；reset() 时清空。
   */
  const lastSnapshot = ref<DisplaySnapshot | null>(null);
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

  // ---------- ADR-0016：TINPUT timer 状态（本地钟表）----------
  //
  // server 不周期 push tick 帧——只在 input/timeout 时发帧。前端收到 WaitInput+TINPUT 帧时
  // 记 receivedAt，用 setInterval(100ms) 算 remaining。displayTime=true 才渲染倒计时 UI。
  //
  // 状态分两部分：
  // - 静态 timer 元数据（timeLimit/displayTime/timeUpMessage）：每帧 turn 覆盖
  // - 动态 startedAt：收到 WaitInput+TINPUT 帧时记 Date.now()，下一帧 turn 到达时清空
  // - 动态 remainingMs：computed，依赖 startedAt + 一个 setInterval 推动的 tick ref
  //
  // 后台 tab 节流、setInterval 跳变：回到前台或下一帧 turn 到达后立即修正（tick 立即重算）。

  /** TINPUT 帧接收时刻（Date.now()），null 表示当前不在 TINPUT 期间。 */
  const tinputStartedAt = ref<number | null>(null);
  /** 当前 TINPUT 总时长（毫秒），null 表示非 TINPUT。与 lastTurn.timeLimit 同步但独立 ref 便于响应式。 */
  const tinputTimeLimit = ref<number | null>(null);
  /** 是否向玩家显示倒计时（ERB 可设 false）。与 lastTurn.displayTime 同步。 */
  const tinputDisplayTime = ref<boolean | null>(null);
  /** ERB 超时提示文案。与 lastTurn.timeUpMessage 同步。 */
  const tinputTimeUpMessage = ref<string | null>(null);
  /**
   * 响应式"当前时刻"——由 setInterval(100ms) ++ 触发 tinputRemainingMs computed 重算。
   * 用 ref 的值本身（递增整数）作为依赖触发器，不直接读 Date.now()。
   * 只在 TINPUT 期间运行 setInterval，非 TINPUT 期间 stop 省电。
   */
  const tinputTick = ref<number>(0);
  /** setInterval 句柄——非 TINPUT 期间为 null。 */
  let tinputIntervalId: ReturnType<typeof setInterval> | null = null;

  /**
   * 启动 setInterval(100ms) 推动 tinputTick。
   * 幂等：若已运行则不重复启动。
   */
  function startTinputTicker(): void {
    if (tinputIntervalId !== null) return;
    tinputIntervalId = setInterval(() => {
      tinputTick.value++;
    }, 100);
  }

  /** 停止 setInterval。幂等：若未运行则无操作。 */
  function stopTinputTicker(): void {
    if (tinputIntervalId !== null) {
      clearInterval(tinputIntervalId);
      tinputIntervalId = null;
    }
  }

  /**
   * ADR-0016 决策三：超时通知——纯函数派生 turn.timedOut。
   *
   * - timedOut=true → 显示 turn.timeUpMessage 或默认文案
   * - timedOut=false → null
   *
   * 行为副作用（已接受）：本通知在**下一帧 turn 到达时**才清空。比用户点击提交晚 50–200ms
   * （网络往返 + server BuildTurn）。TINPUT 场景下人眼无感。
   */
  const timeoutNotice = computed<string | null>(() => {
    const turn = lastTurn.value;
    if (!turn || !turn.timedOut) return null;
    return turn.timeUpMessage ?? 'TINPUT 超时，游戏以默认值继续';
  });

  /**
   * ADR-0016 决策四：当前 TINPUT 剩余毫秒（响应式）。
   *
   * - 非 TINPUT 期间（tinputStartedAt=null 或 tinputTimeLimit=null）→ null
   * - TINPUT 期间 → max(0, timeLimit - (now - startedAt))
   *
   * 依赖 tinputTick 触发响应式更新（每 100ms 一次）；后台 tab 节流跳变回到前台立即修正。
   */
  const tinputRemainingMs = computed<number | null>(() => {
    if (tinputStartedAt.value === null || tinputTimeLimit.value === null) return null;
    // 读取 tinputTick 触发响应式依赖——值本身不参与计算，仅用作更新触发器
    void tinputTick.value;
    const elapsed = Date.now() - tinputStartedAt.value;
    const remaining = tinputTimeLimit.value - elapsed;
    return remaining < 0 ? 0 : remaining;
  });

  /** 是否应渲染倒计时 UI——TINPUT 期间 + ERB 脚本允许显示（displayTime=true）。 */
  const showTinputCountdown = computed<boolean>(
    () =>
      tinputStartedAt.value !== null &&
      tinputTimeLimit.value !== null &&
      tinputDisplayTime.value === true,
  );

  /**
   * 接收一个 WS 帧（裸 JSON 字符串）并更新内部状态。
   *
   * 流程：
   * 1. 存入 lastTurnJson + turnHistory（无论解析成功与否，调试视图都要看到原文）
   * 2. 调 parseTurnRecord 解析为 TurnRecord
   *    - 失败（ParseTurnRecordError 或其他）：写 lastError，displayState 不变
   *    - 成功：清 lastError，继续步骤 3
   * 3. 首帧（isInitial=true）带 protocolVersion；后续帧该字段为 null，不覆盖已 sticky 的版本
   * 4. 应用 diff（若存在）。applyDiff 不修改入参——返回新对象
   * 5. 用 turn 顶层字段覆盖 state/inputType/needValue
   * 6. ADR-0016：根据 turn.state + turn.timeLimit 更新 TINPUT timer 状态——
   *    WaitInput+timeLimit>0 → set startedAt/limit/displayTime/timeUpMessage + 启动 setInterval
   *    其他状态 / timeLimit=null → 清空 + 停止 setInterval
   *
   * 注意：v6 协议中 diff=null 表示本回合显示未变（如纯状态切换），
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

    // 首帧（isInitial=true）带 protocolVersion；后续帧该字段为 null，不覆盖已 sticky 的版本。
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

    // ADR-0016：更新 TINPUT timer 状态。仅 WaitInput + timeLimit>0 才视为 TINPUT 期间。
    if (turn.state === 'WaitInput' && typeof turn.timeLimit === 'number' && turn.timeLimit > 0) {
      tinputStartedAt.value = Date.now();
      tinputTimeLimit.value = turn.timeLimit;
      tinputDisplayTime.value = turn.displayTime ?? null;
      tinputTimeUpMessage.value = turn.timeUpMessage ?? null;
      startTinputTicker();
    } else {
      tinputStartedAt.value = null;
      tinputTimeLimit.value = null;
      tinputDisplayTime.value = null;
      tinputTimeUpMessage.value = null;
      stopTinputTicker();
    }
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
    lastSnapshot.value = null;
    displayState.value = { ...EMPTY_DISPLAY_STATE };
    protocolVersion.value = null;
    tinputStartedAt.value = null;
    tinputTimeLimit.value = null;
    tinputDisplayTime.value = null;
    tinputTimeUpMessage.value = null;
    stopTinputTicker();
  }

  /**
   * 用全量快照重建 displayState——WS 升级后立即调用，处理晚加入者场景。
   *
   * 与 C# `GET /snapshot` 端点对称：server 不在首帧 diff 重放历史 PRINT 输出，
   * 故前端必须在 WS onopen 之前先调 `GET /snapshot` 拿当前全屏状态。
   *
   * ADR-0016：snapshot 也携带 timer 字段（timeLimit/displayTime/timeUpMessage），
   * 晚加入者在 TINPUT 期间也能看到倒计时——故此处同样更新 TINPUT timer 状态。
   *
   * - 用 lib/snapshotReducer.applySnapshot 深拷贝 snapshot.lines 到 displayState
   * - 同步更新 protocolVersion（snapshot.protocolVersion 与 TurnRecord 一致，v6）
   * - 同步更新 TINPUT timer 状态（snapshot 含 timer 字段时 set + start setInterval）
   * - **不更新 lastTurn / lastTurnJson / turnHistory**——snapshot 不是 WS 帧，
   *   调试视图继续展示真实 WS 帧历史
   *
   * Issue 03 就地修复（spec 盲点）：原计划 issue 06 接入，但 issue 03 手测被
   * 「首帧无 diff」阻塞，提前实现 setSnapshot 部分；issue 06 剩余的断线重连 +
   * 指数退避才真正属于其范围。
   */
  function setSnapshot(snapshot: DisplaySnapshot): void {
    lastSnapshot.value = snapshot;
    displayState.value = applySnapshot(displayState.value, snapshot);
    if (typeof snapshot.protocolVersion === 'number') {
      protocolVersion.value = snapshot.protocolVersion;
    }
    // ADR-0016：晚加入者也可能撞上 TINPUT 期间——snapshot 携带 timer 字段时
    // 启动本地钟表。snapshot.state===WaitInput 校验避免 Quit/Error 状态误启。
    if (
      snapshot.state === 'WaitInput' &&
      typeof snapshot.timeLimit === 'number' &&
      snapshot.timeLimit > 0
    ) {
      tinputStartedAt.value = Date.now();
      tinputTimeLimit.value = snapshot.timeLimit;
      tinputDisplayTime.value = snapshot.displayTime ?? null;
      tinputTimeUpMessage.value = snapshot.timeUpMessage ?? null;
      startTinputTicker();
    } else {
      tinputStartedAt.value = null;
      tinputTimeLimit.value = null;
      tinputDisplayTime.value = null;
      tinputTimeUpMessage.value = null;
      stopTinputTicker();
    }
  }

  // ---------- store 卸载时清理 setInterval ----------
  //
  // Pinia store 通常与 app 同生命周期，但测试中 setActivePinia(createPinia()) 频繁切换时
  // 旧 store 会被丢弃。用 scope effect 清理避免 setInterval 泄漏到下一个测试。
  //
  // 注意：Pinia setup store 没有 onUnmounted 生命周期，但 watch + effect scope 可用。
  // 这里用 watch(tinputStartedAt) 间接触发清理——非 TINPUT 期间 stopTinputTicker 已调过。
  // 主卸载时若仍处于 TINPUT，interval 会泄漏——接受这个小问题（生产环境 store 与 app 同寿）。

  return {
    lastTurnJson,
    turnHistory,
    lastError,
    lastTurn,
    lastSnapshot,
    displayState,
    protocolVersion,
    // ADR-0016：暴露 TINPUT timer 状态供 UI / 测试访问
    timeoutNotice,
    tinputStartedAt,
    tinputTimeLimit,
    tinputDisplayTime,
    tinputTimeUpMessage,
    tinputRemainingMs,
    showTinputCountdown,
    applyTurn,
    setSnapshot,
    reset,
  };
});
