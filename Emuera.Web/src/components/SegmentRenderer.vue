<script setup lang="ts">
import { ref } from 'vue';
import { resolveResource } from '../lib/resourceResolver';
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
 *
 * 已知偏差（spec L132）：4 参 rect 绝对 x 按流位置 0（rectStyle left:0）。
 */
const props = defineProps<{ segments: PrintSegment[] }>();
const emit = defineEmits<{ imgClick: [e: MouseEvent, img: SegmentImage] }>();

/** 当前悬停的 segment 索引（per-entry 实例隔离，多图行不串）。 */
const hoveredIdx = ref<number | null>(null);

/** 掩膜 span 样式——image 与 rect 共用（height 钉死=行高，宽度=段宽推进流式 x）。 */
function maskStyle(seg: SegmentImage | PrintSegment['shape']): Record<string, string> {
  return {
    display: 'inline-block',
    width: `${seg!.width}px`,
    // 高度钉死=行高（含 effectiveScale，与虚拟滚动 rowHeight 严格一致）。
    // 变量由 .terminal inline style 恒注入（terminalStyle），无需 fallback——
    // 且带 fallback 逗号的 var() 会在 happy-dom 测试环境被解析器丢弃。
    height: 'var(--term-line-min-height)',
    verticalAlign: 'top',
    overflow: 'visible',
  };
}

/** 掩膜内图片样式——ypos 偏移（可为负，向上溢出）。 */
function imageStyle(img: SegmentImage): Record<string, string> {
  return { marginTop: `${img.ypos}px` };
}

/** 掩膜内矩形样式——4 参 rect 绝对 x 按流位置 0（已知偏差）；y 透传。 */
function rectStyle(shape: NonNullable<PrintSegment['shape']>): Record<string, string> {
  return {
    display: 'block',
    position: 'relative',
    top: `${shape.y}px`,
    left: '0',
    width: `${shape.width}px`,
    height: `${shape.height}px`,
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
      <img
        class="term-img"
        :src="resolveResource(hoveredIdx === segIdx && seg.image!.srcb ? seg.image!.srcb : seg.image!.src)"
        :width="seg.image.width"
        :height="seg.image.height"
        :style="imageStyle(seg.image)"
        draggable="false"
      />
    </span>
    <span v-else-if="seg.shape" class="term-seg term-mask" :style="maskStyle(seg.shape)">
      <span class="term-rect" :style="rectStyle(seg.shape)" />
    </span>
    <span v-else class="term-seg" :style="segmentStyle(seg)">{{ seg.text }}</span>
  </template>
</template>

<script lang="ts">
import type { PrintSegment as PS } from '../types/protocol';

/** 文本段样式（保持与 TerminalDisplay.segmentStyle 同款——color/bold/italic 映射）。 */
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
.term-rect {
  display: block;
}
</style>
