<script setup lang="ts">
// The "live feed dot · caption / big H1 / optional follow-on" header pattern
// shared by HomeView, GamesView, TrendingView, and UploadView. The H1 is the
// only fully prop-driven slot — captions interpolate runtime values (clip
// counts, etc.) so they ship as a named slot, and the default slot covers
// arbitrary follow-on content (description paragraph, control groups).

defineProps<{
  title: string
  pulse?: boolean
}>()
</script>

<template>
  <div>
    <div
      class="mb-2 flex items-center gap-2 font-mono text-[11px] uppercase tracking-widest text-text-muted"
    >
      <span
        v-if="pulse"
        class="h-1.5 w-1.5 shrink-0 rounded-full bg-neon shadow-[0_0_8px_var(--color-neon)] animate-[pulse_2s_infinite]"
      />
      <slot name="caption" />
    </div>
    <h1
      class="m-0 font-heading text-[clamp(32px,4vw,52px)] font-bold leading-none uppercase tracking-[0.02em] text-text-primary"
    >
      {{ title }}
    </h1>
    <slot />
  </div>
</template>
