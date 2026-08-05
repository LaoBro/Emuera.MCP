// @vitest-environment happy-dom
import { describe, it, expect, afterEach } from 'vitest';
import { resolveResource } from '../resourceResolver';

/**
 * 05 — resolveResource MAUI 分支（spec Q3 路线 B / Q6 前端抽象）。
 * Web 返回 /assets/{path}；MAUI（Windows unpackaged 或安卓）返回 https://game.local/{path}。
 * 期望值锁**字面量** 'game.local' 而非被测常量——跨语言契约（C# GameAssetConstants）
 * 漂移时测试变红而不是跟着变绿。
 */
describe('resolveResource', () => {
  afterEach(() => {
    mockLocation('http://localhost:3000/');
  });

  /** happy-dom 的 setURL 无 TS 类型（happyDOM 属性未声明）——用 defineProperty mock location。 */
  function mockLocation(url: string): void {
    const u = new URL(url);
    Object.defineProperty(window, 'location', {
      value: { protocol: u.protocol, hostname: u.hostname },
      configurable: true,
      writable: true,
    });
  }

  it('浏览器环境 → /assets/{path}', () => {
    mockLocation('http://localhost:5173/');
    expect(resolveResource('img/portrait.png')).toBe('/assets/img/portrait.png');
  });

  it('安卓 MAUI（file:// 协议）→ https://game.local/{path}', () => {
    mockLocation('file:///android_asset/wwwroot/index.html');
    expect(resolveResource('img/portrait.png')).toBe('https://game.local/img/portrait.png');
  });

  it('Windows MAUI unpackaged（https://app.local）→ https://game.local/{path}', () => {
    mockLocation('https://app.local/index.html');
    expect(resolveResource('img/portrait.png')).toBe('https://game.local/img/portrait.png');
  });

  it('子目录路径原样透传', () => {
    mockLocation('file:///android_asset/wwwroot/index.html');
    expect(resolveResource('bg/forest.png')).toBe('https://game.local/bg/forest.png');
  });
});
