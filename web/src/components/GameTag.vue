<script setup lang="ts">
// Renders the game's short tag (e.g. VALORANT, RL).
//
// `variant`:
//   - 'pill' (default) — bordered horizontal chip used inline (cards, leaderboard).
//     Pair with `size='sm'|'md'` for density vs. presence.
//   - 'square'          — 40×40 boxed tile, used in list rows where the tag stands
//     in for cover art (e.g. Trending's Hot Games sidebar).
//
// `tone` covers the three text-color variants the design uses today:
//   - default → primary
//   - subtle  → secondary
//   - muted   → text-muted

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
    class="inline-flex h-10 w-10 shrink-0 items-center justify-center rounded-[4px] border border-border-strong bg-surface-base font-mono text-[10px] font-medium uppercase tracking-[0.06em]"
    :class="
      tone === 'subtle'
        ? 'text-text-secondary'
        : tone === 'muted'
          ? 'text-text-muted'
          : 'text-text-primary'
    "
  >
    {{ tag }}
  </span>
  <span
    v-else
    class="rounded-[3px] border border-border-strong bg-surface-base font-mono font-medium uppercase tracking-[0.06em]"
    :class="[
      size === 'md' ? 'px-2.5 py-1 text-[10px] tracking-[0.08em]' : 'px-1.5 py-0.5 text-[10px]',
      tone === 'subtle'
        ? 'text-text-secondary'
        : tone === 'muted'
          ? 'text-text-muted'
          : 'text-text-primary',
    ]"
  >
    {{ tag }}
  </span>
</template>
