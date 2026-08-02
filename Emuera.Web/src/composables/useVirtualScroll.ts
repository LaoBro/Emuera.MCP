import { computed, nextTick, ref, watch, type ComputedRef, type Ref } from 'vue';

/**
 * useVirtualScroll — 定高虚拟滚动 composable（spec.md「虚拟滚动与shift_head协议」）。
 *
 * 仅渲染视口内 + overscan 缓冲的行（~40 行），DOM 节点从 ~50000 降到 ~400。
 * 行高确定（`effectiveLineHeight * effectiveScale`），CSS `white-space: pre` 保证
 * 行不换行——定高虚拟滚动的理想场景。
 *
 * 同时维护 `isStickyToBottom` 标志——用户在底部时新行自动跟随；向上滚过后不再自动跟随。
 * 该标志通过 `onStickyChange` 回调同步到 `useUiStore`，供 `InputBar` 实现"点击推进"守卫。
 *
 * 接口（spec.md决策一）：
 * - startIndex / endIndex：视口 + overscan 的渲染范围 [start, end)
 * - totalHeight：itemCount * rowHeight，撑总高度 spacer 用
 * - offsetY：startIndex * rowHeight，可见行 translateY 定位用
 * - isStickyToBottom：底部跟随标志
 * - scrollToBottom()：程序化滚到底部 + 置 sticky=true
 * - scrollToIndex(i)：跳转到指定行
 * - adjustScrollTop(delta)：shift_head 时补偿视觉位置
 *
 * 设计选择：
 * - 不依赖第三方库——核心逻辑 ~100 行（startIdx = floor(scrollTop / rowHeight)）
 * - 不用 ResizeObserver——行高通过 props 传入，由调用方 computed 派生
 * - threshold = 1 行高度——覆盖浮点误差与触屏手抖
 * - 缩放补偿：watch(rowHeight) 检测变化，按比例补偿 scrollTop 保持视觉位置
 */
export interface VirtualScrollOptions {
  /** 总行数（响应式） */
  itemCount: Ref<number>;
  /** 单行像素高度（响应式，含 scale） */
  rowHeight: Ref<number>;
  /** 视口容器元素 ref（须可滚动：overflow:auto） */
  viewportRef: Ref<HTMLElement | null>;
  /** 视口可见行数（itemCount < visibleCount 时全量渲染） */
  overscan?: number;
  /** isStickyToBottom 变化回调——同步到 ui store */
  onStickyChange?: (v: boolean) => void;
}

export interface VirtualScrollReturn {
  /** 渲染范围起始索引（含 overscan） */
  startIndex: ComputedRef<number>;
  /** 渲染范围结束索引（不含） */
  endIndex: ComputedRef<number>;
  /** 撑总高度的 spacer 高度（像素） */
  totalHeight: ComputedRef<number>;
  /** 可见区域 translateY 偏移（= startIndex * rowHeight） */
  offsetY: ComputedRef<number>;
  /** 是否贴底——true 时新行自动跟随、点击推进允许；false 时翻看历史 */
  isStickyToBottom: Ref<boolean>;
  /** 程序化滚到底部 + 置 sticky=true */
  scrollToBottom: () => void;
  /** 跳转到指定行索引（钳制到 [0, itemCount-1]） */
  scrollToIndex: (i: number) => void;
  /**
   * 补偿 scrollTop——shift_head 头部删除 count 行后调 `adjustScrollTop(-count * rowHeight)`
   * 保持视口内显示的行内容不变（仅 isStickyToBottom=false 时有意义）。
   */
  adjustScrollTop: (delta: number) => void;
}

/**
 * 默认 overscan——上下各 5 行，5000 行 × 19px ≈ 95000px 总高，视口约 600px ≈ 32 行可视，
 * 加 overscan 上下各 5 → 渲染约 42 行 DOM（~0.8% 的全量）。
 */
const DEFAULT_OVERSCAN = 5;

export function useVirtualScroll(opts: VirtualScrollOptions): VirtualScrollReturn {
  const overscan = opts.overscan ?? DEFAULT_OVERSCAN;

  // scrollTop 由 scroll 事件写入——computed 派生 startIndex/endIndex 依赖它
  const scrollTop = ref<number>(0);
  // 视口高度——scroll 事件时同步读 clientHeight；提供 computed 给派生用
  const viewportHeight = ref<number>(0);

  /** 单行高度——props.rowHeight，watch 用于缩放补偿。 */
  const rowHeight = computed(() => Math.max(1, opts.rowHeight.value));
  /** 总行数。 */
  const itemCount = computed(() => Math.max(0, opts.itemCount.value));

  /** 总高度——spacer 撑开滚动条。 */
  const totalHeight = computed(() => itemCount.value * rowHeight.value);

  /**
   * isStickyToBottom——初始 true（未滚动过视为在底部）。
   * scroll 事件触发时更新；scrollToBottom() 主动置 true；adjustScrollTop 后重判定。
   */
  const isStickyToBottom = ref<boolean>(true);

  /**
   * 视口内首行索引（不含 overscan）——floor(scrollTop / rowHeight)。
   * 钳制到 [0, max(0, itemCount - 1)]。
   */
  const rawStartIndex = computed(() => {
    if (itemCount.value === 0) return 0;
    return Math.min(
      Math.max(0, Math.floor(scrollTop.value / rowHeight.value)),
      itemCount.value - 1,
    );
  });

  /**
   * 渲染起始索引——rawStartIndex - overscan，钳制到 [0, itemCount-1]。
   * 含 overscan 缓冲行，避免快速滚动时顶部空白闪烁。
   */
  const startIndex = computed(() => {
    if (itemCount.value === 0) return 0;
    return Math.max(0, rawStartIndex.value - overscan);
  });

  /**
   * 视口内可见行数（不含 overscan）——ceil(viewportHeight / rowHeight)。
   * viewportHeight=0 时（首次渲染前）按 1 行兜底，避免 endIndex < startIndex。
   */
  const visibleCount = computed(() => {
    const h = viewportHeight.value;
    if (h <= 0) return 1;
    return Math.ceil(h / rowHeight.value);
  });

  /**
   * 渲染结束索引（不含）——rawStartIndex + visibleCount + overscan，钳制到 itemCount。
   */
  const endIndex = computed(() => {
    if (itemCount.value === 0) return 0;
    const end = rawStartIndex.value + visibleCount.value + overscan;
    return Math.min(itemCount.value, end);
  });

  /** 可见区域 translateY 偏移——startIndex * rowHeight。 */
  const offsetY = computed(() => startIndex.value * rowHeight.value);

  /**
   * 判定是否贴底——scrollTop + clientHeight >= scrollHeight - threshold。
   * threshold = 1 行高度，覆盖浮点误差与触屏手抖。
   *
   * 直接基于 DOM 读取（不依赖响应式 ref），scroll 事件触发时调用。
   */
  function computeSticky(el: HTMLElement): boolean {
    const threshold = rowHeight.value;
    return el.scrollTop + el.clientHeight >= el.scrollHeight - threshold;
  }

  /**
   * 重判定 sticky 并在变化时同步 onStickyChange 回调——onScroll/scrollToIndex/adjustScrollTop 共用。
   */
  function updateSticky(el: HTMLElement): void {
    const next = computeSticky(el);
    if (next !== isStickyToBottom.value) {
      isStickyToBottom.value = next;
      opts.onStickyChange?.(next);
    }
  }

  /**
   * 写入 el.scrollTop 并同步到响应式 ref——scrollToBottom/scrollToIndex/adjustScrollTop/
   * 缩放补偿共用。el.scrollTop 赋值后浏览器可能钳制（如超过 scrollHeight），故回读到 ref。
   */
  function applyScrollTop(el: HTMLElement, value: number): void {
    el.scrollTop = value;
    scrollTop.value = el.scrollTop;
  }

  /**
   * scroll 事件处理——更新 scrollTop + viewportHeight + isStickyToBottom。
   *
   * passive: true——滚动事件高频，不阻止默认行为。
   * 节流策略：浏览器 scroll 事件本身已合批到帧率（~16ms），无需额外 throttle。
   */
  function onScroll(): void {
    const el = opts.viewportRef.value;
    if (!el) return;
    scrollTop.value = el.scrollTop;
    viewportHeight.value = el.clientHeight;
    updateSticky(el);
  }

  /**
   * 同步读取视口尺寸——挂载时 + resize 时调用，避免首帧 viewportHeight=0 导致渲染 0 行。
   */
  function syncViewport(): void {
    const el = opts.viewportRef.value;
    if (!el) return;
    viewportHeight.value = el.clientHeight;
    // 同步 scrollTop——挂载时若已被浏览器恢复到上次位置，需读取
    if (scrollTop.value === 0 && el.scrollTop > 0) {
      scrollTop.value = el.scrollTop;
    }
  }

  /**
   * 渲染完成后写回 scrollTop——watch 默认 pre-flush 触发时 DOM 还是旧尺寸，
   * 立即写回会被旧内容上限钳制（缩放放大时视口停在旧底，底部内容掉出视口，
   * 表现为画面向上滚）；等渲染完成（spacer/行高/scrollHeight 已更新）再写。
   * target 延迟求值——取写回时刻元素的最新状态（如 scrollHeight）。
   * guard 可选——写回前重检查（如 itemCount 的 sticky 守卫）。
   */
  function applyScrollTopAfterRender(target: (el: HTMLElement) => number, guard?: () => boolean): void {
    void nextTick(() => {
      if (guard && !guard()) return;
      const el = opts.viewportRef.value;
      if (!el) return;
      applyScrollTop(el, target(el));
    });
  }

  /**
   * 缩放补偿——rowHeight 变化时按比例调整 scrollTop 保持视觉位置。
   *
   * 例：行高从 19 → 38（scale 1.0 → 2.0），原 scrollTop=190（看第 10 行）→
   * 新 scrollTop = (190/19) * 38 = 380（仍看第 10 行）。
   *
   * 不补偿会导致视觉位置跳变——行高加倍后 scrollTop 仍 190，看到的是第 5 行而非第 10 行。
   *
   * 时序：pre-flush 时 DOM 尚未按新尺寸重渲染，此刻读 el.scrollTop 得到的是缩放前的
   * 位置（渲染后读可能已被浏览器按新内容上限钳制，丢失锚点）；先换算补偿目标，
   * 写回延迟到渲染完成（applyScrollTopAfterRender）。
   */
  watch(rowHeight, (newH, oldH) => {
    if (oldH <= 0 || newH === oldH) return;
    const el = opts.viewportRef.value;
    if (!el) return;
    const ratio = newH / oldH;
    const target = el.scrollTop * ratio;
    applyScrollTopAfterRender(() => target);
  });

  /**
   * 监听 itemCount 变化——若 isStickyToBottom=true，新行到达时自动滚到底部。
   *
   * 排到渲染完成后（spacer 高度 = 新 itemCount * rowHeight）再读 scrollHeight；
   * 写回前重查 sticky——等待期间用户可能向上滚。
   */
  watch(itemCount, () => {
    if (!isStickyToBottom.value) return;
    const el = opts.viewportRef.value;
    if (!el) return;
    applyScrollTopAfterRender((el2) => el2.scrollHeight, () => isStickyToBottom.value);
  });

  /** 程序化滚到底部 + 置 sticky=true。 */
  function scrollToBottom(): void {
    const el = opts.viewportRef.value;
    if (!el) return;
    applyScrollTop(el, el.scrollHeight);
    if (!isStickyToBottom.value) {
      isStickyToBottom.value = true;
      opts.onStickyChange?.(true);
    }
  }

  /** 跳转到指定行索引——钳制到 [0, itemCount-1]。 */
  function scrollToIndex(i: number): void {
    const el = opts.viewportRef.value;
    if (!el) return;
    const target = Math.max(0, Math.min(i, Math.max(0, itemCount.value - 1)));
    applyScrollTop(el, target * rowHeight.value);
    // 跳转后重判定 sticky——若跳到底部则置 true
    updateSticky(el);
  }

  /**
   * 补偿 scrollTop——shift_head 头部删除 count 行后调 adjustScrollTop(-count * rowHeight)。
   *
   * 仅 isStickyToBottom=false 时有意义——用户在底部时 shift_head 后新内容自然到达，
   * 无需调整。调整后重判定 sticky：若调整后恰在底部 threshold 内，置 true（用户被
   * shift_head "推"到了底部）。
   */
  function adjustScrollTop(delta: number): void {
    const el = opts.viewportRef.value;
    if (!el) return;
    const newScrollTop = Math.max(0, el.scrollTop + delta);
    applyScrollTop(el, newScrollTop);
    // 重判定 sticky——shift_head 后用户可能被"推"到底部
    updateSticky(el);
  }

  // ---------- 挂载 scroll 事件监听 ----------
  //
  // 用 watch(viewportRef) 监听元素挂载/卸载——viewportRef 由 template ref 绑定，
  // 在 onMounted 之前是 null。挂载时 attach scroll listener + syncViewport；
  // 卸载时 detach（element GC 自然清理 listener，但显式 detach 更安全）。
  let currentEl: HTMLElement | null = null;
  watch(opts.viewportRef, (el) => {
    if (currentEl && currentEl !== el) {
      currentEl.removeEventListener('scroll', onScroll, { passive: true } as AddEventListenerOptions);
    }
    currentEl = el ?? null;
    if (el) {
      el.addEventListener('scroll', onScroll, { passive: true } as AddEventListenerOptions);
      syncViewport();
    }
  }, { immediate: true, flush: 'post' });

  return {
    startIndex,
    endIndex,
    totalHeight,
    offsetY,
    isStickyToBottom,
    scrollToBottom,
    scrollToIndex,
    adjustScrollTop,
  };
}
