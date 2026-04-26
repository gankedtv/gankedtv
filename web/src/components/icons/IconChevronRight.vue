<script setup lang="ts">
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
    <polyline points="9 18 15 12 9 6" />
  </svg>
</template>
