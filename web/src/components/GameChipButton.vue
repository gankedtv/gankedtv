<script setup lang="ts">
// Clickable game chip — used in the upload picker today, will fit any future
// "filter by game" affordance (sidebar, search facets, etc.). Neutral pill that
// shifts to the mint accent on hover because it's interactive.

defineProps<{
  tag: string
}>()
defineEmits<{ click: [] }>()
</script>

<template>
  <button
    type="button"
    class="cursor-pointer rounded-full border border-border bg-transparent px-2.5 py-1 text-[11px] font-semibold uppercase tracking-[0.06em] text-text-muted transition-colors duration-150 hover:border-accent-border hover:text-accent"
    @click="$emit('click')"
  >
    {{ tag }}
  </button>
</template>
