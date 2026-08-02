import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { ref, nextTick } from 'vue';
import { usePinchZoom } from '../usePinchZoom';

/**
 * usePinchZoom 单测——双指捏合缩放 composable。
 *
 * 测试矩阵：
 * - 单指移动不触发缩放
 * - 双指张开 → setScale(startScale * dist/startDist)（放大）
 * - 双指捏拢 → 缩小
 * - minMove 阈值内不缩放（两指轻点 / 手指抖动）
 * - 三指仍用最早两指作锚
 * - 手势中 / 结束后 suppressMs 内 click 被拦截（stopPropagation 断言）
 * - 超过 suppressMs 后 click 放行
 * - pointercancel 同样结束手势
 * - 卸载（target → null）移除全部监听
 *
 * 测试环境：Vitest node 环境（无 DOM）——按仓库惯例：
 * - window 用 vi.stubGlobal 模拟（addEventListener/removeEventListener 间谍）
 * - 容器元素用 mock（addEventListener/removeEventListener 间谍）
 * - 监听器从调用记录中提取，手动派发合成事件（cast 为 PointerEvent/MouseEvent）
 * - composable 内部 watch(target, { immediate, flush: 'post' })——
 *   挂 ref 后须 await nextTick() 让监听器完成挂载
 */

interface MockWindow {
  addEventListener: ReturnType<typeof vi.fn>;
  removeEventListener: ReturnType<typeof vi.fn>;
}

interface MockEl extends MockWindow {}

function makeMockWindow(): MockWindow {
  return { addEventListener: vi.fn(), removeEventListener: vi.fn() };
}

function makeMockEl(): MockEl {
  return { addEventListener: vi.fn(), removeEventListener: vi.fn() };
}

/** 从 mock 的 addEventListener 记录提取指定事件监听器。 */
function getListener(mock: MockWindow, type: string): (e: unknown) => void {
  const call = mock.addEventListener.mock.calls.find((c: unknown[]) => c[0] === type);
  if (!call) throw new Error(`listener '${type}' 未注册`);
  return call[1] as (e: unknown) => void;
}

/** 合成 pointer 事件——仅携带 composable 用到的字段。 */
function pe(pointerId: number, x: number, y: number): PointerEvent {
  return { pointerId, clientX: x, clientY: y } as unknown as PointerEvent;
}

/** 合成 click 事件——stopPropagation/preventDefault 用间谍断言。 */
interface ClickSpy extends MouseEvent {
  _stopped: boolean;
  _prevented: boolean;
}

function ce(): ClickSpy {
  return {
    _stopped: false,
    _prevented: false,
    stopPropagation(this: ClickSpy) { this._stopped = true; },
    preventDefault(this: ClickSpy) { this._prevented = true; },
  } as unknown as ClickSpy;
}

interface Harness {
  targetRef: ReturnType<typeof ref<HTMLElement | null>>;
  el: MockEl;
  win: MockWindow;
  currentScale: { value: number };
  setScale: ReturnType<typeof vi.fn>;
  pointerDown: (id: number, x: number, y: number) => void;
  pointerMove: (id: number, x: number, y: number) => void;
  pointerUp: (id: number, x: number, y: number) => void;
  pointerCancel: (id: number, x: number, y: number) => void;
  click: () => ReturnType<typeof ce>;
}

/**
 * 组装测试夹具：
 * - 模拟 game store 的 setScale 语义——clamp [0.5, 2.0] + 更新 currentScale
 *   （与 stores/game.ts setScale 一致，让连续手势锚定"当前值"）
 * - 返回事件派发器——从 mock 提取监听器并手动调用
 */
function setup(opts: { suppressMs?: number; minMove?: number } = {}): Harness {
  const win = makeMockWindow();
  vi.stubGlobal('window', win as unknown as Window & typeof globalThis);

  const targetRef = ref<HTMLElement | null>(null);
  const el = makeMockEl();
  targetRef.value = el as unknown as HTMLElement;

  const currentScale = { value: 1.0 };
  const setScale = vi.fn((v: number) => {
    currentScale.value = Math.max(0.5, Math.min(2.0, v));
  });

  usePinchZoom({
    target: targetRef,
    getScale: () => currentScale.value,
    setScale,
    ...opts,
  });

  // immediate watch + flush 'post'——测试体内先 await nextTick() 再派发事件
  return {
    targetRef,
    el,
    win,
    currentScale,
    setScale,
    pointerDown: (id, x, y) => getListener(el, 'pointerdown')(pe(id, x, y)),
    pointerMove: (id, x, y) => getListener(win, 'pointermove')(pe(id, x, y)),
    pointerUp: (id, x, y) => getListener(win, 'pointerup')(pe(id, x, y)),
    pointerCancel: (id, x, y) => getListener(win, 'pointercancel')(pe(id, x, y)),
    click: () => {
      const e = ce();
      getListener(win, 'click')(e);
      return e;
    },
  };
}

describe('usePinchZoom: 手势与缩放', () => {
  let h: Harness;

  beforeEach(() => {
    h = setup();
  });

  afterEach(() => {
    vi.unstubAllGlobals();
    vi.useRealTimers();
  });

  it('单指移动不触发缩放', async () => {
    await nextTick();
    h.pointerDown(1, 10, 10);
    h.pointerMove(1, 200, 300);
    h.pointerUp(1, 200, 300);
    expect(h.setScale).not.toHaveBeenCalled();
  });

  it('双指张开 → 按距离比例放大', async () => {
    await nextTick();
    h.currentScale.value = 1.0;
    // 起始间距 100px：指 1 在 (0,100)、指 2 在 (0,0)
    h.pointerDown(1, 0, 100);
    h.pointerDown(2, 0, 0);
    // 指 1 移到 (0,200)——间距 200 → scale = 1.0 * 200/100 = 2.0
    h.pointerMove(1, 0, 200);
    expect(h.setScale).toHaveBeenLastCalledWith(2.0);
    // 再张开到 300 → scale = 3.0（未经 clamp 的原值——clamp 是 store 的职责）
    h.pointerMove(1, 0, 300);
    expect(h.setScale).toHaveBeenLastCalledWith(3.0);
  });

  it('双指捏拢 → 按距离比例缩小', async () => {
    await nextTick();
    h.currentScale.value = 1.5;
    h.pointerDown(1, 0, 0);
    h.pointerDown(2, 0, 100);
    // 指 2 移到 (0,50)——间距 50 → scale = 1.5 * 50/100 = 0.75
    h.pointerMove(2, 0, 50);
    expect(h.setScale).toHaveBeenLastCalledWith(0.75);
  });

  it('比例超出 [0.5, 2.0]——投递原值，clamp 落在 setScale（mock 模拟 store 语义）', async () => {
    await nextTick();
    h.currentScale.value = 1.0;
    h.pointerDown(1, 0, 0);
    h.pointerDown(2, 0, 100);
    // 上限：间距 400 → 比例 4.0，投递原值 4.0，经 clamp 后当前值停在 2.0
    h.pointerMove(2, 0, 400);
    expect(h.setScale).toHaveBeenLastCalledWith(4.0);
    expect(h.currentScale.value).toBe(2.0);
    // 下限：间距 10 → 比例 0.1，clamp 后停在 0.5
    h.pointerMove(2, 0, 10);
    expect(h.setScale).toHaveBeenLastCalledWith(0.1);
    expect(h.currentScale.value).toBe(0.5);
  });

  it('间距变化未超 minMove 阈值不缩放（两指轻点）', async () => {
    await nextTick();
    h.pointerDown(1, 0, 0);
    h.pointerDown(2, 0, 100);
    // 位移 3px < 默认 minMove 5
    h.pointerMove(2, 0, 103);
    expect(h.setScale).not.toHaveBeenCalled();
    // 超过阈值后开始缩放
    h.pointerMove(2, 0, 110);
    expect(h.setScale).toHaveBeenLastCalledWith(1.1);
  });

  it('手势期间连续缩放——锚定 startScale 不随 setScale 结果漂移', async () => {
    await nextTick();
    h.currentScale.value = 1.0;
    h.pointerDown(1, 0, 0);
    h.pointerDown(2, 0, 100);
    // 间距 150 → 1.5；再回到 120 → 1.2（基于起始锚 100/1.0 换算，不基于中间值）
    h.pointerMove(2, 0, 150);
    expect(h.setScale).toHaveBeenLastCalledWith(1.5);
    h.pointerMove(2, 0, 120);
    expect(h.setScale).toHaveBeenLastCalledWith(1.2);
  });

  it('三指——仍用最早两指作锚', async () => {
    await nextTick();
    h.pointerDown(1, 0, 0);
    h.pointerDown(2, 0, 100);
    h.pointerDown(3, 0, 200);
    // 指 2 移到 (0,150)——间距(指1,指2) 150 → scale = 1.5
    h.pointerMove(2, 0, 150);
    expect(h.setScale).toHaveBeenLastCalledWith(1.5);
  });

  it('三指中非锚定指（第 3 指）抬起——捏合继续', async () => {
    await nextTick();
    h.pointerDown(1, 0, 0);
    h.pointerDown(2, 0, 100);
    h.pointerDown(3, 0, 200);
    h.pointerUp(3, 0, 200);
    // 锚定指仍是 1、2——继续按原锚换算，不发生跳变
    h.pointerMove(2, 0, 150);
    expect(h.setScale).toHaveBeenLastCalledWith(1.5);
  });

  it('三指中锚定指抬起——手势结束，剩余两指移动不再缩放', async () => {
    await nextTick();
    h.pointerDown(1, 0, 0);
    h.pointerDown(2, 0, 100);
    h.pointerDown(3, 0, 200);
    h.pointerMove(2, 0, 150);
    expect(h.setScale).toHaveBeenLastCalledWith(1.5);
    // 锚定指 1 抬起——即使还剩指 2、3 两指，手势也结束（不重锚，避免比例跳变）
    h.pointerUp(1, 0, 0);
    h.pointerMove(2, 0, 300);
    expect(h.setScale).toHaveBeenLastCalledWith(1.5);
  });

  it('未跟踪的 pointerId 的 move 被忽略', async () => {
    await nextTick();
    h.pointerDown(1, 0, 0);
    h.pointerDown(2, 0, 100);
    h.pointerMove(99, 0, 999);
    expect(h.setScale).not.toHaveBeenCalled();
  });

  it('单指抬起结束手势——剩余单指移动不再缩放', async () => {
    await nextTick();
    h.pointerDown(1, 0, 0);
    h.pointerDown(2, 0, 100);
    h.pointerMove(2, 0, 200);
    expect(h.setScale).toHaveBeenLastCalledWith(2.0);
    h.pointerUp(1, 0, 0);
    h.pointerMove(2, 0, 300);
    // 手势已结束——不缩放
    expect(h.setScale).toHaveBeenLastCalledWith(2.0);
  });
});

describe('usePinchZoom: click 抑制', () => {
  let h: Harness;

  beforeEach(() => {
    h = setup({ suppressMs: 300 });
  });

  afterEach(() => {
    vi.unstubAllGlobals();
    vi.useRealTimers();
  });

  it('捏合进行中 click 被拦截', async () => {
    await nextTick();
    h.pointerDown(1, 0, 0);
    h.pointerDown(2, 0, 100);
    const e = h.click();
    expect(e._stopped).toBe(true);
  });

  it('手势结束后 suppressMs 内 click 被拦截（合成 click 不误推进游戏）', async () => {
    await nextTick();
    h.pointerDown(1, 0, 0);
    h.pointerDown(2, 0, 100);
    h.pointerMove(2, 0, 200);
    h.pointerUp(1, 0, 0);
    h.pointerUp(2, 0, 200);
    const e = h.click();
    expect(e._stopped).toBe(true);
    expect(e._prevented).toBe(true);
  });

  it('超过 suppressMs 后 click 放行', async () => {
    vi.useFakeTimers();
    await nextTick();
    h.pointerDown(1, 0, 0);
    h.pointerDown(2, 0, 100);
    h.pointerUp(1, 0, 0);
    h.pointerUp(2, 0, 100);
    // 未超时——拦截
    expect(h.click()._stopped).toBe(true);
    // 越过 300ms 抑制窗口——放行
    vi.advanceTimersByTime(301);
    expect(h.click()._stopped).toBe(false);
  });

  it('pointercancel 同样结束手势并进入抑制窗口', async () => {
    await nextTick();
    h.pointerDown(1, 0, 0);
    h.pointerDown(2, 0, 100);
    // 浏览器接管（如原生滚动）→ pointercancel 结束手势
    h.pointerCancel(1, 0, 0);
    h.pointerCancel(2, 0, 100);
    expect(h.click()._stopped).toBe(true);
  });

  it('单指点击不受抑制（非手势）', async () => {
    await nextTick();
    h.pointerDown(1, 10, 10);
    h.pointerUp(1, 10, 10);
    expect(h.click()._stopped).toBe(false);
  });
});

describe('usePinchZoom: 生命周期', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
    vi.useRealTimers();
  });

  it('卸载（target → null）移除全部监听', async () => {
    const win = makeMockWindow();
    vi.stubGlobal('window', win as unknown as Window & typeof globalThis);
    const targetRef = ref<HTMLElement | null>(null);
    const el = makeMockEl();
    targetRef.value = el as unknown as HTMLElement;
    usePinchZoom({ target: targetRef, getScale: () => 1.0, setScale: vi.fn() });
    await nextTick();

    expect(el.addEventListener).toHaveBeenCalledWith('pointerdown', expect.any(Function));
    for (const type of ['pointermove', 'pointerup', 'pointercancel', 'click']) {
      expect(win.addEventListener.mock.calls.some((c: unknown[]) => c[0] === type)).toBe(true);
    }

    // 元素卸载（null）→ 全部 detach + 内部状态清空
    targetRef.value = null;
    await nextTick();

    expect(el.removeEventListener).toHaveBeenCalledWith('pointerdown', expect.any(Function));
    expect(win.removeEventListener).toHaveBeenCalledWith('click', expect.any(Function), true);
  });
});
