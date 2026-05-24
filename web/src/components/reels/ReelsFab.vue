<script setup lang="ts">
import IconReels from '@/components/icons/IconReels.vue'
</script>

<template>
  <RouterLink
    :to="{ name: 'reels' }"
    aria-label="Open reels feed"
    title="Reels"
    class="fixed right-6 z-40 inline-flex h-14 w-14 items-center justify-center rounded-full bg-brand text-white no-underline shadow-[0_8px_24px_var(--color-brand-glow)] transition-colors duration-150 hover:bg-brand-light"
    style="bottom: calc(env(safe-area-inset-bottom, 0px) + 24px)"
  >
    <IconReels :size="22" :stroke-width="2.2" />
  </RouterLink>
</template>
