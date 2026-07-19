import { defineStore } from 'pinia';
import { ref, computed } from 'vue';
import { parseTurnRecord, ParseTurnRecordError } from '../lib/parseTurnRecord';
import { applyDiff } from '../lib/opsApplier';
import { applySnapshot } from '../lib/snapshotReducer';
import { EMPTY_DISPLAY_STATE } from '../types/protocol';
import type { DisplayState, DisplaySnapshot, TurnRecord } from '../types/protocol';
import { useConnectionStore } from './connection';

/**
 * Issue 05：游戏目录持久化 key（localStorage）。
 *
 * App.vue 挂载时读此 key 与 GET /state 的 gameDir 比对：
 * - 同 → 直接 connect()（不重启当前局）
 * - 异 → loadGame(localStorage value) 切换
 */
const GAME_DIR_STORAGE_KEY = 'emuera.gameDir';

/**
 * Issue 05：结构化错误码——与 C# `KestrelGameServer.HandleLoadGameAsync` 错误契约对齐。
 *
 * 路径级错误（400）：
 * - DIR_NOT_FOUND：目录不存在
 * - MISSING_CSV：缺 csv 子目录
 * - MISSING_ERB：缺 erb 子目录
 * - MISSING_GAME_DIR / INVALID_JSON：请求体错误（前端构造时不应触发，作兜底）
 *
 * 加载级错误（500）：
 * - LOAD_FAILED：Preload.Load / ERB 解析等失败
 */
export type LoadGameErrorCode =
  | 'DIR_NOT_FOUND'
  | 'MISSING_CSV'
  | 'MISSING_ERB'
  | 'MISSING_GAME_DIR'
  | 'INVALID_JSON'
  | 'LOAD_FAILED';

/** Issue 05：loadGame 抛出的结构化错误——UI 按代码映射提示文案。 */
export class LoadGameError extends Error {
  readonly code: LoadGameErrorCode;
  readonly httpStatus: number;

  constructor(code: LoadGameErrorCode, message: string, httpStatus: number) {
    super(message);
    this.name = 'LoadGameError';
    this.code = code;
    this.httpStatus = httpStatus;
  }
}

/**
 * 错误码 → UI 文案映射——纯函数，便于单测。
 *
 * 与 C# `KestrelGameServer.HandleLoadGameAsync` 的 5 个 code 对称：
 * - DIR_NOT_FOUND：目录不存在
 * - MISSING_CSV：缺 csv 目录
 * - MISSING_ERB：缺 erb 目录
 * - LOAD_FAILED：加载游戏失败（ERB 损坏等）
 * - MISSING_GAME_DIR / INVALID_JSON：请求格式错误（前端 bug，不应让用户看到原文）
 */
export function mapLoadGameErrorCode(code: LoadGameErrorCode): string {
  switch (code) {
    case 'DIR_NOT_FOUND':
      return '目录不存在，请检查路径';
    case 'MISSING_CSV':
      return '缺少 csv 子目录，请确认是 Emuera 游戏目录';
    case 'MISSING_ERB':
      return '缺少 erb 子目录，请确认是 Emuera 游戏目录';
    case 'LOAD_FAILED':
      return '游戏加载失败（ERB 文件可能损坏），请查看服务器日志';
    case 'MISSING_GAME_DIR':
    case 'INVALID_JSON':
      return '请求格式错误，请刷新页面重试';
  }
}

/**
 * 从 localStorage 读 gameDir——纯函数，便于单测。
 *
 * SSR / 旧浏览器无 localStorage 时返 null。读到非 string 值（被外部污染）也返 null。
 */
export function readGameDirFromStorage(
  storage: Storage | null = typeof localStorage !== 'undefined' ? localStorage : null,
): string | null {
  if (!storage) return null;
  try {
    const v = storage.getItem(GAME_DIR_STORAGE_KEY);
    return typeof v === 'string' && v.length > 0 ? v : null;
  } catch {
    // localStorage 访问被禁用（隐私模式 / 跨域）——返 null 不抛
    return null;
  }
}

/** 写 gameDir 到 localStorage——失败时静默（隐私模式 / 容量超限）。 */
function writeGameDirToStorage(dir: string): void {
  try {
    localStorage.setItem(GAME_DIR_STORAGE_KEY, dir);
  } catch {
    // 静默——gameDir ref 仍更新，只是不持久化
  }
}

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

  // ---------- Issue 05：游戏目录与重载状态 ----------
  //
  // gameDir 持久化到 localStorage，App.vue 挂载时与 GET /state 比对决定 connect / loadGame。
  // reloadStatus 标记 /load-game 序列进行中——UI 禁用切换按钮、显示 loading。
  // loadGame(dir) 是跨 store 复合动作：disconnect → POST /load-game → connect。
  //   成功：reloadStatus='idle'，gameDir 更新，WS 重连入新游戏
  //   失败：reloadStatus='idle'，loadGameError 写入，UI 按错误码映射提示
  //         路径级错误（400）不拆旧 session——server 端 dispose 旧前拦下，前端 connect() 回旧局
  //         加载级错误（500）旧 session 已被 dispose——connect() 时 server 会建新空 session
  /** 当前游戏目录（持久化到 localStorage）。null 表示尚未加载过任何游戏。 */
  const gameDir = ref<string | null>(readGameDirFromStorage());
  /** /load-game 进行中标志——UI 禁用切换按钮 + 显示 loading。 */
  const reloadStatus = ref<'idle' | 'loading'>('idle');
  /** 最近一次 loadGame 错误——UI 展示结构化提示。null 表示无错误或已清除。 */
  const loadGameError = ref<LoadGameError | null>(null);
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

  // ---------- Issue 12：游戏窗口布局元信息 ----------
  //
  // 这四个字段从 C# `GET /state` 响应读取——驱动 TerminalDisplay 的固定宽度布局：
  // - windowWidth：游戏 emuera.config 设定的窗口像素宽度（ConfigCode.WindowX，默认 760）
  // - fontSize：字体像素大小（ConfigCode.FontSize，默认 18）
  // - lineHeight：行距像素（ConfigCode.LineHeight，默认 19；绝对像素，非比例）
  // - gameColumns：游戏可绘制字符列数 = DrawableWidth / (FontSize/2)，与 CLI 模式
  //   TerminalLineFormatter.GetGameColumnWidth() 一致。前端用此值以 CSS ch 单位设置
  //   容器宽度——浏览器 monospace 字符宽度（≈0.6em）与 GDI（FontSize/2=0.5em）不同，
  //   按像素宽度布局会让字符画溢出；按字符列数 × 1ch 布局则字体宽度自适应。
  //
  // null 表示尚未从 server 读取——TerminalDisplay 用默认值 fallback。
  // App.vue onMounted + loadGame 成功后会调 setGameLayout 更新这些字段。
  // 新游戏可能有不同 emuera.config——切换游戏后必须重读 GET /state。
  /** 游戏窗口像素宽度（来自 ConfigCode.WindowX，默认 760）。null 表示尚未读取。 */
  const windowWidth = ref<number | null>(null);
  /** 游戏字体像素大小（来自 ConfigCode.FontSize，默认 18）。null 表示尚未读取。 */
  const fontSize = ref<number | null>(null);
  /** 游戏行距像素（来自 ConfigCode.LineHeight，默认 19）。null 表示尚未读取。 */
  const lineHeight = ref<number | null>(null);
  /** 游戏可绘制字符列数（DrawableWidth / (FontSize/2)，默认按 760/18 算 = 84）。null 表示尚未读取。 */
  const gameColumns = ref<number | null>(null);

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

  // ---------- Issue 05：loadGame 跨 store 复合动作 ----------
  //
  // 延迟解引用 connection store——Pinia 单例模式允许 store 互相 use，
  // 调用 loadGame 时才 useConnectionStore()，避免初始化期循环依赖。
  // spec L221 流程：disconnect → POST /load-game → connect
  //   - disconnect 先断旧 WS（用户视角立即"loading"）
  //   - POST /load-game 让 C# server 原子重载游戏目录（dispose 旧 session + Preload.Clear
  //     + 重建 ConfigData + Preload.Load + 建新 session）
  //   - connect 让 WS 重新升级到新 session（onopen 后 refreshSnapshot 拿新游戏画面）
  //
  // 错误恢复：
  //   - 路径级错误（400 DIR_NOT_FOUND/MISSING_CSV/MISSING_ERB）：server 端拦在 dispose 前，
  //     旧 session 还活着——前端 connect() 回旧局，UI 显示错误提示
  //   - 加载级错误（500 LOAD_FAILED）：server 端已 dispose 旧 session + Preload.Clear，
  //     新 session 未建——前端 connect() 时 server 建新空 session，旧画面丢失（接受）
  //   - 网络错误（fetch 抛错）：视为 LOAD_FAILED 同样处理
  async function loadGame(dir: string): Promise<void> {
    // 二次进入保护——reload 进行中时拒绝（UI 也应禁用按钮，作兜底）
    if (reloadStatus.value === 'loading') return;
    if (!dir || !dir.trim()) {
      loadGameError.value = new LoadGameError('MISSING_GAME_DIR', 'gameDir 为空', 400);
      return;
    }
    const trimmed = dir.trim();
    reloadStatus.value = 'loading';
    loadGameError.value = null;

    // 延迟解引用 connection store——避免 store 初始化期循环依赖
    const conn = useConnectionStore();
    const httpBase = conn.deriveHttpBase(conn.serverUrl);

    // 步骤 1：disconnect 旧 WS——用户视角立即"loading"
    // 注意：server 端旧 session 还在，/load-game 内部会 dispose 它
    conn.disconnect();
    // 清空本地显示状态——避免新游戏加载时画面闪烁旧帧
    reset();

    // 步骤 2：POST /load-game {gameDir}
    try {
      const resp = await fetch(`${httpBase}/load-game`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ gameDir: trimmed }),
      });

      if (resp.status === 200) {
        // 成功——更新 gameDir + 持久化
        gameDir.value = trimmed;
        writeGameDirToStorage(trimmed);
        // Issue 12：重读 GET /state 更新窗口布局元信息——新游戏可能有不同 emuera.config
        // （WindowX/FontSize/LineHeight）。失败不阻塞切换——保持原 layout 字段。
        try {
          const stateResp = await fetch(`${httpBase}/state`);
          if (stateResp.status === 200) {
            const stateBody = await stateResp.json();
            setGameLayout({
              windowWidth: typeof stateBody?.windowWidth === 'number' ? stateBody.windowWidth : null,
              fontSize: typeof stateBody?.fontSize === 'number' ? stateBody.fontSize : null,
              lineHeight: typeof stateBody?.lineHeight === 'number' ? stateBody.lineHeight : null,
              gameColumns: typeof stateBody?.gameColumns === 'number' ? stateBody.gameColumns : null,
            });
          }
        } catch {
          // 忽略——layout 字段更新失败不影响游戏切换
        }
        // 步骤 3：connect 新 session（server 已建好新 session 等待 WS 升级）
        await conn.connect(conn.serverUrl);
      } else {
        // 错误——解析 {error:{code,message}} 结构
        let code: LoadGameErrorCode = 'LOAD_FAILED';
        let message = `HTTP ${resp.status}`;
        try {
          const body = await resp.json();
          if (body?.error?.code) code = body.error.code as LoadGameErrorCode;
          if (body?.error?.message) message = body.error.message;
        } catch {
          // body 非 JSON——用默认 code/message
        }
        loadGameError.value = new LoadGameError(code, message, resp.status);
        // 错误时仍 connect 回旧 session（路径级错误）/ 建空 session（加载级错误）
        await conn.connect(conn.serverUrl);
      }
    } catch (e) {
      // 网络错误——fetch 抛错
      loadGameError.value = new LoadGameError(
        'LOAD_FAILED',
        e instanceof Error ? e.message : String(e),
        0,
      );
      // 尝试重连——可能 server 重启中
      try {
        await conn.connect(conn.serverUrl);
      } catch {
        // 重连也失败——保持 loadGameError 状态，UI 显示"无法连接服务器"
      }
    } finally {
      reloadStatus.value = 'idle';
    }
  }

  /** Issue 05：清空 loadGameError——用户关闭错误提示时调用。 */
  function clearLoadGameError(): void {
    loadGameError.value = null;
  }

  /**
   * Issue 12：写入窗口布局元信息——App.vue onMounted + loadGame 成功后调用。
   *
   * 入参 null / undefined 表示 server 响应缺失该字段——保持原值不动，
   * 让前端 fallback 到上一已知值或默认 760/18/19/94。
   */
  function setGameLayout(opts: {
    windowWidth?: number | null;
    fontSize?: number | null;
    lineHeight?: number | null;
    gameColumns?: number | null;
  }): void {
    if (typeof opts.windowWidth === 'number') windowWidth.value = opts.windowWidth;
    if (typeof opts.fontSize === 'number') fontSize.value = opts.fontSize;
    if (typeof opts.lineHeight === 'number') lineHeight.value = opts.lineHeight;
    if (typeof opts.gameColumns === 'number') gameColumns.value = opts.gameColumns;
  }

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
    // Issue 05：游戏目录与重载状态
    gameDir,
    reloadStatus,
    loadGameError,
    loadGame,
    clearLoadGameError,
    // Issue 12：窗口布局元信息
    windowWidth,
    fontSize,
    lineHeight,
    gameColumns,
    setGameLayout,
  };
});
