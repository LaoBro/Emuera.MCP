import { describe, it, expect, beforeEach } from 'vitest';
import { setActivePinia, createPinia } from 'pinia';
import { useGameStore } from '../game';
import type { DisplayLine, PrintSegment } from '../../types/protocol';

/**
 * useGameStore.applyTurn 单测——集成 issue 02 的 parseTurnRecord + applyDiff。
 *
 * 这是 issue 03 的协议层 wiring 测试（spec.md 决策：Vitest 仅覆盖协议层 seam，
 * 不写 Vue 组件 e2e）。验证：
 * - 合法 WS 帧 → displayState 正确更新（lines / bgColor / state / inputType / needValue）
 * - 多帧累积：第二帧的 diff 在第一帧的基础上叠加
 * - 非法 JSON → lastError 设置，displayState 保持不变
 * - diff=null 的帧（纯状态切换）→ state 字段更新，lines 不变
 * - protocolVersion 派生：从 lastTurn 正确推导
 * - reset() 复位所有状态
 */

// ---------- 测试夹具 ----------

function seg(text: string, color?: string): PrintSegment {
  return color ? { text, color } : { text };
}

function diffLine(text: string, align?: 'left' | 'center' | 'right'): DisplayLine {
  return align
    ? { entries: [{ segments: [seg(text)] }], align, isLineEnd: true }
    : { entries: [{ segments: [seg(text)] }], isLineEnd: true };
}

/** 构造一个简单的 append diff 帧 JSON */
function appendFrameJson(
  texts: string[],
  opts: { state?: string; inputType?: string | null; needValue?: boolean; bgColor?: string | null; protocolVersion?: number | null } = {},
): string {
  const state = opts.state ?? 'WaitInput';
  const needValue = opts.needValue ?? false;
  const inputType = opts.inputType === undefined ? null : opts.inputType;
  const bgColor = opts.bgColor === undefined ? null : opts.bgColor;
  const protocolVersion = opts.protocolVersion === undefined ? null : opts.protocolVersion;
  return JSON.stringify({
    state,
    inputType,
    needValue,
    protocolVersion,
    diff: {
      lineOps: [{ type: 'append', newLines: texts.map((t) => diffLine(t)) }],
      bgColor,
    },
  });
}

/** 构造一个 clear_line_diff 帧 */
function clearLineFrameJson(clearCount: number): string {
  return JSON.stringify({
    state: 'WaitInput',
    needValue: false,
    diff: {
      lineOps: [{ type: 'clear_line_diff', clearCount }],
      bgColor: null,
    },
  });
}

/** 构造一个 clear_screen + set_bg 帧 */
function clearScreenFrameJson(newBg?: string): string {
  return JSON.stringify({
    state: 'WaitInput',
    needValue: false,
    diff: {
      lineOps: [{ type: 'clear_screen' }],
      bgColor: newBg ?? null,
    },
  });
}

/** 构造一个 diff=null 的纯状态切换帧 */
function stateOnlyFrameJson(state: string, inputType?: string, needValue?: boolean): string {
  return JSON.stringify({
    state,
    inputType: inputType ?? null,
    needValue: needValue ?? false,
    diff: null,
  });
}

// ---------- 测试 ----------

describe('useGameStore.applyTurn', () => {
  beforeEach(() => {
    // 每个测试用独立的 Pinia 实例，避免状态泄漏
    setActivePinia(createPinia());
  });

  it('初始状态：displayState 空，lastTurn null，protocolVersion null', () => {
    const game = useGameStore();
    expect(game.displayState.lines).toEqual([]);
    expect(game.displayState.bgColor).toBeNull();
    expect(game.displayState.state).toBe('');
    expect(game.displayState.inputType).toBeNull();
    expect(game.displayState.needValue).toBe(false);
    expect(game.lastTurn).toBeNull();
    expect(game.lastTurnJson).toBeNull();
    expect(game.turnHistory).toEqual([]);
    expect(game.lastError).toBeNull();
    expect(game.protocolVersion).toBeNull();
  });

  it('合法 append 帧：displayState 更新 lines + 顶层字段', () => {
    const game = useGameStore();
    game.applyTurn(appendFrameJson(['Hello', 'World'], {
      state: 'WaitInput',
      inputType: 'AnyKey',
      needValue: false,
      bgColor: '#FF0000',
      protocolVersion: 5,
    }));

    expect(game.lastError).toBeNull();
    expect(game.displayState.lines).toHaveLength(2);
    expect(game.displayState.lines[0].entries[0].segments[0].text).toBe('Hello');
    expect(game.displayState.lines[1].entries[0].segments[0].text).toBe('World');
    expect(game.displayState.bgColor).toBe('#FF0000');
    expect(game.displayState.state).toBe('WaitInput');
    expect(game.displayState.inputType).toBe('AnyKey');
    expect(game.displayState.needValue).toBe(false);
    expect(game.protocolVersion).toBe(5);
    expect(game.lastTurnJson).not.toBeNull();
    expect(game.turnHistory).toHaveLength(1);
  });

  it('多帧累积：第二帧 diff 在第一帧基础上叠加', () => {
    const game = useGameStore();
    game.applyTurn(appendFrameJson(['A', 'B']));
    game.applyTurn(appendFrameJson(['C']));

    expect(game.displayState.lines).toHaveLength(3);
    expect(game.displayState.lines[0].entries[0].segments[0].text).toBe('A');
    expect(game.displayState.lines[1].entries[0].segments[0].text).toBe('B');
    expect(game.displayState.lines[2].entries[0].segments[0].text).toBe('C');
    expect(game.turnHistory).toHaveLength(2);
  });

  it('clear_line_diff 帧：从末尾删除 n 行', () => {
    const game = useGameStore();
    game.applyTurn(appendFrameJson(['A', 'B', 'C']));
    game.applyTurn(clearLineFrameJson(2));

    expect(game.displayState.lines).toHaveLength(1);
    expect(game.displayState.lines[0].entries[0].segments[0].text).toBe('A');
  });

  it('clear_screen + set_bg 帧：清空全部行并更新背景色', () => {
    const game = useGameStore();
    game.applyTurn(appendFrameJson(['data'], { bgColor: '#000000' }));
    expect(game.displayState.lines).toHaveLength(1);
    expect(game.displayState.bgColor).toBe('#000000');

    game.applyTurn(clearScreenFrameJson('#FFFFFF'));
    expect(game.displayState.lines).toEqual([]);
    expect(game.displayState.bgColor).toBe('#FFFFFF');
  });

  it('diff=null 帧：仅更新 state/inputType/needValue，lines 不变', () => {
    const game = useGameStore();
    game.applyTurn(appendFrameJson(['keep'], { state: 'Running', inputType: null, needValue: false }));
    expect(game.displayState.lines).toHaveLength(1);

    game.applyTurn(stateOnlyFrameJson('WaitInput', 'IntValue', true));
    expect(game.displayState.lines).toHaveLength(1); // lines 不变
    expect(game.displayState.lines[0].entries[0].segments[0].text).toBe('keep');
    expect(game.displayState.state).toBe('WaitInput');
    expect(game.displayState.inputType).toBe('IntValue');
    expect(game.displayState.needValue).toBe(true);
  });

  it('非法 JSON：lastError 设置，displayState 保持不变', () => {
    const game = useGameStore();
    game.applyTurn(appendFrameJson(['orig']));
    const beforeLines = game.displayState.lines.length;
    const beforeState = game.displayState.state;

    game.applyTurn('not a json');

    expect(game.lastError).not.toBeNull();
    expect(game.lastError).toContain('协议解析失败');
    expect(game.displayState.lines).toHaveLength(beforeLines); // 状态不变
    expect(game.displayState.state).toBe(beforeState);
    expect(game.turnHistory).toHaveLength(2); // 但原文仍入历史
    expect(game.lastTurnJson).toBe('not a json');
  });

  it('缺必填字段的 JSON：lastError 设置，displayState 不变', () => {
    const game = useGameStore();
    game.applyTurn(appendFrameJson(['orig']));

    // 缺 needValue
    game.applyTurn(JSON.stringify({ state: 'WaitInput' }));

    expect(game.lastError).not.toBeNull();
    expect(game.lastError).toContain('needValue');
    expect(game.displayState.lines).toHaveLength(1);
    expect(game.displayState.lines[0].entries[0].segments[0].text).toBe('orig');
  });

  it('protocolVersion sticky：首帧后后续帧省略时保留版本（C# isInitial 行为对称）', () => {
    const game = useGameStore();
    // 第一帧带 protocolVersion=5（C# AgentJsonlProtocol isInitial=true）
    game.applyTurn(appendFrameJson(['A'], { protocolVersion: 5 }));
    expect(game.protocolVersion).toBe(5);

    // 第二帧不带 protocolVersion（C# isInitial=false 时该字段为 null）
    game.applyTurn(appendFrameJson(['B'], { protocolVersion: null }));
    // sticky 行为：版本号保留，不闪烁归 null
    expect(game.protocolVersion).toBe(5);
  });

  it('protocolVersion sticky：后续帧带新版本时更新（罕见场景，协议升级时）', () => {
    const game = useGameStore();
    game.applyTurn(appendFrameJson(['A'], { protocolVersion: 4 }));
    expect(game.protocolVersion).toBe(4);

    game.applyTurn(appendFrameJson(['B'], { protocolVersion: 5 }));
    expect(game.protocolVersion).toBe(5);
  });

  it('protocolVersion sticky：reset 后清空', () => {
    const game = useGameStore();
    game.applyTurn(appendFrameJson(['A'], { protocolVersion: 5 }));
    expect(game.protocolVersion).toBe(5);

    game.reset();
    expect(game.protocolVersion).toBeNull();
  });

  it('reset()：所有状态复位到空', () => {
    const game = useGameStore();
    game.applyTurn(appendFrameJson(['data'], { state: 'WaitInput', protocolVersion: 5 }));
    expect(game.displayState.lines).toHaveLength(1);

    game.reset();

    expect(game.displayState.lines).toEqual([]);
    expect(game.displayState.bgColor).toBeNull();
    expect(game.displayState.state).toBe('');
    expect(game.displayState.inputType).toBeNull();
    expect(game.displayState.needValue).toBe(false);
    expect(game.lastTurn).toBeNull();
    expect(game.lastTurnJson).toBeNull();
    expect(game.turnHistory).toEqual([]);
    expect(game.lastError).toBeNull();
    expect(game.protocolVersion).toBeNull();
  });

  it('按钮 entry：value 为 number / string 都正确解析并保留', () => {
    const game = useGameStore();
    const json = JSON.stringify({
      state: 'WaitInput',
      needValue: false,
      diff: {
        lineOps: [
          {
            type: 'append',
            newLines: [
              {
                entries: [
                  {
                    segments: [seg('[0]')],
                    button: { value: 0, isInteger: true, col: 0, width: 3 },
                  },
                  {
                    segments: [seg('[Cancel]')],
                    button: { value: 'cancel', isInteger: false, col: 3, width: 8 },
                  },
                ],
                isLineEnd: true,
              },
            ],
          },
        ],
      },
    });
    game.applyTurn(json);

    expect(game.displayState.lines).toHaveLength(1);
    const entries = game.displayState.lines[0].entries;
    expect(entries[0].button!.value).toBe(0);
    expect(entries[0].button!.isInteger).toBe(true);
    expect(entries[1].button!.value).toBe('cancel');
    expect(entries[1].button!.isInteger).toBe(false);
  });

  it('多 entry 行（按钮 + 非按钮 + 按钮）：结构完整保留', () => {
    const game = useGameStore();
    const json = JSON.stringify({
      state: 'WaitInput',
      needValue: false,
      diff: {
        lineOps: [
          {
            type: 'append',
            newLines: [
              {
                entries: [
                  { segments: [seg('[Yes]')], button: { value: 1, isInteger: true, col: 0, width: 5 } },
                  { segments: [seg(' or ')] },
                  { segments: [seg('[No]')], button: { value: 2, isInteger: true, col: 9, width: 4 } },
                ],
                isLineEnd: true,
              },
            ],
          },
        ],
      },
    });
    game.applyTurn(json);

    const entries = game.displayState.lines[0].entries;
    expect(entries).toHaveLength(3);
    expect(entries[0].button).not.toBeNull();
    expect(entries[1].button).toBeNull();
    expect(entries[2].button).not.toBeNull();
    expect(entries[0].button!.col).toBe(0);
    expect(entries[2].button!.col).toBe(9);
  });

  it('segment 颜色正确保留（hex 字符串透传）', () => {
    const game = useGameStore();
    const json = JSON.stringify({
      state: 'WaitInput',
      needValue: false,
      diff: {
        lineOps: [
          {
            type: 'append',
            newLines: [
              {
                entries: [
                  { segments: [{ text: 'red', color: '#FF0000', bold: true }] },
                  { segments: [{ text: ' ', color: null }] },
                  { segments: [{ text: 'blue', color: '#0000FF', italic: true }] },
                ],
                isLineEnd: true,
              },
            ],
          },
        ],
      },
    });
    game.applyTurn(json);

    const entries = game.displayState.lines[0].entries;
    expect(entries[0].segments[0].color).toBe('#FF0000');
    expect(entries[0].segments[0].bold).toBe(true);
    expect(entries[1].segments[0].color).toBeNull();
    expect(entries[2].segments[0].color).toBe('#0000FF');
    expect(entries[2].segments[0].italic).toBe(true);
  });
});
