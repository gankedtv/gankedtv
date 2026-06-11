<script setup lang="ts">
import IconReels from '@/components/icons/IconReels.vue'
</script>

<template>
  <!-- Desktop-only: below lg the bottom tab bar carries the Reels destination
       (and would overlap a fixed corner button). -->
  <RouterLink
    :to="{ name: 'reels' }"
    aria-label="Open reels feed"
    title="Reels"
    class="fixed right-6 bottom-6 z-50 hidden size-12 items-center justify-center bg-ink text-signal-text no-underline transition-[filter] duration-150 hover:brightness-108 lg:inline-flex"
  >
    <IconReels :size="22" :stroke-width="2.2" />
  </RouterLink>
</template>
