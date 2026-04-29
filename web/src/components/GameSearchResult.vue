<script setup lang="ts">
// Single row inside a games dropdown / search result list: small accent tag
// next to the full game name. Ships as an `<li>` so it composes inside any
// `<ul role="listbox">`. Click is exposed via @click so the consumer wires
// keyboard nav / mousedown.prevent semantics as needed.

defineProps<{
  tag: string
  name: string
}>()
defineEmits<{ select: [] }>()
</script>

<template>
  <li
    class="flex cursor-pointer items-center gap-3 px-3.5 py-2.5 transition-colors duration-150 hover:bg-surface-overlay"
    @mousedown.prevent="$emit('select')"
  >
    <span class="font-mono text-[10px] uppercase tracking-[0.06em] text-neon">
      {{ tag }}
    </span>
    <span class="font-body text-sm text-text-primary">{{ name }}</span>
  </li>
</template>
