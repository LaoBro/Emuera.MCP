import type { TurnRecord, DisplayDiff, LineOp, DisplayLine, DisplayEntry, ButtonRef, PrintSegment } from '../types/protocol';

/**
 * 解析 WS 帧 JSON 字符串为 `TurnRecord`（issue 02）。
 *
 * C# `KestrelGameServer` 把每个 turn 序列化为单个 Text 帧下发——前端 `connection` store
 * 的 `onmessage` 收到原始字符串后经此函数得到结构化对象。
 *
 * 设计选择：
 * - 只做最小形状校验——`state`/`needValue` 必填；其余字段可选（与 C# WhenWritingNull 对齐）
 * - 不强校验 `protocolVersion`——版本协商留给上层 store；解析层只透传
 * - 抛 `ParseTurnRecordError` 而非 `Error`——便于上层 catch 时区分协议错误与其他异常
 * - 未知 `LineOp.type` 抛错——与 C# `TestAdapter.ApplyDiff` 抛 `InvalidOperationException` 对称
 *
 * @param rawJson WS 帧原始 JSON 字符串
 * @returns 结构化 `TurnRecord` 对象
 * @throws {ParseTurnRecordError} JSON 语法错误 / 缺必填字段 / 未知 op 类型
 */
export function parseTurnRecord(rawJson: string): TurnRecord {
  let obj: unknown;
  try {
    obj = JSON.parse(rawJson);
  } catch (e) {
    throw new ParseTurnRecordError(
      `JSON 语法错误: ${e instanceof Error ? e.message : String(e)}`,
    );
  }

  if (typeof obj !== 'object' || obj === null) {
    throw new ParseTurnRecordError(`期望顶层对象，得到 ${typeof obj}`);
  }

  const root = obj as Record<string, unknown>;

  // 必填字段校验
  if (typeof root.state !== 'string') {
    throw new ParseTurnRecordError(`字段 state 缺失或非 string（得到 ${typeof root.state}）`);
  }
  if (typeof root.needValue !== 'boolean') {
    throw new ParseTurnRecordError(`字段 needValue 缺失或非 boolean（得到 ${typeof root.needValue}）`);
  }

  // optional 字段
  const inputType = readStringOrNull(root.inputType, 'inputType');
  const error = readStringOrNull(root.error, 'error');
  const protocolVersion = readIntOrNull(root.protocolVersion, 'protocolVersion');
  const diff = root.diff === undefined || root.diff === null ? null : parseDiff(root.diff);

  return {
    state: root.state,
    needValue: root.needValue,
    inputType,
    diff,
    error,
    protocolVersion,
  };
}

/**
 * 解析 `DisplayDiff`。`lineOps` 必填且必须为数组；`bgColor` optional。
 */
function parseDiff(raw: unknown): DisplayDiff {
  if (typeof raw !== 'object' || raw === null) {
    throw new ParseTurnRecordError(`diff 期望对象，得到 ${typeof raw}`);
  }
  const obj = raw as Record<string, unknown>;
  if (!Array.isArray(obj.lineOps)) {
    throw new ParseTurnRecordError(`diff.lineOps 缺失或非数组（得到 ${typeof obj.lineOps}）`);
  }
  return {
    lineOps: obj.lineOps.map((op, i) => parseLineOp(op, i)),
    bgColor: readStringOrNull(obj.bgColor, 'diff.bgColor'),
  };
}

/**
 * 解析单个 `LineOp`。鉴别字段 `type` 必填；其余字段按 type 校验。
 * 与 C# `LineOpConverter` 反序列化不支持的差别——前端必须能反序列化（消费方）。
 */
function parseLineOp(raw: unknown, index: number): LineOp {
  if (typeof raw !== 'object' || raw === null) {
    throw new ParseTurnRecordError(`lineOps[${index}] 期望对象，得到 ${typeof raw}`);
  }
  const obj = raw as Record<string, unknown>;
  const type = obj.type;
  switch (type) {
    case 'append':
      if (!Array.isArray(obj.newLines)) {
        throw new ParseTurnRecordError(`lineOps[${index}].newLines 缺失或非数组`);
      }
      return { type: 'append', newLines: obj.newLines.map((l, i) => parseDisplayLine(l, `lineOps[${index}].newLines[${i}]`)) };
    case 'clear_line_diff':
      return { type: 'clear_line_diff', clearCount: readIntRequired(obj.clearCount, `lineOps[${index}].clearCount`) };
    case 'clear_screen':
      return { type: 'clear_screen' };
    default:
      throw new ParseTurnRecordError(`lineOps[${index}] 未知 type="${String(type)}"`);
  }
}

/**
 * 解析 `DisplayLine`。`entries` 必填数组；`align` 可空；`isLineEnd` 必填 bool。
 */
function parseDisplayLine(raw: unknown, path: string): DisplayLine {
  if (typeof raw !== 'object' || raw === null) {
    throw new ParseTurnRecordError(`${path} 期望对象，得到 ${typeof raw}`);
  }
  const obj = raw as Record<string, unknown>;
  if (!Array.isArray(obj.entries)) {
    throw new ParseTurnRecordError(`${path}.entries 缺失或非数组`);
  }
  if (typeof obj.isLineEnd !== 'boolean') {
    throw new ParseTurnRecordError(`${path}.isLineEnd 缺失或非 boolean`);
  }
  const align = readAlignOrNull(obj.align, `${path}.align`);
  return {
    entries: obj.entries.map((e, i) => parseDisplayEntry(e, `${path}.entries[${i}]`)),
    align,
    isLineEnd: obj.isLineEnd,
  };
}

/**
 * 解析 `DisplayEntry`。`segments` 必填数组；`button` 可空。
 */
function parseDisplayEntry(raw: unknown, path: string): DisplayEntry {
  if (typeof raw !== 'object' || raw === null) {
    throw new ParseTurnRecordError(`${path} 期望对象，得到 ${typeof raw}`);
  }
  const obj = raw as Record<string, unknown>;
  if (!Array.isArray(obj.segments)) {
    throw new ParseTurnRecordError(`${path}.segments 缺失或非数组`);
  }
  return {
    segments: obj.segments.map((s, i) => parsePrintSegment(s, `${path}.segments[${i}]`)),
    button: obj.button === undefined || obj.button === null ? null : parseButtonRef(obj.button, `${path}.button`),
  };
}

/**
 * 解析 `PrintSegment`。`text` 必填；`color`/`bold`/`italic`/`fontname` 可空。
 */
function parsePrintSegment(raw: unknown, path: string): PrintSegment {
  if (typeof raw !== 'object' || raw === null) {
    throw new ParseTurnRecordError(`${path} 期望对象，得到 ${typeof raw}`);
  }
  const obj = raw as Record<string, unknown>;
  if (typeof obj.text !== 'string') {
    throw new ParseTurnRecordError(`${path}.text 缺失或非 string`);
  }
  return {
    text: obj.text,
    color: readStringOrNull(obj.color, `${path}.color`),
    bold: readBoolOrNull(obj.bold, `${path}.bold`),
    italic: readBoolOrNull(obj.italic, `${path}.italic`),
    fontname: readStringOrNull(obj.fontname, `${path}.fontname`),
  };
}

/**
 * 解析 `ButtonRef`。`value` 必填（number|string）；`isInteger` 必填 bool；
 * `col`/`width` 可空 int。
 */
function parseButtonRef(raw: unknown, path: string): ButtonRef {
  if (typeof raw !== 'object' || raw === null) {
    throw new ParseTurnRecordError(`${path} 期望对象，得到 ${typeof raw}`);
  }
  const obj = raw as Record<string, unknown>;
  if (typeof obj.value !== 'number' && typeof obj.value !== 'string') {
    throw new ParseTurnRecordError(`${path}.value 缺失或非 number/string`);
  }
  if (typeof obj.isInteger !== 'boolean') {
    throw new ParseTurnRecordError(`${path}.isInteger 缺失或非 boolean`);
  }
  return {
    value: obj.value,
    isInteger: obj.isInteger,
    col: readIntOrNull(obj.col, `${path}.col`),
    width: readIntOrNull(obj.width, `${path}.width`),
  };
}

// ---------- 字段读取辅助 ----------

function readStringOrNull(v: unknown, path: string): string | null {
  if (v === undefined || v === null) return null;
  if (typeof v !== 'string') {
    throw new ParseTurnRecordError(`${path} 期望 string|null，得到 ${typeof v}`);
  }
  return v;
}

function readIntOrNull(v: unknown, path: string): number | null {
  if (v === undefined || v === null) return null;
  if (typeof v !== 'number' || !Number.isInteger(v)) {
    throw new ParseTurnRecordError(`${path} 期望 int|null，得到 ${typeof v}`);
  }
  return v;
}

function readIntRequired(v: unknown, path: string): number {
  if (typeof v !== 'number' || !Number.isInteger(v)) {
    throw new ParseTurnRecordError(`${path} 缺失或非 int（得到 ${typeof v}）`);
  }
  return v;
}

function readBoolOrNull(v: unknown, path: string): boolean | null {
  if (v === undefined || v === null) return null;
  if (typeof v !== 'boolean') {
    throw new ParseTurnRecordError(`${path} 期望 boolean|null，得到 ${typeof v}`);
  }
  return v;
}

function readAlignOrNull(v: unknown, path: string): 'left' | 'center' | 'right' | null {
  if (v === undefined || v === null) return null;
  if (v === 'left' || v === 'center' || v === 'right') return v;
  throw new ParseTurnRecordError(`${path} 期望 "left"|"center"|"right"|null，得到 ${JSON.stringify(v)}`);
}

/**
 * 解析错误。上层 catch 时用 `instanceof ParseTurnRecordError` 区分协议错误与其他异常。
 */
export class ParseTurnRecordError extends Error {
  constructor(message: string) {
    super(message);
    this.name = 'ParseTurnRecordError';
  }
}
