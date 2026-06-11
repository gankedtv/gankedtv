<script setup lang="ts">
// The Broadcast-voice container: inset ink border, four corner brackets, and
// a mono topbar (channel | status | spec). Wraps watch surfaces only — the
// clip player, reels viewer, upload transcode preview. Brackets earn their
// meaning by appearing nowhere else.
defineProps<{
  channel: string
  status?: string
  spec?: string
  live?: boolean
}>()

// Each corner bracket is an L-shape: one horizontal + one vertical stroke.
const brackets = [
  ['top-0 left-0', 'top-0 left-0'],
  ['top-0 right-0', 'top-0 right-0'],
  ['bottom-0 left-0', 'bottom-0 left-0'],
  ['bottom-0 right-0', 'bottom-0 right-0'],
] as const
</script>

<template>
  <div class="relative p-3.5">
    <div class="pointer-events-none absolute inset-0 border border-ink/35" aria-hidden="true" />
    <template v-for="([h, v], i) in brackets" :key="i">
      <span :class="['pointer-events-none absolute h-1 w-3.5 bg-ink', h]" aria-hidden="true" />
      <span :class="['pointer-events-none absolute h-3.5 w-1 bg-ink', v]" aria-hidden="true" />
    </template>

    <div
      class="flex items-center justify-between gap-4 whitespace-nowrap px-1.5 pb-3 pt-1 font-mono text-[10px] uppercase tracking-[0.15em] text-text-secondary"
    >
      <span class="truncate">{{ channel }}</span>
      <span v-if="status" class="flex items-center gap-1.5 text-text-primary">
        <span
          v-if="live"
          class="size-1.75 rounded-full bg-signal animate-[pulse_2s_infinite]"
          aria-hidden="true"
        />
        {{ status }}
      </span>
      <span v-if="spec" class="max-tablet:hidden">{{ spec }}</span>
    </div>

    <slot />
  </div>
</template>
