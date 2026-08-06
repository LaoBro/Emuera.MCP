// @vitest-environment happy-dom
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { mount } from '@vue/test-utils';
import { createPinia, setActivePinia } from 'pinia';
import TerminalDisplay from '../TerminalDisplay.vue';
import { useGameStore } from '../../stores/game';
import { useConnectionStore } from '../../stores/connection';
import { loadImageAndReadPixel } from '../../lib/imageLayout';
import type { DisplaySnapshot, PrintSegment, BgImageState } from '../../types/protocol';

/**
 * TerminalDisplay 组件测试（issue 04，spec Q7 seam ④——首个 happy-dom 组件测试）。
 *
 * 覆盖 Q6 渲染模型的模板层行为：
 * - 掩膜渲染：image segment → 掩膜 span + img（src 经 resolveResource 拼 /assets）
 * - 溢出：img margin-top = ypos（负值向上溢出）
 * - rect 渲染色块
 * - srcb 悬停切换 <img src>
 * - srcm 热区：点击 → canvas 读像素（mock）→ 提交十进制色值 + 阻断按钮冒泡
 * - 非 srcm 图片在按钮内：点击冒泡走既有按钮路径
 * - 背景层：bgImages 渲染 + depth 排序 + opacity
 *
 * 虚拟滚动/捏合缩放 mock（模板层的行切片与手势与此测试无关）；
 * canvas 胶水 loadImageAndReadPixel mock（浏览器像素读取不在组件测试驱动）。
 */

vi.mock('../../composables/useVirtualScroll', () => ({
  useVirtualScroll: () => ({
    startIndex: { value: 0 },
    endIndex: { value: 200 },
    totalHeight: { value: 1900 },
    offsetY: { value: 0 },
    isStickyToBottom: { value: true },
    adjustScrollTop: vi.fn(),
    scrollToBottom: vi.fn(),
  }),
}));

vi.mock('../../composables/usePinchZoom', () => ({
  usePinchZoom: () => {},
}));

vi.mock('../../lib/imageLayout', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../lib/imageLayout')>();
  return { ...actual, loadImageAndReadPixel: vi.fn() };
});

function imgSeg(partial: Partial<PrintSegment['image']> = {}): PrintSegment {
  return {
    text: '<img>',
    image: {
      src: 'img/test.png',
      srcb: null,
      srcm: null,
      width: 36,
      height: 18,
      ypos: 0,
      ...partial,
    },
  };
}

function rectSeg(): PrintSegment {
  return { text: '<rect>', shape: { type: 'rect', x: 0, y: 0, width: 18, height: 18, color: '#FF0000' } };
}

function line(segments: PrintSegment[]): DisplaySnapshot['lines'][number] {
  return { entries: [{ segments }], align: 'left', isLineEnd: true };
}

async function mountWith(lines: DisplaySnapshot['lines'][number][], bgImages: BgImageState[] = [], scale?: number) {
  const pinia = createPinia();
  setActivePinia(pinia);
  const game = useGameStore();
  // issue 09：可选缩放——SegmentRenderer 从 store 读 effectiveScale 计算图片几何
  if (scale !== undefined) game.setScale(scale);
  const conn = useConnectionStore();
  conn.status = 'connected';
  conn.sendInput = vi.fn();
  // Pinia setup store 自动解包 ref——displayState 是值不是 Ref，直接整体赋值
  game.displayState = { ...game.displayState, lines, bgImages, state: 'WaitInput', inputType: 'IntValue' };
  game.currentTurnGeneration = 0;
  const wrapper = mount(TerminalDisplay, { global: { plugins: [pinia] } });
  return { wrapper, game, conn };
}

describe('TerminalDisplay: image 掩膜渲染（Q6）', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('image segment 渲染掩膜 span + img（src 经 resolveResource 拼 /assets）', async () => {
    const { wrapper } = await mountWith([line([imgSeg()])]);
    const mask = wrapper.find('.term-mask');
    expect(mask.exists()).toBe(true);
    const img = wrapper.find('.term-img');
    expect(img.attributes('src')).toBe('/assets/img/test.png');
    // issue 09：img 尺寸在 style（× effectiveScale），不再用 width/height attribute
    expect(img.attributes('width')).toBeUndefined();
    expect(img.attributes('style')).toContain('width: 36px');
    expect(img.attributes('style')).toContain('height: 18px');
  });

  it('掩膜高度钉死=行高（CSS 变量），overflow 可见，宽度×缩放', async () => {
    const { wrapper } = await mountWith([line([imgSeg()])]);
    const mask = wrapper.find('.term-mask');
    const style = mask.attributes('style') ?? '';
    expect(style).toContain('var(--term-line-min-height');
    expect(style).toContain('overflow: visible');
    expect(style).toContain('vertical-align: top');
    // issue 09：掩膜宽度也吃缩放（此前写死 px 不随缩放）
    expect(style).toContain('width: 36px');
  });

  it('ypos 溢出：img margin-top = ypos×缩放（可为负）', async () => {
    const { wrapper } = await mountWith([line([imgSeg({ ypos: -8 })])]);
    const img = wrapper.find('.term-img');
    expect(img.attributes('style')).toContain('margin-top: -8px');
  });

  // ---------- issue 07（协议 v9）：crop 裁切渲染 ----------

  it('crop 裁切：掩膜内嵌 overflow:hidden 定尺寸容器 + img 负偏移', async () => {
    const { wrapper } = await mountWith([line([imgSeg({ crop: { x: -36, y: 0, imgWidth: 72, imgHeight: 36 } })])]);
    // 外层掩膜保持 overflow:visible（"后行盖先行"语义不破坏）
    const mask = wrapper.find('.term-mask');
    expect(mask.attributes('style')).toContain('overflow: visible');
    // 容器：定尺寸（裁切后显示尺寸，×缩放）+ overflow:hidden
    const crop = wrapper.find('.term-crop');
    expect(crop.exists()).toBe(true);
    const style = crop.attributes('style') ?? '';
    expect(style).toContain('overflow: hidden');
    expect(style).toContain('width: 36px');
    expect(style).toContain('height: 18px');
    // img：元素尺寸 = 整图×缩放（72×36，再×缩放），负偏移 margin-left
    const img = wrapper.find('.term-img');
    expect(img.attributes('style')).toContain('width: 72px');
    expect(img.attributes('style')).toContain('height: 36px');
    expect(img.attributes('style')).toContain('margin-left: -36px');
    expect(img.attributes('style')).toContain('margin-top: 0px');
  });

  it('crop 裁切：ypos 转移到容器（img 不再带 margin-top）', async () => {
    const { wrapper } = await mountWith([line([imgSeg({ ypos: -8, crop: { x: 0, y: -4, imgWidth: 72, imgHeight: 36 } })])]);
    const crop = wrapper.find('.term-crop');
    expect(crop.attributes('style')).toContain('margin-top: -8px');
    const img = wrapper.find('.term-img');
    expect(img.attributes('style')).toContain('margin-top: -4px'); // crop.y 在 img 上
    expect(img.attributes('style')).not.toContain('margin-top: -8px');
  });

  it('无 crop 图片不渲染 term-crop 容器（v8 行为锁定）', async () => {
    const { wrapper } = await mountWith([line([imgSeg()])]);
    expect(wrapper.find('.term-crop').exists()).toBe(false);
    const img = wrapper.find('.term-img');
    expect(img.attributes('style')).toContain('width: 36px');
    expect(img.attributes('style')).toContain('height: 18px');
  });

  it('rect segment 渲染掩膜 + 色块（background 颜色，尺寸×缩放）', async () => {
    const { wrapper } = await mountWith([line([rectSeg()])]);
    const rect = wrapper.find('.term-rect');
    expect(rect.exists()).toBe(true);
    expect(rect.attributes('style')).toContain('background-color: #FF0000');
    expect(rect.attributes('style')).toContain('width: 18px');
  });

  // ---------- issue 09：缩放（effectiveScale 作用于图片几何） ----------

  it('缩放 1.5 倍：掩膜/图片/crop 全部几何等比放大', async () => {
    const { wrapper } = await mountWith(
      [line([imgSeg({ ypos: -8, crop: { x: -36, y: 0, imgWidth: 72, imgHeight: 36 } })])],
      [],
      1.5,
    );
    // 掩膜宽 36×1.5=54；img 72×1.5=108；crop.y=-36×1.5=-54；ypos -8×1.5=-12
    const mask = wrapper.find('.term-mask');
    expect(mask.attributes('style')).toContain('width: 54px');
    const crop = wrapper.find('.term-crop');
    expect(crop.attributes('style')).toContain('width: 54px');
    expect(crop.attributes('style')).toContain('height: 27px');
    expect(crop.attributes('style')).toContain('margin-top: -12px');
    const img = wrapper.find('.term-img');
    expect(img.attributes('style')).toContain('width: 108px');
    expect(img.attributes('style')).toContain('height: 54px');
    expect(img.attributes('style')).toContain('margin-left: -54px');
  });

  // ---------- issue 09：文本段绘制序（position:relative 盖图片溢出） ----------

  it('文本段 position:relative——强制定位层绘制（图片跨行溢出时文本覆盖其上）', async () => {
    // 图片行 + 后行纯文本：文本段必须带 position:relative
    const { wrapper } = await mountWith([
      line([imgSeg()]),
      line([{ text: '后行文本', color: null, bold: null, italic: null, fontname: null }]),
    ]);
    const textSeg = wrapper.findAll('.term-seg').find((s) => s.text() === '后行文本');
    expect(textSeg).toBeDefined();
    expect(textSeg!.attributes('style')).toContain('position: relative');
  });

  it('图片/矩形掩膜不设 position（保持静态，不进入定位层盖文本）', async () => {
    const { wrapper } = await mountWith([line([imgSeg({ ypos: -50 })])]);
    const mask = wrapper.find('.term-mask');
    expect(mask.attributes('style')).not.toContain('position:');
  });
});

describe('TerminalDisplay: srcb 悬停切换', () => {
  it('mouseenter 切换 img src 到 srcb，mouseleave 还原', async () => {
    const { wrapper } = await mountWith([line([imgSeg({ srcb: 'img/hover.png' })])]);
    const img = wrapper.find('.term-img');
    expect(img.attributes('src')).toBe('/assets/img/test.png');

    await wrapper.find('.term-mask').trigger('mouseenter');
    expect(wrapper.find('.term-img').attributes('src')).toBe('/assets/img/hover.png');

    await wrapper.find('.term-mask').trigger('mouseleave');
    expect(wrapper.find('.term-img').attributes('src')).toBe('/assets/img/test.png');
  });

  it('无 srcb 的图片 mouseenter 不切换', async () => {
    const { wrapper } = await mountWith([line([imgSeg()])]);
    await wrapper.find('.term-mask').trigger('mouseenter');
    expect(wrapper.find('.term-img').attributes('src')).toBe('/assets/img/test.png');
  });
});

describe('TerminalDisplay: srcm 热区点击', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('srcm 图片点击 → 提交映射色（十进制）且阻断按钮冒泡', async () => {
    vi.mocked(loadImageAndReadPixel).mockResolvedValue(0xff0000);
    // srcm 图在按钮 entry 内，按钮原值 = 999
    const seg = imgSeg({ srcm: 'img/map.png' });
    const snap = {
      lines: [{
        entries: [{ segments: [seg], button: { value: 999, isInteger: true, generation: 0 } }],
        align: 'left' as const,
        isLineEnd: true,
      }],
      bgColor: '#000000',
      bgImages: [],
      state: 'WaitInput',
      inputType: 'IntValue',
      needValue: true,
      protocolVersion: 8,
      generation: 0,
    };
    const { wrapper, conn } = await mountWith(snap.lines);

    // mock getBoundingClientRect：img 元素（onImageClick 用 img 自身 rect）display 尺寸 36×18 @ (0,0)
    const imgEl = wrapper.find('.term-img');
    imgEl.element.getBoundingClientRect = () => ({
      left: 0, top: 0, width: 36, height: 18, right: 36, bottom: 18,
      x: 0, y: 0, toJSON: () => ({}),
    });
    // naturalWidth/Height 是只读 getter（happy-dom），defineProperty 重定义
    Object.defineProperty(imgEl.element, 'naturalWidth', { value: 36, configurable: true });
    Object.defineProperty(imgEl.element, 'naturalHeight', { value: 18, configurable: true });

    await wrapper.find('.term-mask').trigger('click', { clientX: 18, clientY: 9 });

    expect(loadImageAndReadPixel).toHaveBeenCalledWith('/assets/img/map.png', 18, 9);
    expect(conn.sendInput).toHaveBeenCalledWith('16711680'); // 0xFF0000 十进制
    expect(conn.sendInput).not.toHaveBeenCalledWith('999'); // 未提交按钮原值
  });

  it('srcm 读像素失败（null）→ 不提交、但已阻断按钮冒泡（热区接管语义）', async () => {
    vi.mocked(loadImageAndReadPixel).mockResolvedValue(null);
    const seg = imgSeg({ srcm: 'img/map.png' });
    const snap = {
      lines: [{
        entries: [{ segments: [seg], button: { value: 999, isInteger: true, generation: 0 } }],
        align: 'left' as const,
        isLineEnd: true,
      }],
      bgColor: '#000000',
      bgImages: [],
      state: 'WaitInput',
      inputType: 'IntValue',
      needValue: true,
      protocolVersion: 8,
      generation: 0,
    };
    const { wrapper, conn } = await mountWith(snap.lines);
    const imgEl = wrapper.find('.term-img');
    imgEl.element.getBoundingClientRect = () => ({
      left: 0, top: 0, width: 36, height: 18, right: 36, bottom: 18,
      x: 0, y: 0, toJSON: () => ({}),
    });
    // 必须 mock natural 尺寸——否则零尺寸守卫让 clickToImagePixel 提前返回 null，
    // 测不到"读像素失败"路径（review 2026-08-06 假用例修复）
    Object.defineProperty(imgEl.element, 'naturalWidth', { value: 36, configurable: true });
    Object.defineProperty(imgEl.element, 'naturalHeight', { value: 18, configurable: true });

    await wrapper.find('.term-mask').trigger('click', { clientX: 18, clientY: 9 });

    expect(loadImageAndReadPixel).toHaveBeenCalled();
    expect(conn.sendInput).not.toHaveBeenCalled(); // 读像素失败不提交
  });

  it('非 srcm 图片在按钮内 → 点击冒泡提交按钮原值', async () => {
    const seg = imgSeg(); // 无 srcm
    const snap = {
      lines: [{
        entries: [{ segments: [seg], button: { value: 42, isInteger: true, generation: 0 } }],
        align: 'left' as const,
        isLineEnd: true,
      }],
      bgColor: '#000000',
      bgImages: [],
      state: 'WaitInput',
      inputType: 'IntValue',
      needValue: true,
      protocolVersion: 8,
      generation: 0,
    };
    const { wrapper, conn } = await mountWith(snap.lines);

    await wrapper.find('.term-mask').trigger('click', { clientX: 10, clientY: 5 });

    expect(conn.sendInput).toHaveBeenCalledWith('42');
  });
});

describe('TerminalDisplay: 背景图层', () => {
  it('bgImages 渲染 + depth 排序（大 depth 在后/在上）+ opacity', async () => {
    const bgImages: BgImageState[] = [
      { src: 'bg/top.png', depth: 5, opacity: 0.8 },
      { src: 'bg/bottom.png', depth: 0, opacity: 0.4 },
      { src: 'bg/mid.png', depth: 2, opacity: 1 },
    ];
    const { wrapper } = await mountWith([line([imgSeg()])], bgImages);

    const layer = wrapper.find('.term-bg-layer');
    expect(layer.exists()).toBe(true);
    expect(layer.attributes('style')).toContain('position: fixed');
    expect(layer.attributes('style')).toContain('z-index: -1');

    const imgs = wrapper.findAll('.term-bg-img');
    expect(imgs).toHaveLength(3);
    // 排序后：depth 0 → 2 → 5（渲染顺序=升序，大 depth 最后画=视觉在上）
    expect(imgs[0].attributes('src')).toBe('/assets/bg/bottom.png');
    expect(imgs[1].attributes('src')).toBe('/assets/bg/mid.png');
    expect(imgs[2].attributes('src')).toBe('/assets/bg/top.png');
    expect(imgs[0].attributes('style')).toContain('opacity: 0.4');
    expect(imgs[2].attributes('style')).toContain('opacity: 0.8');
  });

  it('无 bgImages 不渲染背景层', async () => {
    const { wrapper } = await mountWith([line([imgSeg()])]);
    expect(wrapper.find('.term-bg-layer').exists()).toBe(false);
  });
});
