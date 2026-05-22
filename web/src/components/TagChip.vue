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
</script>

<template>
  <RouterLink
    v-if="interactive"
    :to="{ name: 'tag-detail', params: { slug } }"
    :aria-label="`Browse #${slug}`"
    class="rounded-[3px] border border-border-strong bg-surface-base font-mono font-medium uppercase tracking-[0.06em] text-text-primary outline-none transition-colors duration-150 hover:border-brand-light hover:text-text-primary focus-visible:ring-2 focus-visible:ring-brand"
    :class="
      size === 'md' ? 'px-2.5 py-1 text-[10px] tracking-[0.08em]' : 'px-1.5 py-0.5 text-[10px]'
    "
    @click.stop
    @keydown.enter.stop
    @keydown.space.prevent.stop
  >
    #{{ name }}
  </RouterLink>
  <span
    v-else
    class="rounded-[3px] border border-border-strong bg-surface-base font-mono font-medium uppercase tracking-[0.06em] text-text-muted"
    :class="
      size === 'md' ? 'px-2.5 py-1 text-[10px] tracking-[0.08em]' : 'px-1.5 py-0.5 text-[10px]'
    "
  >
    {{ name }}
  </span>
</template>
