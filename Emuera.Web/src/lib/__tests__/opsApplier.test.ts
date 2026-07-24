import { describe, it, expect } from 'vitest';
import { applyOps, applyDiff } from '../opsApplier';
import { applySnapshot } from '../snapshotReducer';
import { EMPTY_DISPLAY_STATE } from '../../types/protocol';
import type { DisplayState, DisplaySnapshot, DisplayLine, DisplayEntry, ButtonRef, PrintSegment, TurnOp, DisplayDiff } from '../../types/protocol';

/**
 * applyOps + applyDiff 单测——与 C# `TestAdapterTests` 的 T_ops_* + T_diff_* 系列
 * （Emuera.Headless.Tests/TestAdapterTests.cs:96+/389+）对称。
 *
 * 测试矩阵（issue 02）：
 * - print：向当前行追加 entry
 * - newline：终止当前行 / 产生空行
 * - clearline：从末尾删除 n 行（含 n 超量场景）
 * - clear：清空全部行 + 重置 bgColor
 * - set_bg：更新 bgColor
 * - 几何透传：ButtonRef 的 col/width 正确传递
 * - 多按钮同行（连续 PrintOp 不换行）
 * - 全量快照 + 增量 ops roundtrip
 * - state/inputType/needValue 在 ops 应用后保持不变
 * - 空 ops 不改变状态
 * - 纯度：applyOps 不修改入参 state 与 ops
 *
 * applyDiff（plan C v5 显式清空信号——对照 C# TestAdapter.ApplyDiff）：
 * - T_diff_append / T_diff_clearline / T_diff_clear / T_diff_clearscreen+append / T_diff_setbg
 */

// ---------- 测试夹具 ----------

function seg(text: string): PrintSegment {
  return { text };
}

function entry(text: string, button?: ButtonRef | null): DisplayEntry {
  return button === undefined ? { segments: [seg(text)] } : { segments: [seg(text)], button };
}

function button(value: number | string, isInteger: boolean, col: number, width: number, generation: number): ButtonRef {
  return { value, isInteger, generation, col, width };
}

function snapshot(partial: Partial<DisplaySnapshot> & Pick<DisplaySnapshot, 'state' | 'needValue'>): DisplaySnapshot {
  return {
    lines: partial.lines ?? [],
    bgColor: partial.bgColor ?? null,
    state: partial.state,
    inputType: partial.inputType ?? null,
    needValue: partial.needValue,
    protocolVersion: partial.protocolVersion ?? 7,
    generation: partial.generation ?? 0,
  };
}

function makeState(partial: Partial<DisplayState> = {}): DisplayState {
  return {
    lines: partial.lines ?? [],
    bgColor: partial.bgColor ?? null,
    state: partial.state ?? '',
    inputType: partial.inputType ?? null,
    needValue: partial.needValue ?? false,
  };
}

// ---------- print + newline ----------

describe('applyOps: print + newline', () => {
  it('T_ops_print 对称：PrintOp 追加 entry，NewLineOp 终止行', () => {
    const ops: TurnOp[] = [
      { type: 'print', segments: [seg('Hello')], button: button(42, true, 0, 5, 0) },
      { type: 'newline', align: null },
    ];
    const newState = applyOps(EMPTY_DISPLAY_STATE, ops);

    expect(newState.lines).toHaveLength(1);
    const l = newState.lines[0];
    expect(l.isLineEnd).toBe(true);
    expect(l.entries).toHaveLength(1);

    const e = l.entries[0];
    expect(e.segments[0].text).toBe('Hello');
    expect(e.button).not.toBeNull();
    expect(e.button!.col).toBe(0);
    expect(e.button!.width).toBe(5);
    expect(e.button!.value).toBe(42);
    expect(e.button!.isInteger).toBe(true);
  });

  it('连续 PrintOp 不带 newline 留在同一行', () => {
    const ops: TurnOp[] = [
      { type: 'print', segments: [seg('Hello')] },
      { type: 'print', segments: [seg(' ')] },
      { type: 'print', segments: [seg('World')] },
      { type: 'newline', align: null },
    ];
    const newState = applyOps(EMPTY_DISPLAY_STATE, ops);

    expect(newState.lines).toHaveLength(1);
    expect(newState.lines[0].entries).toHaveLength(3);
    expect(newState.lines[0].isLineEnd).toBe(true);
    expect(newState.lines[0].entries.map((e) => e.segments[0].text).join('')).toBe('Hello World');
  });

  it('NewLineOp 在空状态产生空行', () => {
    const ops: TurnOp[] = [{ type: 'newline', align: 'left' }];
    const newState = applyOps(EMPTY_DISPLAY_STATE, ops);

    expect(newState.lines).toHaveLength(1);
    expect(newState.lines[0].entries).toEqual([]);
    expect(newState.lines[0].isLineEnd).toBe(true);
    expect(newState.lines[0].align).toBe('left');
  });

  it('连续 NewLineOp 产生多个空行', () => {
    const ops: TurnOp[] = [
      { type: 'newline', align: null },
      { type: 'newline', align: null },
      { type: 'newline', align: null },
    ];
    const newState = applyOps(EMPTY_DISPLAY_STATE, ops);

    expect(newState.lines).toHaveLength(3);
    for (const l of newState.lines) {
      expect(l.entries).toEqual([]);
      expect(l.isLineEnd).toBe(true);
    }
  });

  it('NewLineOp 覆盖当前行 align（即使 align=null 也覆盖）', () => {
    // 先建一行 align="left"
    const ops1: TurnOp[] = [
      { type: 'print', segments: [seg('x')] },
      { type: 'newline', align: 'left' },
    ];
    const state1 = applyOps(EMPTY_DISPLAY_STATE, ops1);
    expect(state1.lines[0].align).toBe('left');

    // 再来一行 align=null 的 newline，覆盖现有 align
    const ops2: TurnOp[] = [{ type: 'print', segments: [seg('y')] }, { type: 'newline', align: null }];
    const state2 = applyOps(state1, ops2);
    // 第一行 align 保留 "left"；第二行 align 被 null 覆盖
    expect(state2.lines[0].align).toBe('left');
    expect(state2.lines[1].align).toBeNull();
  });
});

// ---------- clearline ----------

describe('applyOps: clearline', () => {
  it('T_ops_clearline 对称：删除末尾 n 行', () => {
    const setup: TurnOp[] = [
      { type: 'print', segments: [seg('A')] },
      { type: 'newline', align: null },
      { type: 'print', segments: [seg('B')] },
      { type: 'newline', align: null },
      { type: 'print', segments: [seg('C')] },
      { type: 'newline', align: null },
    ];
    const state = applyOps(EMPTY_DISPLAY_STATE, setup);
    expect(state.lines).toHaveLength(3);

    const cleared = applyOps(state, [{ type: 'clearline', n: 2 }]);
    expect(cleared.lines).toHaveLength(1);
    expect(cleared.lines[0].entries[0].segments[0].text).toBe('A');
  });

  it('T_ops_clearline_exceeding 对称：n 超过总行数时全部清空', () => {
    const setup: TurnOp[] = [
      { type: 'print', segments: [seg('only')] },
      { type: 'newline', align: null },
    ];
    const state = applyOps(EMPTY_DISPLAY_STATE, setup);

    const cleared = applyOps(state, [{ type: 'clearline', n: 5 }]);
    expect(cleared.lines).toEqual([]);
  });

  it('clearline n=0 不改变行', () => {
    const setup: TurnOp[] = [
      { type: 'print', segments: [seg('A')] },
      { type: 'newline', align: null },
    ];
    const state = applyOps(EMPTY_DISPLAY_STATE, setup);

    const cleared = applyOps(state, [{ type: 'clearline', n: 0 }]);
    expect(cleared.lines).toHaveLength(1);
  });
});

// ---------- clear ----------

describe('applyOps: clear', () => {
  it('T_ops_clear 对称：清空全部行 + 重置 bgColor', () => {
    // 先用 snapshot 设置初始状态（含 bgColor）
    const snap = snapshot({
      state: 'WaitInput',
      needValue: false,
      bgColor: '#FF0000',
      lines: [{ entries: [entry('data')], isLineEnd: true }],
    });
    const state = applySnapshot(EMPTY_DISPLAY_STATE, snap);
    expect(state.lines).toHaveLength(1);
    expect(state.bgColor).toBe('#FF0000');

    const cleared = applyOps(state, [{ type: 'clear' }]);
    expect(cleared.lines).toEqual([]);
    expect(cleared.bgColor).toBeNull();
  });
});

// ---------- set_bg ----------

describe('applyOps: set_bg', () => {
  it('T_ops_setbg 对称：更新 bgColor', () => {
    const state = applyOps(EMPTY_DISPLAY_STATE, [{ type: 'set_bg', color: '#FF0000' }]);
    expect(state.bgColor).toBe('#FF0000');

    const state2 = applyOps(state, [{ type: 'set_bg', color: '#00FF00' }]);
    expect(state2.bgColor).toBe('#00FF00');
  });
});

// ---------- 几何透传 ----------

describe('applyOps: 几何透传', () => {
  it('T_ops_geometry 对称：ButtonRef col=5, width=4 不重算直接透传', () => {
    const ops: TurnOp[] = [
      {
        type: 'print',
        segments: [seg('btn')],
        button: button('click', false, 5, 4, 0),
      },
      { type: 'newline', align: null },
    ];
    const newState = applyOps(EMPTY_DISPLAY_STATE, ops);

    const e = newState.lines[0].entries[0];
    expect(e.button).not.toBeNull();
    expect(e.button!.col).toBe(5);
    expect(e.button!.width).toBe(4);
    expect(e.button!.value).toBe('click');
    expect(e.button!.isInteger).toBe(false);
  });
});

// ---------- roundtrip ----------

describe('applyOps: roundtrip', () => {
  it('T_full_roundtrip 对称：snapshot 初始化 + ops 增量（clear + set_bg + print + newline）', () => {
    // 初始快照：1 行 [Start] 按钮
    const snap = snapshot({
      state: 'WaitInput',
      needValue: false,
      bgColor: '#000000',
      lines: [lineFromButton('[Start]', 1)],
    });
    const state = applySnapshot(EMPTY_DISPLAY_STATE, snap);
    expect(state.lines).toHaveLength(1);
    expect(state.lines[0].entries[0].segments[0].text).toBe('[Start]');

    // 增量 ops：清屏 + 新按钮 + 换背景
    const ops: TurnOp[] = [
      { type: 'clear' },
      { type: 'set_bg', color: '#0000FF' },
      {
        type: 'print',
        segments: [seg('[Next]')],
        button: button(2, true, 0, 6, 0),
      },
      { type: 'newline', align: null },
    ];

    const final = applyOps(state, ops);

    expect(final.lines).toHaveLength(1);
    expect(final.lines[0].entries[0].segments[0].text).toBe('[Next]');
    expect(final.lines[0].entries[0].button!.col).toBe(0);
    expect(final.lines[0].entries[0].button!.width).toBe(6);
    expect(final.bgColor).toBe('#0000FF');
  });

  it('T_full_roundtrip_preserves_state 对称：ops 不改变 state/inputType/needValue', () => {
    const snap = snapshot({
      state: 'WaitInput',
      inputType: 'IntValue',
      needValue: true,
    });
    const state = applySnapshot(EMPTY_DISPLAY_STATE, snap);

    expect(state.state).toBe('WaitInput');
    expect(state.inputType).toBe('IntValue');
    expect(state.needValue).toBe(true);

    const ops: TurnOp[] = [
      { type: 'print', segments: [seg('x')] },
      { type: 'newline', align: null },
    ];
    const final = applyOps(state, ops);

    expect(final.state).toBe('WaitInput');
    expect(final.inputType).toBe('IntValue');
    expect(final.needValue).toBe(true);
  });

  it('空 ops 不改变状态', () => {
    const snap = snapshot({
      state: 'WaitInput',
      needValue: false,
      bgColor: '#FF0000',
      lines: [lineFromButton('[OK]', 1)],
    });
    const state = applySnapshot(EMPTY_DISPLAY_STATE, snap);

    const final = applyOps(state, []);
    expect(final.lines).toHaveLength(1);
    expect(final.bgColor).toBe('#FF0000');
    expect(final.state).toBe('WaitInput');
  });
});

// ---------- 纯度验证 ----------

describe('applyOps: 纯度', () => {
  it('applyOps 不修改入参 state', () => {
    const ops: TurnOp[] = [
      { type: 'print', segments: [seg('A')] },
      { type: 'newline', align: null },
      { type: 'set_bg', color: '#FF0000' },
    ];
    const state = makeState({ bgColor: '#000000', lines: [{ entries: [entry('orig')], isLineEnd: true }] });
    const originalLinesLen = state.lines.length;
    const originalBg = state.bgColor;
    const originalFirstText = state.lines[0].entries[0].segments[0].text;

    applyOps(state, ops);

    expect(state.lines).toHaveLength(originalLinesLen);
    expect(state.bgColor).toBe(originalBg);
    expect(state.lines[0].entries[0].segments[0].text).toBe(originalFirstText);
  });

  it('applyOps 不与入参 ops 共享 segment 引用（深拷贝隔离）', () => {
    // C# TestAdapter 的 segments.ToList() 浅拷贝 List 但元素共享——C# PrintSegment 是 record
    // （init-only 不可变），共享安全。TS 对象默认可变，故 applyOps 深拷贝每个 segment。
    const op: TurnOp = {
      type: 'print',
      segments: [seg('orig')],
    };
    const state = applyOps(EMPTY_DISPLAY_STATE, [op, { type: 'newline', align: null }]);

    // 修改原 op.segments[0].text——不应影响已应用的状态
    op.segments[0].text = 'mutated';

    expect(state.lines[0].entries[0].segments[0].text).toBe('orig');
  });

  it('未知 op 类型抛 Error（与 C# InvalidOperationException 对称）', () => {
    const bogus = { type: 'unknown_op' } as unknown as TurnOp;
    expect(() => applyOps(EMPTY_DISPLAY_STATE, [bogus])).toThrow(/Unknown op type/);
  });
});

// ---------- applyDiff：消费 DisplayDiff（v5 协议增量） ----------

/**
 * 与 C# TestAdapterTests.T_diff_* 系列（Emuera.Headless.Tests/TestAdapterTests.cs:389+）对称。
 * WS 帧 TurnRecord.diff.lineOps 即 LineOp[]——前端实际消费 WS 流靠 applyDiff。
 */
describe('applyDiff', () => {
  // 辅助：构造只含一段 text 的 DisplayLine（用于 append 测试）
  function diffLine(text: string): DisplayLine {
    return { entries: [entry(text)], align: 'left', isLineEnd: true };
  }

  function diff(partial: Partial<DisplayDiff>): DisplayDiff {
    return {
      lineOps: partial.lineOps ?? [],
      bgColor: partial.bgColor ?? null,
    };
  }

  it('T_diff_append 对称：追加新行到末尾', () => {
    const d = diff({
      lineOps: [{ type: 'append', newLines: [diffLine('a'), diffLine('b')] }],
    });
    const state = applyDiff(EMPTY_DISPLAY_STATE, d);

    expect(state.lines).toHaveLength(2);
    expect(state.lines[0].entries[0].segments[0].text).toBe('a');
    expect(state.lines[1].entries[0].segments[0].text).toBe('b');
  });

  it('T_diff_clearline 对称：从末尾删除 n 行', () => {
    // 先 append 3 行
    const setup = diff({
      lineOps: [{ type: 'append', newLines: [diffLine('a'), diffLine('b'), diffLine('c')] }],
    });
    const state = applyDiff(EMPTY_DISPLAY_STATE, setup);
    expect(state.lines).toHaveLength(3);

    // 再 clear_line_diff 删除 2 行
    const cleared = applyDiff(state, diff({ lineOps: [{ type: 'clear_line_diff', clearCount: 2 }] }));
    expect(cleared.lines).toHaveLength(1);
    expect(cleared.lines[0].entries[0].segments[0].text).toBe('a');
  });

  it('T_diff_clear 对称：清空全部行 + 更新 bgColor', () => {
    const setup = diff({
      lineOps: [{ type: 'append', newLines: [diffLine('data')] }],
    });
    const state = applyDiff(EMPTY_DISPLAY_STATE, setup);

    const cleared = applyDiff(state, diff({
      lineOps: [{ type: 'clear_screen' }],
      bgColor: '#FF0000',
    }));
    expect(cleared.lines).toEqual([]);
    expect(cleared.bgColor).toBe('#FF0000');
  });

  it('T_diff_clearscreen_then_append 对称：全清后重印重建状态', () => {
    const setup = diff({
      lineOps: [{ type: 'append', newLines: [diffLine('old')] }],
    });
    const state = applyDiff(EMPTY_DISPLAY_STATE, setup);

    // CLEAR + 重印新行：等价于引擎 CLEAR 后打印
    const rebuilt = applyDiff(state, diff({
      lineOps: [
        { type: 'clear_screen' },
        { type: 'append', newLines: [diffLine('new')] },
      ],
    }));
    expect(rebuilt.lines).toHaveLength(1);
    expect(rebuilt.lines[0].entries[0].segments[0].text).toBe('new');
  });

  it('T_diff_setbg 对称：仅更新 bgColor 不动 lines', () => {
    const setup = diff({
      lineOps: [{ type: 'append', newLines: [diffLine('keep')] }],
      bgColor: '#00FF00',
    });
    const state = applyDiff(EMPTY_DISPLAY_STATE, setup);

    const updated = applyDiff(state, diff({ lineOps: [], bgColor: '#0000FF' }));
    expect(updated.lines).toHaveLength(1);
    expect(updated.lines[0].entries[0].segments[0].text).toBe('keep');
    expect(updated.bgColor).toBe('#0000FF');
  });

  it('diff.bgColor=null 时保留原 bgColor', () => {
    const state = makeState({ bgColor: '#FF0000', lines: [{ entries: [entry('keep')], isLineEnd: true }] });
    const d = diff({ lineOps: [{ type: 'append', newLines: [diffLine('extra')] }] });
    const updated = applyDiff(state, d);
    expect(updated.bgColor).toBe('#FF0000');
    expect(updated.lines).toHaveLength(2);
  });

  it('未知 LineOp 类型抛 Error', () => {
    const bogus = {
      lineOps: [{ type: 'unknown_diff_op' }],
      bgColor: null,
    } as unknown as DisplayDiff;
    expect(() => applyDiff(EMPTY_DISPLAY_STATE, bogus)).toThrow(/Unknown LineOp type/);
  });

  it('applyDiff 不与入参共享 segments 引用（深拷贝隔离）', () => {
    const newLine: DisplayLine = { entries: [entry('orig')], isLineEnd: true };
    const d = diff({ lineOps: [{ type: 'append', newLines: [newLine] }] });
    const state = applyDiff(EMPTY_DISPLAY_STATE, d);

    // 修改原 newLine.entries[0].segments[0].text——不应影响已应用的状态
    newLine.entries[0].segments[0].text = 'mutated';

    expect(state.lines[0].entries[0].segments[0].text).toBe('orig');
  });
});

// ---------- applyDiff: shift_head（MaxLog 头部截断） ----------
//
// 与 C# TestAdapterTests.T_diff_shift_head_* 系列（Emuera.Headless.Tests/TestAdapterTests.cs）
// 对称——验证前端消费方与 C# TestAdapter 行为一致。
describe('applyDiff: shift_head', () => {
  function diffLine(text: string): DisplayLine {
    return { entries: [entry(text)], align: 'left', isLineEnd: true };
  }

  function diff(partial: Partial<DisplayDiff>): DisplayDiff {
    return {
      lineOps: partial.lineOps ?? [],
      bgColor: partial.bgColor ?? null,
    };
  }

  it('T_diff_shift_head 对称：从头部删除指定行数', () => {
    // 先 append 3 行 [a, b, c]
    const setup = diff({
      lineOps: [{ type: 'append', newLines: [diffLine('a'), diffLine('b'), diffLine('c')] }],
    });
    const state = applyDiff(EMPTY_DISPLAY_STATE, setup);
    expect(state.lines).toHaveLength(3);

    // shift_head 删除头部 1 行——剩 [b, c]
    const shifted = applyDiff(state, diff({ lineOps: [{ type: 'shift_head', count: 1 }] }));
    expect(shifted.lines).toHaveLength(2);
    expect(shifted.lines[0].entries[0].segments[0].text).toBe('b');
    expect(shifted.lines[1].entries[0].segments[0].text).toBe('c');
  });

  it('T_diff_shift_head_count_exceeding 对称：count 超过总行数时全部清空', () => {
    const setup = diff({
      lineOps: [{ type: 'append', newLines: [diffLine('only')] }],
    });
    const state = applyDiff(EMPTY_DISPLAY_STATE, setup);
    expect(state.lines).toHaveLength(1);

    const shifted = applyDiff(state, diff({ lineOps: [{ type: 'shift_head', count: 5 }] }));
    expect(shifted.lines).toEqual([]);
  });

  it('T_diff_shift_head_zero 对称：count=0 不改变行', () => {
    const setup = diff({
      lineOps: [{ type: 'append', newLines: [diffLine('keep')] }],
    });
    const state = applyDiff(EMPTY_DISPLAY_STATE, setup);

    const shifted = applyDiff(state, diff({ lineOps: [{ type: 'shift_head', count: 0 }] }));
    expect(shifted.lines).toHaveLength(1);
    expect(shifted.lines[0].entries[0].segments[0].text).toBe('keep');
  });

  it('T_diff_shift_head_then_append 对称：头部截断 + 尾部追加（MaxLog 滚动场景）', () => {
    // 长 game 达 MaxLog 后每新增一行 = ShiftHeadLineOp(1) + AppendLinesOp(1)
    const setup = diff({
      lineOps: [{ type: 'append', newLines: [diffLine('a'), diffLine('b')] }],
    });
    const state = applyDiff(EMPTY_DISPLAY_STATE, setup);

    const shifted = applyDiff(state, diff({
      lineOps: [
        { type: 'shift_head', count: 1 },
        { type: 'append', newLines: [diffLine('c')] },
      ],
    }));
    // 头部删 a + 尾部加 c → [b, c]
    expect(shifted.lines).toHaveLength(2);
    expect(shifted.lines[0].entries[0].segments[0].text).toBe('b');
    expect(shifted.lines[1].entries[0].segments[0].text).toBe('c');
  });

  it('T_diff_shift_head_with_clear_line_diff 对称：头部截断 + 尾部清行', () => {
    // 头部截断 + 尾部 CLEARLINE 可同回合发生（spec.md shift_head 与 ClearLineDiffOp 共存）
    const setup = diff({
      lineOps: [{ type: 'append', newLines: [diffLine('a'), diffLine('b'), diffLine('c'), diffLine('d')] }],
    });
    const state = applyDiff(EMPTY_DISPLAY_STATE, setup);

    const shifted = applyDiff(state, diff({
      lineOps: [
        { type: 'shift_head', count: 1 },          // 删 a → [b, c, d]
        { type: 'clear_line_diff', clearCount: 1 }, // 删 d → [b, c]
      ],
    }));
    expect(shifted.lines).toHaveLength(2);
    expect(shifted.lines[0].entries[0].segments[0].text).toBe('b');
    expect(shifted.lines[1].entries[0].segments[0].text).toBe('c');
  });

  it('shift_head 不与入参共享引用（纯度）', () => {
    const setup = diff({
      lineOps: [{ type: 'append', newLines: [diffLine('a'), diffLine('b')] }],
    });
    const state = applyDiff(EMPTY_DISPLAY_STATE, setup);
    const originalLinesLen = state.lines.length;

    applyDiff(state, diff({ lineOps: [{ type: 'shift_head', count: 1 }] }));

    // applyDiff 不应修改入参 state——state.lines 仍为 2 行
    expect(state.lines).toHaveLength(originalLinesLen);
  });
});

// ---------- 辅助 ----------

function lineFromButton(text: string, value: number, col = 0, width?: number, generation = 0): DisplayLine {
  const w = width ?? text.length;
  return {
    entries: [entry(text, button(value, true, col, w, generation))],
    isLineEnd: true,
  };
}
