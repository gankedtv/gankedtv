<script setup lang="ts">
// Display-only tag chip — clicking navigates to /tag/:slug. Modeled on GameTag.vue
// pill variant but renders as a RouterLink so card clicks don't trigger clip
// navigation (we stopPropagation the link click in the parent template).
//
// Use `interactive=false` for non-linking variants (e.g. the "+N" overflow chip
// or read-only previews) to render a plain <span> with identical typography.

withDefaults(
  defineProps<{
    slug: string
    name: string
    size?: 'sm' | 'md'
    interactive?: boolean
  }>(),
  { interactive: true },
)

// Enter fires the link's native synthetic click; Space we preventDefault so it
// doesn't scroll the page on top of the navigation. Both stop so the parent
// card's @keydown doesn't also route to the clip detail. One handler — two
// @keydown bindings would compile to duplicate object keys (TS1117).
function onLinkKeydown(e: KeyboardEvent) {
  if (e.key !== 'Enter' && e.key !== ' ') return
  if (e.key === ' ') e.preventDefault()
  e.stopPropagation()
}
</script>

<template>
  <RouterLink
    v-if="interactive"
    :to="{ name: 'tag-detail', params: { slug } }"
    :aria-label="`Browse #${slug}`"
    class="border border-border bg-transparent font-mono font-medium uppercase tracking-[0.08em] text-text-secondary outline-none transition-colors duration-150 hover:border-ink hover:text-ink focus-visible:border-ink focus-visible:text-ink"
    :class="
      size === 'md' ? 'px-2.5 py-1 text-[10px] tracking-[0.08em]' : 'px-1.5 py-0.5 text-[10px]'
    "
    @click.stop
    @keydown="onLinkKeydown"
  >
    #{{ name }}
  </RouterLink>
  <span
    v-else
    class="border border-border bg-transparent font-mono font-medium uppercase tracking-[0.08em] text-text-muted"
    :class="
      size === 'md' ? 'px-2.5 py-1 text-[10px] tracking-[0.08em]' : 'px-1.5 py-0.5 text-[10px]'
    "
  >
    {{ name }}
  </span>
</template>
