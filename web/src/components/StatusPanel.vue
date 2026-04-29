<script setup lang="ts">
// Loading / empty / error placeholder shared across views.
//
// kind='loading' → centered "Loading…"-style line, no border.
// kind='empty' / 'error' → bordered surface-raised card. Default slot is
//   reserved for action buttons (Retry, Upload a clip, etc.).
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
    class="mt-10 flex items-center justify-center py-16"
    role="status"
    aria-live="polite"
  >
    <span class="font-mono text-sm uppercase tracking-widest text-text-muted">{{ message }}</span>
  </div>
  <div
    v-else
    class="mt-10 flex flex-col items-center justify-center gap-2 rounded-md border border-border bg-surface-raised py-16 text-center"
    :role="kind === 'error' ? 'alert' : undefined"
  >
    <span class="font-mono text-sm uppercase tracking-widest text-text-muted">{{ message }}</span>
    <slot />
  </div>
</template>
