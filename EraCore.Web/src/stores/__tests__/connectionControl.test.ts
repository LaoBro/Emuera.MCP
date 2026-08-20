import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { setActivePinia, createPinia } from 'pinia';
import { useConnectionStore } from '../connection';
import { useGameStore } from '../game';
import type { ControlEvent } from '../../types/protocol';

/**
 * Seam 3：HTTP 模式控制权交接（issue 04 / M3）。
 *
 * 测 connection store 的公开控制面：当前 Controller、旁观/可输入派生、
 * 事件驱动切换、用户 acquire（无 token）、输入 409 回落到正确控制状态。
 */

function agentController() {
  return { kind: 'agent' as const, leaseExpiresAt: '2026-08-14T12:00:00Z' };
}

function userController() {
  return { kind: 'user' as const, leaseExpiresAt: null };
}

function event(type: ControlEvent['type'], controller: ControlEvent['controller'], state = 'held'): ControlEvent {
  return { type, controller, state, at: '2026-08-14T12:00:00Z' };
}

describe('useConnectionStore 控制状态派生', () => {
  beforeEach(() => setActivePinia(createPinia()));

  it('默认空闲：用户可输入，不是旁观者', () => {
    const conn = useConnectionStore();
    expect(conn.controller).toBeNull();
    expect(conn.controlState).toBe('idle');
    expect(conn.isSpectator).toBe(false);
    expect(conn.canInput).toBe(true);
    expect(conn.canMutateLifecycle).toBe(true);
  });

  it('agent 持有：旁观中，输入和换游戏/停止禁用', () => {
    const conn = useConnectionStore();
    conn.applyControlStatus({ controller: agentController(), state: 'held' });
    expect(conn.controller?.kind).toBe('agent');
    expect(conn.isSpectator).toBe(true);
    expect(conn.canInput).toBe(false);
    expect(conn.canMutateLifecycle).toBe(false);
  });

  it('user 持有：可输入，不是旁观者', () => {
    const conn = useConnectionStore();
    conn.applyControlStatus({ controller: userController(), state: 'held' });
    expect(conn.isSpectator).toBe(false);
    expect(conn.canInput).toBe(true);
    expect(conn.canMutateLifecycle).toBe(true);
  });
});

describe('useConnectionStore 控制事件驱动 UI 状态', () => {
  beforeEach(() => setActivePinia(createPinia()));

  it('acquired（agent）→ 旁观', () => {
    const conn = useConnectionStore();
    conn.applyControlEvent(event('acquired', agentController()));
    expect(conn.lastControlEvent?.type).toBe('acquired');
    expect(conn.isSpectator).toBe(true);
    expect(conn.canInput).toBe(false);
  });

  it('stolen → 用户可输入', () => {
    const conn = useConnectionStore();
    conn.applyControlStatus({ controller: agentController(), state: 'held' });
    conn.applyControlEvent(event('stolen', userController()));
    expect(conn.controller?.kind).toBe('user');
    expect(conn.isSpectator).toBe(false);
    expect(conn.canInput).toBe(true);
  });

  it('released / lease_expired / game_ended → 空闲可输入', () => {
    const conn = useConnectionStore();
    for (const type of ['released', 'lease_expired', 'game_ended'] as const) {
      conn.applyControlStatus({ controller: agentController(), state: 'held' });
      conn.applyControlEvent(event(type, null, 'idle'));
      expect(conn.controller).toBeNull();
      expect(conn.controlState).toBe('idle');
      expect(conn.isSpectator).toBe(false);
      expect(conn.canInput).toBe(true);
    }
  });

  it('多事件序列 acquired → stolen → released 收敛到同一空闲态', () => {
    const conn = useConnectionStore();
    conn.applyControlEvent(event('acquired', agentController()));
    expect(conn.isSpectator).toBe(true);
    conn.applyControlEvent(event('stolen', userController()));
    expect(conn.controller?.kind).toBe('user');
    expect(conn.canInput).toBe(true);
    conn.applyControlEvent(event('released', null, 'idle'));
    expect(conn.controller).toBeNull();
    expect(conn.controlState).toBe('idle');
    expect(conn.canInput).toBe(true);
  });
});

describe('useConnectionStore.acquireControl', () => {
  let fetchSpy: ReturnType<typeof vi.spyOn>;

  beforeEach(() => {
    setActivePinia(createPinia());
    fetchSpy = vi.spyOn(globalThis, 'fetch');
  });

  afterEach(() => {
    fetchSpy.mockRestore();
  });

  it('POST /control/acquire 不带 token，成功后立即解锁输入', async () => {
    fetchSpy.mockResolvedValueOnce(
      new Response(
        JSON.stringify({
          controller: userController(),
          state: 'held',
          turn: { state: 'WaitInput', needValue: true },
          turnsAdvanced: 0,
        }),
        { status: 200 },
      ),
    );
    const conn = useConnectionStore();
    conn.applyControlStatus({ controller: agentController(), state: 'held' });
    expect(conn.canInput).toBe(false);

    await conn.acquireControl();

    expect(fetchSpy).toHaveBeenCalledWith(
      'http://localhost:5173/control/acquire',
      expect.objectContaining({ method: 'POST' }),
    );
    const init = fetchSpy.mock.calls[0][1] as RequestInit;
    expect(init.body).toBeUndefined();
    expect(conn.controller?.kind).toBe('user');
    expect(conn.canInput).toBe(true);
    expect(conn.controlError).toBeNull();
  });
});

describe('useConnectionStore.sendInput 409 回落', () => {
  let fetchSpy: ReturnType<typeof vi.spyOn>;

  beforeEach(() => {
    setActivePinia(createPinia());
    fetchSpy = vi.spyOn(globalThis, 'fetch');
  });

  afterEach(() => {
    fetchSpy.mockRestore();
  });

  it('输入被拒 409：写入 controlError，同步 controller，清 inputInFlight', async () => {
    fetchSpy.mockResolvedValueOnce(
      new Response(
        JSON.stringify({
          error: 'CONTROL_HELD_BY_AGENT',
          code: 'CONTROL_HELD_BY_AGENT',
          reason: 'CONTROL_HELD_BY_AGENT',
          message: 'Input is allowed only for the current Controller',
          hint: '重新 acquire 可获取快照',
          controller: agentController(),
          state: 'held',
        }),
        { status: 409 },
      ),
    );
    const conn = useConnectionStore();
    const game = useGameStore();
    conn.status = 'connected';
    game.setInputInFlight();
    expect(game.inputInFlight).toBe(true);

    await conn.sendInput('0');

    expect(fetchSpy).toHaveBeenCalledWith(
      'http://localhost:5173/input',
      expect.objectContaining({ method: 'POST' }),
    );
    expect(conn.controller?.kind).toBe('agent');
    expect(conn.isSpectator).toBe(true);
    expect(conn.controlError).toMatch(/agent|操控|接管/i);
    expect(game.inputInFlight).toBe(false);
  });
});
