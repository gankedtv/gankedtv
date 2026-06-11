<script setup lang="ts">
// Loading / empty / error placeholder shared across views.
//
// kind='loading' → centered ticker bar + mono caps line, no border.
// kind='empty' / 'error' → hairline-banded section (border-y, no card chrome).
//   Default slot is reserved for action buttons (Retry, Upload a clip, etc.).
//   Error kicker uses --color-signal — an earned moment.
//
// `message` is required so the caller commits to copy that fits its context;
// keeping it a prop (vs slot) avoids mismatched typography across views.

defineProps<{
  kind: 'loading' | 'empty' | 'error'
  message: string
}>()
</script>

<template>
  <div
    v-if="kind === 'loading'"
    class="mt-10 flex items-center justify-center gap-3 py-16"
    role="status"
    aria-live="polite"
  >
    <span class="block h-1.5 w-5.5 overflow-hidden bg-surface-raised">
      <span class="block h-full w-full origin-left bg-ink animate-[tick_1.6s_ease-in-out_infinite]"></span>
    </span>
    <span class="font-mono text-sm uppercase tracking-widest text-text-muted">{{ message }}</span>
  </div>
  <div
    v-else
    class="mt-10 flex flex-col items-center justify-center gap-3 border-y border-border py-16 text-center"
    :role="kind === 'error' ? 'alert' : undefined"
  >
    <span
      class="font-mono text-[10px] uppercase tracking-[0.22em]"
      :class="kind === 'error' ? 'text-signal' : 'text-text-muted'"
    >
      {{ kind === 'error' ? 'Transmission error' : 'Nothing filed' }}
    </span>
    <span class="font-mono text-sm uppercase tracking-widest text-text-secondary">{{ message }}</span>
    <slot />
  </div>
</template>
