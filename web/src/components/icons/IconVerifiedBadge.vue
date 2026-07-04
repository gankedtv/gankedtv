<script setup lang="ts">
withDefaults(defineProps<{ size?: number }>(), { size: 14 })
</script>

<template>
  <svg
    :width="size"
    :height="size"
    viewBox="0 0 24 24"
    fill="none"
    aria-hidden="true"
    class="text-white"
  >
    <circle cx="12" cy="12" r="10" class="fill-ink" />
    <path
      d="M9 12l2 2 4-4"
      stroke="currentColor"
      stroke-width="2"
      stroke-linecap="round"
      stroke-linejoin="round"
    />
  </svg>
</template>
