<script setup lang="ts">
withDefaults(defineProps<{ size?: number }>(), { size: 16 })
</script>

<template>
  <svg :width="size" :height="size" viewBox="0 0 24 24" fill="currentColor" aria-hidden="true">
    <path
      d="M18 16a3 3 0 00-2.4 1.2L8.7 13.1c.05-.34.08-.69.08-1.04 0-.36-.03-.71-.08-1.05l6.9-4.07A3 3 0 1014.4 4.5l-7.04 4.15A3 3 0 103 12a3 3 0 001.36-.33l7.04 4.15A3 3 0 1018 19a3 3 0 000-3z"
    />
  </svg>
</template>
