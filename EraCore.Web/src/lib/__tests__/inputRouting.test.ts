import { describe, it, expect } from 'vitest';
import {
  isAnyKeyInput,
  shouldSubmitButtonValue,
  shouldAdvanceOnTerminalClick,
  type ButtonClickContext,
  type TerminalClickContext,
} from '../inputRouting';

/**
 * inputRouting 单测——"点击→行为"路由判定（spec.md 用户故事 9 / 输入守卫）。
 *
 * 回归锚点：EnterKey/AnyKey（任意键/回车等待）下，点击已存在按钮——
 * - 不应提交按钮 value（shouldSubmitButtonValue=false）→ 游戏不会多回显一行输入；
 * - 且应推进游戏（shouldAdvanceOnTerminalClick=true）→ 点击按钮区域与空白区行为一致，
 *   同时修复过期按钮死区。
 * 与 CLI / WinForms 行为一致：该态下按钮是惰性的。
 */

describe('isAnyKeyInput——任意键/回车等待态判定', () => {
  it('EnterKey / AnyKey → true', () => {
    expect(isAnyKeyInput('EnterKey')).toBe(true);
    expect(isAnyKeyInput('AnyKey')).toBe(true);
  });

  it('IntValue/StrValue/AnyValue/IntButton/StrButton → false', () => {
    expect(isAnyKeyInput('IntValue')).toBe(false);
    expect(isAnyKeyInput('StrValue')).toBe(false);
    expect(isAnyKeyInput('AnyValue')).toBe(false);
    expect(isAnyKeyInput('IntButton')).toBe(false);
    expect(isAnyKeyInput('StrButton')).toBe(false);
  });

  it('null / undefined → false', () => {
    expect(isAnyKeyInput(null)).toBe(false);
    expect(isAnyKeyInput(undefined)).toBe(false);
  });
});

describe('shouldSubmitButtonValue——点击按钮是否提交 value', () => {
  const base: ButtonClickContext = {
    connected: true,
    state: 'WaitInput',
    inputType: 'IntButton',
    buttonGeneration: 1,
    currentTurnGeneration: 1,
    inputInFlight: false,
  };

  it('正常按钮输入态（IntButton）→ 提交', () => {
    expect(shouldSubmitButtonValue(base)).toBe(true);
    expect(shouldSubmitButtonValue({ ...base, inputType: 'StrButton' })).toBe(true);
  });

  it('EnterKey 等待态 + 当前代按钮 → 不提交（回归锚点：避免多回显一行输入）', () => {
    expect(shouldSubmitButtonValue({ ...base, inputType: 'EnterKey' })).toBe(false);
  });

  it('AnyKey 等待态 + 当前代按钮 → 不提交（回归锚点）', () => {
    expect(shouldSubmitButtonValue({ ...base, inputType: 'AnyKey' })).toBe(false);
  });

  it('EnterKey 等待态 + 过期按钮 → 不提交（按钮本就惰性，不再额外区分 generation）', () => {
    expect(shouldSubmitButtonValue({
      ...base,
      inputType: 'EnterKey',
      buttonGeneration: 0,
      currentTurnGeneration: 1,
    })).toBe(false);
  });

  it('过期按钮（IntButton，generation 不匹配）→ 不提交', () => {
    expect(shouldSubmitButtonValue({ ...base, buttonGeneration: 0, currentTurnGeneration: 1 })).toBe(false);
  });

  it('非 WaitInput 态 → 不提交', () => {
    expect(shouldSubmitButtonValue({ ...base, state: 'Running' })).toBe(false);
    expect(shouldSubmitButtonValue({ ...base, state: 'Quit' })).toBe(false);
  });

  it('未连接 → 不提交', () => {
    expect(shouldSubmitButtonValue({ ...base, connected: false })).toBe(false);
  });

  it('inputInFlight（乐观锁）→ 不提交', () => {
    expect(shouldSubmitButtonValue({ ...base, inputInFlight: true })).toBe(false);
  });

  it('inputType 为 null 时不阻断按钮提交（与旧行为一致，协议异常值不额外拦）', () => {
    expect(shouldSubmitButtonValue({ ...base, inputType: null })).toBe(true);
  });

  it('旁观中（canInput=false）→ 不提交', () => {
    expect(shouldSubmitButtonValue({ ...base, canInput: false })).toBe(false);
  });
});

describe('shouldAdvanceOnTerminalClick——点击终端是否推进', () => {
  const base: TerminalClickContext = {
    isButton: false,
    isStickyToBottom: true,
    state: 'WaitInput',
    inputType: 'EnterKey',
    connected: true,
    inputInFlight: false,
  };

  it('空白区 + EnterKey → 推进', () => {
    expect(shouldAdvanceOnTerminalClick(base)).toBe(true);
  });

  it('空白区 + AnyKey → 推进', () => {
    expect(shouldAdvanceOnTerminalClick({ ...base, inputType: 'AnyKey' })).toBe(true);
  });

  it('按钮区 + EnterKey → 推进（回归锚点：任意键态按钮点击同样推进，不成为死区）', () => {
    expect(shouldAdvanceOnTerminalClick({ ...base, isButton: true })).toBe(true);
  });

  it('按钮区 + AnyKey → 推进（回归锚点）', () => {
    expect(shouldAdvanceOnTerminalClick({ ...base, isButton: true, inputType: 'AnyKey' })).toBe(true);
  });

  it('按钮区 + 非任意键态（IntButton/IntValue）→ 不推进（按钮有独立 @click，终端跳过）', () => {
    expect(shouldAdvanceOnTerminalClick({ ...base, isButton: true, inputType: 'IntButton' })).toBe(false);
    expect(shouldAdvanceOnTerminalClick({ ...base, isButton: true, inputType: 'IntValue' })).toBe(false);
  });

  it('空白区 + 非任意键态（IntButton/IntValue/AnyValue）→ 不推进（输入由按钮/输入栏承担）', () => {
    expect(shouldAdvanceOnTerminalClick({ ...base, inputType: 'IntButton' })).toBe(false);
    expect(shouldAdvanceOnTerminalClick({ ...base, inputType: 'IntValue' })).toBe(false);
    expect(shouldAdvanceOnTerminalClick({ ...base, inputType: 'AnyValue' })).toBe(false);
  });

  it('非 WaitInput 态 → 不推进', () => {
    expect(shouldAdvanceOnTerminalClick({ ...base, state: 'Running' })).toBe(false);
    expect(shouldAdvanceOnTerminalClick({ ...base, state: 'Quit' })).toBe(false);
  });

  it('isStickyToBottom=false（翻看历史）→ 不推进，按钮区同样不推进', () => {
    expect(shouldAdvanceOnTerminalClick({ ...base, isStickyToBottom: false })).toBe(false);
    expect(shouldAdvanceOnTerminalClick({ ...base, isButton: true, isStickyToBottom: false })).toBe(false);
  });

  it('未连接 / inputInFlight → 不推进', () => {
    expect(shouldAdvanceOnTerminalClick({ ...base, connected: false })).toBe(false);
    expect(shouldAdvanceOnTerminalClick({ ...base, inputInFlight: true })).toBe(false);
  });

  it('inputType 为 null → 不推进（非任意键态）', () => {
    expect(shouldAdvanceOnTerminalClick({ ...base, inputType: null })).toBe(false);
    expect(shouldAdvanceOnTerminalClick({ ...base, isButton: true, inputType: null })).toBe(false);
  });

  it('旁观中（canInput=false）→ 不推进', () => {
    expect(shouldAdvanceOnTerminalClick({ ...base, canInput: false })).toBe(false);
  });
});

describe('EnterKey/AnyKey 态点击路由契约——两个判定协同', () => {
  it('任意键态点按钮：不提交 value（false）+ 推进（true）——行为统一为"惰性按钮 + 推进"', () => {
    for (const inputType of ['EnterKey', 'AnyKey'] as const) {
      // 按钮 @click：不提交
      expect(shouldSubmitButtonValue({
        connected: true,
        state: 'WaitInput',
        inputType,
        buttonGeneration: 5,
        currentTurnGeneration: 5,
        inputInFlight: false,
      })).toBe(false);
      // 终端 @click（冒泡）：推进
      expect(shouldAdvanceOnTerminalClick({
        isButton: true,
        isStickyToBottom: true,
        state: 'WaitInput',
        inputType,
        connected: true,
        inputInFlight: false,
      })).toBe(true);
    }
  });
});
