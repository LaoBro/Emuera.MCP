import { describe, it, expect } from 'vitest';
import { deriveDisplayStatus } from '../loadingStatus';
import type { ConnectionStatus } from '../../stores/connection';

/**
 * Issue 11 D5：deriveDisplayStatus 纯函数单测——覆盖"加载中"状态契约。
 *
 * 测试矩阵：
 * - reloadStatus='loading' + 任意 connectionStatus → 'loading'（屏蔽 disconnected 闪断）
 * - reloadStatus='idle' + connectionStatus=X → X（原样透传）
 *
 * 这是 TerminalView.vue / ConnectionPanel.vue 派生显示状态的共享契约——
 * 测试覆盖即验证 UI 行为契约。
 */
describe('deriveDisplayStatus — issue 11 D5', () => {
  const allConnStatuses: ConnectionStatus[] = [
    'disconnected',
    'connecting',
    'connected',
    'reconnecting',
  ];

  describe("reloadStatus='loading' 时强制返 'loading'", () => {
    for (const cs of allConnStatuses) {
      it(`connectionStatus='${cs}' → 'loading'（屏蔽 disconnected 闪断等）`, () => {
        expect(deriveDisplayStatus('loading', cs)).toBe('loading');
      });
    }

    it("关键场景：reloadStatus='loading' + connectionStatus='disconnected' → 'loading'（而非 'disconnected'）", () => {
      // loadGame 切换游戏目录时：disconnect 旧 WS → POST /load-game → connect 新 WS
      // 期间 conn.status 短暂为 'disconnected'，UI 必须显示"加载中…"而非"已断开"
      expect(deriveDisplayStatus('loading', 'disconnected')).toBe('loading');
      expect(deriveDisplayStatus('loading', 'disconnected')).not.toBe('disconnected');
    });
  });

  describe("reloadStatus='idle' 时原样透传 connectionStatus", () => {
    for (const cs of allConnStatuses) {
      it(`connectionStatus='${cs}' → '${cs}'`, () => {
        expect(deriveDisplayStatus('idle', cs)).toBe(cs);
      });
    }
  });

  it('返回值类型为 "loading" | ConnectionStatus', () => {
    // TypeScript 编译期已校验；运行时验证值集合
    const validValues = ['loading', 'disconnected', 'connecting', 'connected', 'reconnecting'];
    expect(validValues).toContain(deriveDisplayStatus('idle', 'disconnected'));
    expect(validValues).toContain(deriveDisplayStatus('loading', 'disconnected'));
    expect(validValues).toContain(deriveDisplayStatus('idle', 'connected'));
  });
});
