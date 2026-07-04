<script setup lang="ts">
import { useId } from 'vue'

withDefaults(defineProps<{ size?: number; glow?: boolean }>(), {
  size: 22,
  glow: false,
})

// Unique gradient ids per instance — duplicated SVG defs ids resolve to the
// first instance in the document, which breaks when that one is unmounted.
const uid = useId()
</script>

<template>
  <svg
    viewBox="0 0 120 120"
    :width="size"
    :height="size"
    class="shrink-0"
    :style="glow ? { filter: 'drop-shadow(0 0 4px rgba(0,229,160,0.45))' } : undefined"
    aria-hidden="true"
  >
    <defs>
      <linearGradient :id="`${uid}-frame`" x1="0" y1="0" x2="1" y2="1">
        <stop offset="0" stop-color="#7dffd8" />
        <stop offset="0.5" stop-color="#00e5a0" />
        <stop offset="1" stop-color="#00a376" />
      </linearGradient>
      <radialGradient :id="`${uid}-screen`" cx="0.5" cy="0.4" r="0.85">
        <stop offset="0" stop-color="#13211a" />
        <stop offset="1" stop-color="#060b09" />
      </radialGradient>
      <linearGradient :id="`${uid}-play`" x1="0" y1="0" x2="0.3" y2="1">
        <stop offset="0" stop-color="#b6ffe6" />
        <stop offset="1" stop-color="#00e5a0" />
      </linearGradient>
      <linearGradient :id="`${uid}-gloss`" x1="0" y1="0" x2="0" y2="1">
        <stop offset="0" stop-color="rgba(255,255,255,0.45)" />
        <stop offset="0.5" stop-color="rgba(255,255,255,0)" />
      </linearGradient>
    </defs>
    <path
      d="M30 0 L100 0 A20 20 0 0 1 120 20 L120 90 L90 120 L20 120 A20 20 0 0 1 0 100 L0 30 Z"
      :fill="`url(#${uid}-frame)`"
    />
    <path
      d="M30 0 L100 0 A20 20 0 0 1 120 20 L120 90 L90 120 L20 120 A20 20 0 0 1 0 100 L0 30 Z"
      :fill="`url(#${uid}-gloss)`"
      opacity="0.5"
    />
    <path
      d="M36 14 L92 14 A14 14 0 0 1 106 28 L106 84 L84 106 L28 106 A14 14 0 0 1 14 92 L14 36 Z"
      :fill="`url(#${uid}-screen)`"
    />
    <path
      d="M37.5 15.5 L91 15.5 A12.5 12.5 0 0 1 104.5 29 L104.5 83.5 L83.5 104.5 L29 104.5 A12.5 12.5 0 0 1 15.5 91 L15.5 37.5 Z"
      fill="none"
      stroke="rgba(0,229,160,0.4)"
      stroke-width="1.4"
    />
    <path d="M49 38 L80 60 L49 82 Z" :fill="`url(#${uid}-play)`" stroke-linejoin="round" />
  </svg>
</template>
