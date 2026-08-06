<script setup lang="ts">
import { ref } from 'vue';
import { resolveResource } from '../lib/resourceResolver';
import { useGameStore } from '../stores/game';
import type { PrintSegment, SegmentImage } from '../types/protocol';

/**
 * SegmentRenderer.vue — 单 entry 的 segments 渲染（issue 04，spec Q6 流式掩膜模型）。
 *
 * 从 TerminalDisplay 抽出的原因：button/entry 两个分支的 segment 渲染块完全重复
 * （review 2026-08-06 Duplicated Code）——子组件实例化后一处定义两处使用，
 * 且 hovered 状态随实例隔离（per-entry 悬停不串，消除父级 hoveredKey 字符串编码）。
 *
 * 职责：
 * - 三态渲染：image → 掩膜 span + img（srcb hover 切换）；shape → 掩膜 span + 色块；
 *   文本 → 普通 span
 * - srcm 热区：img 点击 emit('img-click') 给父组件（父持有 entry.button 上下文做
 *   守卫与提交）；非 srcm 图片不 emit——点击自然冒泡到按钮走既有路径
 * - 掩膜样式：inline-block + height: var(--term-line-min-height)（含 effectiveScale，
 *   由 .terminal 注入）——行盒恒定、虚拟滚动零改动
 * - issue 07（协议 v9）：裁切——掩膜内嵌 `.term-crop`（overflow:hidden 定尺寸容器）+ img
 *   负偏移（margin-left/top = crop.x/y）。**外层掩膜保持 overflow:visible**——不破坏
 *   "后行盖先行"语义；容器 overflow:hidden 只裁剪图集横向/纵向溢出（单层 static 定位，
 *   不产生 stacking context，后续行文字仍按 DOM 序覆盖）。
 * - issue 09：缩放——所有 px 几何 × effectiveScale（SegmentRenderer 从 store 读，
 *   JS 计算；不用 CSS calc/var——happy-dom 测试环境丢弃 calc(var()) 值）。
 *   文本段保持静态定位（issue 09 曾临时加 position:relative 强制定位层绘制，
 *   用户实测非 WinForms 语义——WinForms 是文本被图片挤到右侧——已回退，
 *   恢复纯 DOM 序"后行盖先行"）。
 *
 * 已知偏差（spec L132）：4 参 rect 绝对 x 按流位置 0（rectStyle left:0）。
 */
const props = defineProps<{ segments: PrintSegment[] }>();
const emit = defineEmits<{ imgClick: [e: MouseEvent, img: SegmentImage] }>();

/** issue 09：缩放几何——从 store 读 effectiveScale（JS 计算而非 CSS calc/var：
 *  happy-dom 测试环境丢弃 calc(var()) 值，JS 计算在浏览器与测试行为一致）。 */
const game = useGameStore();

/** 当前悬停的 segment 索引（per-entry 实例隔离，多图行不串）。 */
const hoveredIdx = ref<number | null>(null);

/** px 几何 × effectiveScale（四舍五入；负值正常）。 */
function scaled(px: number): string {
  return `${Math.round(px * game.effectiveScale)}px`;
}

/** 掩膜 span 样式——image 与 rect 共用（height 钉死=行高，宽度=段宽×缩放推进流式 x）。 */
function maskStyle(seg: SegmentImage | PrintSegment['shape']): Record<string, string> {
  return {
    display: 'inline-block',
    width: scaled(seg!.width),
    // 高度钉死=行高（含 effectiveScale，与虚拟滚动 rowHeight 严格一致）。
    // 变量由 .terminal inline style 恒注入（terminalStyle），无需 fallback——
    // 且带 fallback 逗号的 var() 会在 happy-dom 测试环境被解析器丢弃。
    height: 'var(--term-line-min-height)',
    verticalAlign: 'top',
    overflow: 'visible',
  };
}

/** 掩膜内图片样式——尺寸×缩放 + ypos 偏移（可为负，向上溢出）。 */
function imageStyle(img: SegmentImage): Record<string, string> {
  return {
    width: scaled(img.width),
    height: scaled(img.height),
    marginTop: scaled(img.ypos),
  };
}

/**
 * issue 07：裁切容器样式——定尺寸（裁切后显示尺寸，×缩放）+ overflow:hidden 裁剪图集溢出；
 * ypos 从 img 转移到容器（容器即可见区）。
 */
function cropStyle(img: SegmentImage): Record<string, string> {
  return {
    display: 'block',
    width: scaled(img.width),
    height: scaled(img.height),
    overflow: 'hidden',
    marginTop: scaled(img.ypos),
  };
}

/** issue 07：裁切内 img 样式——元素尺寸（整图×缩放系数，×缩放）+ 负偏移（margin-left/top）。 */
function cropImageStyle(img: SegmentImage): Record<string, string> {
  const c = img.crop!;
  return {
    width: scaled(c.imgWidth),
    height: scaled(c.imgHeight),
    marginLeft: scaled(c.x),
    marginTop: scaled(c.y),
  };
}

/** 掩膜内矩形样式——4 参 rect 绝对 x 按流位置 0（已知偏差）；y 透传（×缩放）。 */
function rectStyle(shape: NonNullable<PrintSegment['shape']>): Record<string, string> {
  return {
    display: 'block',
    position: 'relative',
    top: scaled(shape.y),
    left: '0',
    width: scaled(shape.width),
    height: scaled(shape.height),
    backgroundColor: shape.color,
  };
}
</script>

<template>
  <template v-for="(seg, segIdx) in props.segments" :key="segIdx">
    <span
      v-if="seg.image"
      class="term-seg term-mask"
      :style="maskStyle(seg.image)"
      @click="(e) => emit('imgClick', e, seg.image!)"
      @mouseenter="hoveredIdx = seg.image!.srcb ? segIdx : null"
      @mouseleave="hoveredIdx = null"
    >
      <!-- issue 07（协议 v9）：裁切（图集 sprite）——掩膜内嵌 overflow:hidden 定尺寸容器
           + img 负偏移；外层掩膜保持 overflow:visible（不破坏"后行盖先行"语义）。
           无裁切走既有 img 路径。issue 09：img 尺寸在 style（calc × --term-scale）。 -->
      <span v-if="seg.image.crop" class="term-crop" :style="cropStyle(seg.image)">
        <img
          class="term-img"
          :src="resolveResource(hoveredIdx === segIdx && seg.image!.srcb ? seg.image!.srcb : seg.image!.src)"
          :style="cropImageStyle(seg.image)"
          draggable="false"
        />
      </span>
      <img
        v-else
        class="term-img"
        :src="resolveResource(hoveredIdx === segIdx && seg.image!.srcb ? seg.image!.srcb : seg.image!.src)"
        :style="imageStyle(seg.image)"
        draggable="false"
      />
    </span>
    <span v-else-if="seg.shape" class="term-seg term-mask" :style="maskStyle(seg.shape)">
      <span class="term-rect" :style="rectStyle(seg.shape)" />
    </span>
    <!-- 文本段：静态定位（无 position/z-index）——按 DOM 序"后行盖先行"绘制 -->
    <span v-else class="term-seg" :style="segmentStyle(seg)">{{ seg.text }}</span>
  </template>
</template>

<script lang="ts">
import type { PrintSegment as PS } from '../types/protocol';

/** 文本段样式（保持与 TerminalDisplay.segmentStyle 同款——color/bold/italic 映射）。
 *  静态定位（无 position/z-index）——按 DOM 序参与"后行盖先行"（issue 09 曾加
 *  position:relative，用户实测非 WinForms 语义已回退）。 */
function segmentStyle(s: PS): Record<string, string> {
  const style: Record<string, string> = {};
  if (s.color) style.color = s.color;
  if (s.bold) style.fontWeight = 'bold';
  if (s.italic) style.fontStyle = 'italic';
  return style;
}
</script>

<style scoped>
/* v8 图片/矩形掩膜——inline-block 混入流式布局：
   高度钉死=行高（--term-line-min-height，含 effectiveScale）→ 行盒恒定、虚拟滚动零改动；
   overflow:visible 让溢出（ypos / 高于行高）自然绘制到下一行上方（WinForms 逐行绘制语义）。
   注意：掩膜必须保持静态定位（无 position/z-index）——否则进入定位层叠层，会盖过后行
   静态文字，破坏"后行盖先行"的忠实语义（review 2026-08-06）。 */
.term-mask {
  display: inline-block;
  vertical-align: top;
  overflow: visible;
  line-height: 0; /* 掩膜内 img/rect 是块级元素，行盒高度由掩膜 height 决定 */
}
.term-img {
  display: block;
}
/* issue 07（协议 v9）：裁切容器——overflow:hidden 只裁剪图集溢出；
   静态定位（无 position/z-index/transform），不产生 stacking context，
   后续行静态文字仍按 DOM 序覆盖（"后行盖先行"忠实语义）。 */
.term-crop {
  display: block;
  overflow: hidden;
  line-height: 0;
}
.term-rect {
  display: block;
}
</style>
