<script setup lang="ts">
// Clickable game chip — used in the upload picker today, will fit any future
// "filter by game" affordance (sidebar, search facets, etc.). Visually it's a
// `<button>` with the same uppercase-mono treatment as `GameTag`, but with
// hover and pressed states because it's interactive.

defineProps<{
  tag: string
}>()
defineEmits<{ click: [] }>()
</script>

<template>
  <button
    type="button"
    class="cursor-pointer border border-border bg-transparent px-2.5 py-1 font-mono text-[10px] uppercase tracking-[0.08em] text-text-muted transition-colors duration-150 hover:border-ink hover:text-ink"
    @click="$emit('click')"
  >
    {{ tag }}
  </button>
</template>
