import { watch, type Ref } from 'vue';

/**
 * usePinchZoom — 触屏双指捏合缩放 composable。
 *
 * 终端（.terminal）的缩放由 game store 的 `setScale` 统一管理（clamp [0.5, 2.0] +
 * localStorage 持久化）；渲染层（TerminalDisplay 的 terminalStyle/contentStyle）与虚拟滚动
 * 的 scrollTop 补偿（useVirtualScroll 的 rowHeight watch）都随 effectiveScale 自动生效——
 * 本 composable 只需把手势的"距离比例"换算成 scale 值投递给 setScale，无渲染层改动。
 *
 * 手势语义（Pointer Events，按 pointerId 跟踪）：
 * - 第 2 个手指落下（pointers.size === 2）时锚定起始两指间距 startDist 与起始 scale，
 *   锚定指记下 pointerId——后续缩放只看这两指的距离，不随其他手指的按下/抬起漂移；
 * - pointermove 按 `startScale * dist / startDist` 连续缩放（两端 clamp 由 setScale 自带）；
 * - 位移未超过 minMove 阈值前不缩放——过滤两指轻点与手指抖动；
 * - 3 指及以上仍取最早落下的两指作锚，其余手指忽略；锚定指抬起即结束手势——
 *   （不重锚到剩余指对，否则 startDist 与"当前两指"错位会导致缩放跳变）。
 *
 * 防误触（click 抑制）：
 * 浏览器会在手指抬起后为每个 pointer 合成 click 事件——若不拦截，捏合结束会误触发
 * TerminalDisplay 的 onTerminalClick（EnterKey/AnyKey 推进游戏）与 InputBar 的
 * window 级 onGlobalClick（AnyKey 提交）。本 composable 在 window 捕获阶段监听 click，
 * 捏合进行中 + 结束后 suppressMs 内 stopPropagation()——捕获阶段在 window 上
 * stopPropagation 会让事件根本走不到 .terminal / 按钮 / InputBar 的 bubble 监听。
 *
 * 依赖前置（须由调用方配置，见 TerminalDisplay）：
 * - 容器元素 touch-action 设为 `pan-x pan-y`：保留单指原生滚动（虚拟滚动依赖 scrollTop），
 *   同时声明双指手势归 JS 处理、禁用浏览器原生页面缩放与双击放大（否则双指会放大页面
 *   而非游戏画面）。
 */
export interface PinchZoomOptions {
  /** 手势容器元素 ref（.terminal）——pointerdown 在此元素上监听（事件冒泡）。 */
  target: Ref<HTMLElement | null>;
  /** 读取当前缩放值（game.effectiveScale）。 */
  getScale: () => number;
  /** 应用缩放（game.setScale——自带 [0.5, 2.0] clamp + localStorage 持久化）。 */
  setScale: (value: number) => void;
  /** 手势结束后抑制合成 click 的时长（毫秒）。默认 300。 */
  suppressMs?: number;
  /** 判定捏合的最小两指间距位移（像素）。默认 5——过滤两指轻点/手指抖动。 */
  minMove?: number;
}

interface TrackedPointer {
  x: number;
  y: number;
}

/** 捏合锚点——手势开始时固定，缩放按当前距离 / 起始距离的比例换算。 */
interface PinchAnchor {
  /** 锚定指 1 的 pointerId。 */
  idA: number;
  /** 锚定指 2 的 pointerId。 */
  idB: number;
  /** 起始两指间距（像素）。 */
  startDist: number;
  /** 起始缩放值。 */
  startScale: number;
  /** 是否已越过 minMove 阈值开始缩放——越过前不投递任何 setScale。 */
  thresholdPassed: boolean;
}

export function usePinchZoom(opts: PinchZoomOptions): void {
  const suppressMs = opts.suppressMs ?? 300;
  const minMove = opts.minMove ?? 5;

  /** 当前按下的 pointer——key 为 pointerId，value 为最近一次坐标。 */
  const pointers = new Map<number, TrackedPointer>();
  let pinchAnchor: PinchAnchor | null = null;
  /** 手势结束后允许合成 click 到达的最晚时间戳。 */
  let suppressUntil = 0;

  function distance(a: TrackedPointer, b: TrackedPointer): number {
    return Math.hypot(a.x - b.x, a.y - b.y);
  }

  function onPointerDown(e: PointerEvent): void {
    pointers.set(e.pointerId, { x: e.clientX, y: e.clientY });
    // 第 2 指落下 → 进入捏合态，锚定起始距离与起始 scale（记下锚定指 id）
    if (pointers.size === 2 && !pinchAnchor) {
      const [idA, idB] = pointers.keys();
      pinchAnchor = {
        idA,
        idB,
        startDist: distance(pointers.get(idA)!, pointers.get(idB)!),
        startScale: opts.getScale(),
        thresholdPassed: false,
      };
    }
  }

  function onPointerMove(e: PointerEvent): void {
    const p = pointers.get(e.pointerId);
    if (!p) return;
    p.x = e.clientX;
    p.y = e.clientY;
    const anchor = pinchAnchor;
    if (!anchor) return;
    // 只量锚定两指的距离——三指时其余手指的移动不影响缩放
    const pa = pointers.get(anchor.idA);
    const pb = pointers.get(anchor.idB);
    if (!pa || !pb) return;
    const d = distance(pa, pb);
    // 位移未越过阈值前不缩放——两指轻点 / 手指抖动不改变 scale
    if (!anchor.thresholdPassed) {
      if (Math.abs(d - anchor.startDist) < minMove) return;
      anchor.thresholdPassed = true;
    }
    opts.setScale((anchor.startScale * d) / anchor.startDist);
  }

  /**
   * pointerup / pointercancel 共用的收尾逻辑——手指抬起或手势被浏览器接管时结束捏合。
   * 锚定指抬起即结束手势（即使剩余仍有两指）——不重锚到剩余指对，避免 startDist
   * 与 firstTwo 错位导致缩放跳变；非锚定指（第 3+ 指）抬起不影响捏合。
   */
  function endPointer(e: PointerEvent): void {
    pointers.delete(e.pointerId);
    const anchor = pinchAnchor;
    if (anchor && (pointers.size < 2 || e.pointerId === anchor.idA || e.pointerId === anchor.idB)) {
      pinchAnchor = null;
      // 从此刻起抑制合成 click——两个手指抬起都会各自合成一个 click
      suppressUntil = Date.now() + suppressMs;
    }
  }

  /**
   * window 捕获阶段 click 拦截——捏合中或结束后 suppressMs 内 stopPropagation()，
   * 阻断浏览器合成 click 到达 .terminal / 按钮 / InputBar 的 window click 监听。
   * （捕获阶段在 window 上 stopPropagation 即终止事件传播，目标元素与其 bubble 监听收不到。）
   */
  function onWindowClick(e: MouseEvent): void {
    if (pinchAnchor || Date.now() < suppressUntil) {
      e.stopPropagation();
      e.preventDefault();
    }
  }

  /**
   * 挂载/卸载监听——watch(target) 监听元素挂载（template ref 在 onMounted 前为 null）。
   * pointerdown 挂在容器元素上（冒泡阶段，按钮内的手指同样算入手势）；
   * pointermove/up/cancel 挂在 window——手指滑出容器后仍能跟踪与收尾；
   * click 挂在 window 捕获阶段——见 onWindowClick 注释。
   */
  let currentEl: HTMLElement | null = null;
  watch(opts.target, (el) => {
    if (currentEl) {
      currentEl.removeEventListener('pointerdown', onPointerDown);
      window.removeEventListener('pointermove', onPointerMove);
      window.removeEventListener('pointerup', endPointer);
      window.removeEventListener('pointercancel', endPointer);
      window.removeEventListener('click', onWindowClick, true);
      pointers.clear();
      pinchAnchor = null;
    }
    currentEl = el ?? null;
    if (el) {
      el.addEventListener('pointerdown', onPointerDown);
      window.addEventListener('pointermove', onPointerMove);
      window.addEventListener('pointerup', endPointer);
      window.addEventListener('pointercancel', endPointer);
      window.addEventListener('click', onWindowClick, true);
    }
  }, { immediate: true, flush: 'post' });
}
