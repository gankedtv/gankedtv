<script setup lang="ts">
// Renders the game's short tag (e.g. VALORANT, RL).
//
// `variant`:
//   - 'pill' (default) — mint tag chip used on thumbnails and inline rows.
//   - 'square'          — 40×40 boxed tile, used in list rows where the tag stands
//     in for cover art (e.g. Trending's Hot Games sidebar).
//
// Arena badge grammar: one mint accent. 'subtle'/'muted' tones render hairline
// outlines in neutral text colors so rows can de-emphasize without a second hue.

defineProps<{
  tag: string
  variant?: 'pill' | 'square'
  size?: 'sm' | 'md'
  tone?: 'default' | 'subtle' | 'muted'
}>()
</script>

<template>
  <span
    v-if="variant === 'square'"
    class="inline-flex h-10 w-10 shrink-0 items-center justify-center rounded-md border border-border-strong bg-surface-base text-[10px] font-bold uppercase tracking-[0.06em]"
    :class="tone === 'muted' ? 'text-text-muted' : 'text-text-secondary'"
  >
    {{ tag }}
  </span>
  <span
    v-else
    class="inline-flex items-center rounded-sm border font-bold uppercase leading-none"
    :class="[
      size === 'md' ? 'px-2 py-1 text-[10px] tracking-[0.07em]' : 'px-1.5 py-1 text-[9px] tracking-[0.07em]',
      tone === 'subtle'
        ? 'border-border bg-transparent text-text-secondary'
        : tone === 'muted'
          ? 'border-border bg-transparent text-text-muted'
          : 'border-accent-border bg-accent-bg text-accent',
    ]"
  >
    {{ tag }}
  </span>
</template>
