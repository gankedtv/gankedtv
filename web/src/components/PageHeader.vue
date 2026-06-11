<script setup lang="ts">
// Newsprint page-level header: mono kicker row → oversized condensed title →
// optional follow-on content. Shared by the editorial views (Games, Trending,
// Search, Tag, Notifications, Upload, Settings, Admin…). Captions interpolate
// runtime values (clip counts, etc.) so they ship as a named slot; the default
// slot covers arbitrary follow-on content (blurb, tabs, control groups).
// `live` renders the pulsing signal dot — an earned moment, use sparingly.

defineProps<{
  title: string
  live?: boolean
}>()
</script>

<template>
  <header>
    <div
      class="mb-2 flex items-center gap-2 font-mono text-[10px] uppercase leading-none tracking-[0.22em] text-text-secondary"
    >
      <span
        v-if="live"
        class="size-1.75 shrink-0 rounded-full bg-signal animate-[pulse_2s_infinite]"
      />
      <slot name="caption" />
    </div>
    <h1
      class="m-0 font-heading text-[clamp(36px,4.5vw,52px)] font-bold leading-none uppercase tracking-[0.01em] text-text-primary"
    >
      {{ title }}
    </h1>
    <slot />
  </header>
</template>
