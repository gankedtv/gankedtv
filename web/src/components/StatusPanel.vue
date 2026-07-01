<script setup lang="ts">
// Loading / empty / error placeholder shared across views.
//
// kind='loading' → centered tick bar + quiet line, no card chrome.
// kind='empty' / 'error' → raised card with kicker + message.
//   Default slot is reserved for action buttons (Retry, Upload a clip, etc.).
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
    <span class="block h-1.5 w-5.5 overflow-hidden rounded-full bg-surface-high">
      <span
        class="block h-full w-full origin-left bg-accent animate-[tick_1.6s_ease-in-out_infinite]"
      ></span>
    </span>
    <span class="text-sm text-text-muted">{{ message }}</span>
  </div>
  <div
    v-else
    class="mt-10 flex flex-col items-center justify-center gap-3 rounded-lg border border-border bg-surface-raised py-16 text-center"
    :role="kind === 'error' ? 'alert' : undefined"
  >
    <span
      class="text-[10px] font-bold uppercase tracking-[0.14em]"
      :class="kind === 'error' ? 'text-accent' : 'text-text-muted'"
    >
      {{ kind === 'error' ? 'Something went wrong' : 'Nothing here yet' }}
    </span>
    <span class="text-sm text-text-secondary">{{ message }}</span>
    <slot />
  </div>
</template>
