import { describe, it, expect } from 'vitest';
import { parseTurnRecord, ParseTurnRecordError } from '../parseTurnRecord';

/**
 * parseTurnRecord 单测——WS 帧原始 JSON 字符串解析为 TurnRecord。
 *
 * C# 端 `KestrelGameServer` 把 turn 序列化为 JSON 文本帧，前端必须能正确反序列化。
 * 此处验证：
 * - 合法 JSON → 结构化 TurnRecord（含 diff / lineOps 嵌套）
 * - 缺必填字段 → 抛 ParseTurnRecordError
 * - 未知 op type → 抛 ParseTurnRecordError（与 C# TestAdapter.ApplyDiff 抛 InvalidOperationException 对称）
 * - 非 JSON 字符串 → 抛 ParseTurnRecordError
 */
describe('parseTurnRecord', () => {
  it('解析最简 TurnRecord（仅必填字段 state + needValue）', () => {
    const json = JSON.stringify({ state: 'WaitInput', needValue: false });
    const turn = parseTurnRecord(json);
    expect(turn.state).toBe('WaitInput');
    expect(turn.needValue).toBe(false);
    expect(turn.inputType).toBeNull();
    expect(turn.diff).toBeNull();
    expect(turn.error).toBeNull();
    expect(turn.protocolVersion).toBeNull();
  });

  it('解析完整 TurnRecord（含 inputType / diff / protocolVersion）', () => {
    const json = JSON.stringify({
      state: 'WaitInput',
      inputType: 'IntValue',
      needValue: true,
      protocolVersion: 5,
      diff: {
        lineOps: [
          { type: 'append', newLines: [{ entries: [{ segments: [{ text: 'Hello' }] }], isLineEnd: true }] },
          { type: 'clear_line_diff', clearCount: 2 },
          { type: 'clear_screen' },
        ],
        bgColor: '#FF0000',
      },
    });
    const turn = parseTurnRecord(json);
    expect(turn.state).toBe('WaitInput');
    expect(turn.inputType).toBe('IntValue');
    expect(turn.needValue).toBe(true);
    expect(turn.protocolVersion).toBe(5);
    expect(turn.diff).not.toBeNull();
    expect(turn.diff!.bgColor).toBe('#FF0000');
    expect(turn.diff!.lineOps).toHaveLength(3);

    // lineOps[0]: append + 1 行 + 1 entry + 1 segment
    const op0 = turn.diff!.lineOps[0];
    expect(op0.type).toBe('append');
    if (op0.type !== 'append') throw new Error('unreachable');
    expect(op0.newLines).toHaveLength(1);
    expect(op0.newLines[0].entries).toHaveLength(1);
    expect(op0.newLines[0].entries[0].segments).toHaveLength(1);
    expect(op0.newLines[0].entries[0].segments[0].text).toBe('Hello');
    expect(op0.newLines[0].isLineEnd).toBe(true);

    // lineOps[1]: clear_line_diff + clearCount=2
    const op1 = turn.diff!.lineOps[1];
    expect(op1.type).toBe('clear_line_diff');
    if (op1.type !== 'clear_line_diff') throw new Error('unreachable');
    expect(op1.clearCount).toBe(2);

    // lineOps[2]: clear_screen（无其他字段）
    expect(turn.diff!.lineOps[2].type).toBe('clear_screen');
  });

  it('解析 error 帧（state=Error 时携带 error 字段）', () => {
    const json = JSON.stringify({
      state: 'Error',
      needValue: false,
      error: 'EraBrowser load failed',
    });
    const turn = parseTurnRecord(json);
    expect(turn.state).toBe('Error');
    expect(turn.error).toBe('EraBrowser load failed');
  });

  it('解析带 ButtonRef 的 entry（value 为 number/string 两种）', () => {
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
                    segments: [{ text: '[OK]' }],
                    button: { value: 1, isInteger: true, col: 0, width: 4 },
                  },
                  {
                    segments: [{ text: '[Cancel]' }],
                    button: { value: 'cancel', isInteger: false, col: 4, width: 8 },
                  },
                ],
                isLineEnd: true,
              },
            ],
          },
        ],
      },
    });
    const turn = parseTurnRecord(json);
    const op0 = turn.diff!.lineOps[0];
    expect(op0.type).toBe('append');
    if (op0.type !== 'append') throw new Error('unreachable');
    const entry0 = op0.newLines[0].entries[0];
    expect(entry0.button!.value).toBe(1);
    expect(entry0.button!.isInteger).toBe(true);
    expect(entry0.button!.col).toBe(0);
    expect(entry0.button!.width).toBe(4);

    const entry1 = op0.newLines[0].entries[1];
    expect(entry1.button!.value).toBe('cancel');
    expect(entry1.button!.isInteger).toBe(false);
    expect(entry1.button!.col).toBe(4);
    expect(entry1.button!.width).toBe(8);
  });

  it('解析 align 字段（left/center/right/null）', () => {
    const json = JSON.stringify({
      state: 'WaitInput',
      needValue: false,
      diff: {
        lineOps: [
          {
            type: 'append',
            newLines: [
              { entries: [{ segments: [{ text: 'left' }] }], align: 'left', isLineEnd: true },
              { entries: [{ segments: [{ text: 'center' }] }], align: 'center', isLineEnd: true },
              { entries: [{ segments: [{ text: 'right' }] }], align: 'right', isLineEnd: true },
              { entries: [{ segments: [{ text: 'default' }] }], isLineEnd: true },
            ],
          },
        ],
      },
    });
    const turn = parseTurnRecord(json);
    const op0 = turn.diff!.lineOps[0];
    expect(op0.type).toBe('append');
    if (op0.type !== 'append') throw new Error('unreachable');
    const lines = op0.newLines;
    expect(lines[0].align).toBe('left');
    expect(lines[1].align).toBe('center');
    expect(lines[2].align).toBe('right');
    expect(lines[3].align).toBeNull();
  });

  it('非 JSON 字符串抛 ParseTurnRecordError', () => {
    expect(() => parseTurnRecord('not a json')).toThrow(ParseTurnRecordError);
    expect(() => parseTurnRecord('{')).toThrow(ParseTurnRecordError);
  });

  it('顶层非对象抛 ParseTurnRecordError', () => {
    expect(() => parseTurnRecord('123')).toThrow(ParseTurnRecordError);
    expect(() => parseTurnRecord('"string"')).toThrow(ParseTurnRecordError);
    expect(() => parseTurnRecord('null')).toThrow(ParseTurnRecordError);
    expect(() => parseTurnRecord('[]')).toThrow(ParseTurnRecordError);
  });

  it('缺 state 字段抛 ParseTurnRecordError', () => {
    expect(() => parseTurnRecord(JSON.stringify({ needValue: false }))).toThrow(ParseTurnRecordError);
  });

  it('缺 needValue 字段抛 ParseTurnRecordError', () => {
    expect(() => parseTurnRecord(JSON.stringify({ state: 'WaitInput' }))).toThrow(ParseTurnRecordError);
  });

  it('未知 LineOp type 抛 ParseTurnRecordError', () => {
    const json = JSON.stringify({
      state: 'WaitInput',
      needValue: false,
      diff: {
        lineOps: [{ type: 'unknown_op', foo: 'bar' }],
      },
    });
    expect(() => parseTurnRecord(json)).toThrow(ParseTurnRecordError);
  });

  it('lineOps 非数组抛 ParseTurnRecordError', () => {
    const json = JSON.stringify({
      state: 'WaitInput',
      needValue: false,
      diff: { lineOps: 'not_an_array' },
    });
    expect(() => parseTurnRecord(json)).toThrow(ParseTurnRecordError);
  });

  it('ButtonRef.value 既非 number 也非 string 抛 ParseTurnRecordError', () => {
    const json = JSON.stringify({
      state: 'WaitInput',
      needValue: false,
      diff: {
        lineOps: [
          {
            type: 'append',
            newLines: [
              {
                entries: [{ segments: [{ text: 'x' }], button: { value: true, isInteger: false } }],
                isLineEnd: true,
              },
            ],
          },
        ],
      },
    });
    expect(() => parseTurnRecord(json)).toThrow(ParseTurnRecordError);
  });
});
