<script setup lang="ts">
// Stacked-rectangles glyph evocative of a vertical-snap reel of cards.
withDefaults(defineProps<{ size?: number; strokeWidth?: number }>(), {
  size: 16,
  strokeWidth: 2,
})
</script>

<template>
  <svg
    :width="size"
    :height="size"
    viewBox="0 0 24 24"
    fill="none"
    stroke="currentColor"
    :stroke-width="strokeWidth"
    stroke-linecap="round"
    stroke-linejoin="round"
    aria-hidden="true"
  >
    <rect x="6" y="3" width="12" height="8" rx="1.5" />
    <rect x="6" y="13" width="12" height="8" rx="1.5" />
    <line x1="10" y1="7" x2="14" y2="7" />
    <line x1="10" y1="17" x2="14" y2="17" />
  </svg>
</template>
