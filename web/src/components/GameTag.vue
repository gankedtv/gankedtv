<script setup lang="ts">
// Renders the game's short tag (e.g. VALORANT, RL).
//
// `variant`:
//   - 'pill' (default) — horizontal chip used inline (cards, leaderboard).
//     Pair with `size='sm'|'md'` for density vs. presence.
//   - 'square'          — 40×40 boxed tile, used in list rows where the tag stands
//     in for cover art (e.g. Trending's Hot Games sidebar).
//
// Newsprint badge grammar: the default tone is the solid-ink primary badge
// (game tag on thumbnails); 'subtle'/'muted' tones render the hairline outline
// secondary style so the two styles differentiate without a third color.

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
    class="inline-flex h-10 w-10 shrink-0 items-center justify-center border border-border-strong bg-surface-base font-mono text-[10px] font-bold uppercase tracking-[0.06em]"
    :class="tone === 'muted' ? 'text-text-muted' : 'text-text-secondary'"
  >
    {{ tag }}
  </span>
  <span
    v-else
    class="inline-flex items-center font-mono font-bold uppercase leading-none"
    :class="[
      size === 'md'
        ? 'px-2 py-1.25 text-[10px] tracking-[0.15em]'
        : 'px-1.5 py-1 text-[10px] tracking-[0.12em]',
      tone === 'subtle'
        ? 'border border-border text-text-secondary'
        : tone === 'muted'
          ? 'border border-border text-text-muted'
          : 'bg-ink text-signal-text',
    ]"
  >
    {{ tag }}
  </span>
</template>
