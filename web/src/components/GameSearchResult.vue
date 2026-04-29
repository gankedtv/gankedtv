<script setup lang="ts">
// Single row inside a games dropdown / search result list: small accent tag
// next to the full game name. Renders as `role="option"` so it composes inside
// any `<ul role="listbox">`. The `selected` prop drives `aria-selected` for
// assistive tech and is also consumed by the visual highlight.
//
// Click is exposed via @select so the consumer wires keyboard nav semantics
// (mousedown.prevent here keeps the parent input from blurring before the
// click handler fires).

withDefaults(
  defineProps<{
    tag: string
    name: string
    selected?: boolean
  }>(),
  { selected: false },
)
defineEmits<{ select: [] }>()
</script>

<template>
  <li
    role="option"
    :aria-selected="selected"
    class="flex cursor-pointer items-center gap-3 px-3.5 py-2.5 transition-colors duration-150"
    :class="selected ? 'bg-surface-overlay' : 'hover:bg-surface-overlay'"
    @mousedown.prevent="$emit('select')"
  >
    <span class="font-mono text-[10px] uppercase tracking-[0.06em] text-neon">
      {{ tag }}
    </span>
    <span class="font-body text-sm text-text-primary">{{ name }}</span>
  </li>
</template>
