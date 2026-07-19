<script setup lang="ts">
import { useId } from 'vue'

// Rewynd's mint HUD-frame rewind mark (◄◄) — sibling product's brand art, the
// counterpart to GankedTV's play mark in LogoMark.vue. The internal SVG gradients
// are a sanctioned brand-art exception to the "no gradients" rule (DESIGN.md §2/§9),
// same as LogoMark.
withDefaults(defineProps<{ size?: number }>(), { size: 20 })

// Unique gradient ids per instance — duplicated SVG defs ids resolve to the
// first instance in the document, which breaks when that one is unmounted.
const uid = useId()
</script>

<template>
  <svg viewBox="0 0 120 120" :width="size" :height="size" class="shrink-0" aria-hidden="true">
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
      <linearGradient :id="`${uid}-marks`" x1="0" y1="0" x2="0.3" y2="1">
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
      d="M37.5 18 L90 18 A12 12 0 0 1 102 30 L102 82.5 L82.5 102 L30 102 A12 12 0 0 1 18 90 L18 37.5 Z"
      :fill="`url(#${uid}-screen)`"
    />
    <path d="M56 42 L34 60 L56 78 Z M78 42 L56 60 L78 78 Z" :fill="`url(#${uid}-marks)`" />
  </svg>
</template>
