import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import {
  isMauiEnvironment,
  postInput,
  registerTurnHandler,
  sendReady,
} from '../mauiBridge';

/**
 * mauiBridge 单测——issue 07 / spec ID7。
 *
 * 覆盖：
 * - isMauiEnvironment：协议检测（ms-appx-web: / file: / http: / https: / SSR）
 * - postInput：feature detection（chrome.webview / emueraBridge / 两者都不存在）
 * - registerTurnHandler：window.__emueraOnTurn 注册 + 调用契约
 * - sendReady：发送 {"type":"ready"} 消息
 *
 * 测试环境：Vitest node 环境（无 window），用 vi.stubGlobal 模拟 window。
 * mauiBridge.ts 内部用 `typeof window === 'undefined'` 防 SSR——stub 前调 isMauiEnvironment 应返 false。
 */

/** 模拟 window 对象——location.protocol 可按用例覆盖，chrome/emueraBridge 按需挂。 */
function makeMockWindow(protocol: string = 'http:'): any {
  return {
    location: { protocol },
    chrome: undefined,
    emueraBridge: undefined,
    __emueraOnTurn: undefined,
  };
}

describe('mauiBridge (issue 07 / spec ID7)', () => {
  let mockWindow: any;

  beforeEach(() => {
    mockWindow = makeMockWindow();
    vi.stubGlobal('window', mockWindow);
  });

  afterEach(() => {
    vi.unstubAllGlobals();
    vi.restoreAllMocks();
  });

  /** 切换 location.protocol——重新 stub 整个 window 让 mauiBridge.ts 读到新值。 */
  function setProtocol(protocol: string): void {
    mockWindow = makeMockWindow(protocol);
    vi.stubGlobal('window', mockWindow);
  }

  // ===== isMauiEnvironment =====

  describe('isMauiEnvironment', () => {
    it('ms-appx-web: 协议 → true（Windows MAUI）', () => {
      setProtocol('ms-appx-web:');
      expect(isMauiEnvironment()).toBe(true);
    });

    it('file: 协议 → true（Android MAUI）', () => {
      setProtocol('file:');
      expect(isMauiEnvironment()).toBe(true);
    });

    it('http: 协议 → false（HTTP 模式）', () => {
      setProtocol('http:');
      expect(isMauiEnvironment()).toBe(false);
    });

    it('https: 协议 → false（HTTPS 模式）', () => {
      setProtocol('https:');
      expect(isMauiEnvironment()).toBe(false);
    });

    it('window 未定义（SSR / node 环境）→ false', () => {
      vi.unstubAllGlobals(); // 移除 window stub
      expect(isMauiEnvironment()).toBe(false);
    });
  });

  // ===== postInput =====

  describe('postInput', () => {
    it('window.chrome.webview 存在 → 调 chrome.webview.postMessage（Windows）', () => {
      const postMessage = vi.fn();
      mockWindow.chrome = { webview: { postMessage } };

      postInput('{"type":"ready"}');

      expect(postMessage).toHaveBeenCalledOnce();
      expect(postMessage).toHaveBeenCalledWith('{"type":"ready"}');
    });

    it('window.emueraBridge 存在 → 调 emueraBridge.postMessage（Android）', () => {
      const postMessage = vi.fn();
      mockWindow.emueraBridge = { postMessage };

      postInput('{"type":"input","value":"1"}');

      expect(postMessage).toHaveBeenCalledOnce();
      expect(postMessage).toHaveBeenCalledWith('{"type":"input","value":"1"}');
    });

    it('chrome.webview 优先于 emueraBridge（Windows 平台优先）', () => {
      const chromePost = vi.fn();
      const bridgePost = vi.fn();
      mockWindow.chrome = { webview: { postMessage: chromePost } };
      mockWindow.emueraBridge = { postMessage: bridgePost };

      postInput('test');

      expect(chromePost).toHaveBeenCalledOnce();
      expect(bridgePost).not.toHaveBeenCalled();
    });

    it('两者都不存在 → 静默 no-op（不抛错）', () => {
      expect(() => postInput('test')).not.toThrow();
    });
  });

  // ===== registerTurnHandler =====

  describe('registerTurnHandler', () => {
    it('注册 window.__emueraOnTurn 函数', () => {
      registerTurnHandler(() => {});

      expect(typeof mockWindow.__emueraOnTurn).toBe('function');
    });

    it('C# 传 turn 对象 → handler 收到 JSON 字符串', () => {
      const handler = vi.fn();
      registerTurnHandler(handler);

      const turn = { state: 'WaitInput', buttons: [1, 2, 3] };
      mockWindow.__emueraOnTurn(turn);

      expect(handler).toHaveBeenCalledOnce();
      const received = handler.mock.calls[0][0];
      expect(typeof received).toBe('string');
      expect(JSON.parse(received)).toEqual(turn);
    });

    it('C# 传字符串 → handler 收到原字符串（边界，正常不应发生）', () => {
      const handler = vi.fn();
      registerTurnHandler(handler);

      mockWindow.__emueraOnTurn('already-a-string');

      expect(handler).toHaveBeenCalledWith('already-a-string');
    });

    it('覆盖前次注册（多次调用幂等）', () => {
      const handler1 = vi.fn();
      const handler2 = vi.fn();
      registerTurnHandler(handler1);
      registerTurnHandler(handler2);

      mockWindow.__emueraOnTurn({ ok: true });

      expect(handler1).not.toHaveBeenCalled();
      expect(handler2).toHaveBeenCalledOnce();
    });
  });

  // ===== sendReady =====

  describe('sendReady', () => {
    it('调 postInput 投递 {"type":"ready"} 消息（Windows chrome.webview）', () => {
      const postMessage = vi.fn();
      mockWindow.chrome = { webview: { postMessage } };

      sendReady();

      expect(postMessage).toHaveBeenCalledOnce();
      const sent = postMessage.mock.calls[0][0];
      expect(JSON.parse(sent)).toEqual({ type: 'ready' });
    });

    it('调 postInput 投递 {"type":"ready"} 消息（Android emueraBridge）', () => {
      const postMessage = vi.fn();
      mockWindow.emueraBridge = { postMessage };

      sendReady();

      expect(postMessage).toHaveBeenCalledOnce();
      const sent = postMessage.mock.calls[0][0];
      expect(JSON.parse(sent)).toEqual({ type: 'ready' });
    });

    it('无桥接对象时静默 no-op（不抛错）', () => {
      expect(() => sendReady()).not.toThrow();
    });
  });
});
