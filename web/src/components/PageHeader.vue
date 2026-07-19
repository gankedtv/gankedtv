<script setup lang="ts">
// Arena page-level header: small caption row → condensed display title →
// optional follow-on content. Shared by Games, Trending, Search, Tag,
// Notifications, Upload, Settings, Admin… Captions interpolate runtime values
// (clip counts, etc.) so they ship as a named slot; the default slot covers
// arbitrary follow-on content (blurb, tabs, control groups).
// `live` renders the pulsing mint dot — an earned moment, use sparingly.

defineProps<{
  title: string
  live?: boolean
}>()
</script>

<template>
  <header>
    <div
      v-if="live || $slots.caption"
      class="mb-2 flex items-center gap-2 text-[10px] font-bold uppercase leading-none tracking-[0.14em] text-text-secondary"
    >
      <span
        v-if="live"
        class="size-1.75 shrink-0 rounded-full bg-accent animate-[pulse_2s_infinite]"
      />
      <slot name="caption" />
    </div>
    <h1
      class="m-0 font-condensed text-[clamp(30px,3.6vw,42px)] font-black leading-none uppercase tracking-[0.02em] text-text-primary"
    >
      {{ title }}
    </h1>
    <slot />
  </header>
</template>
