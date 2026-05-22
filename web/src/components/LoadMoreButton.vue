<script setup lang="ts">
// Cursor-paginated "Load more" control. Shows a Loading… / Retry / Load more
// button plus an inline error message when pagination fails. The parent owns
// the loading + errored refs and the fetcher; this component is presentational
// only. Outer spacing comes from the parent via class inheritance — Vue passes
// it through to the root <div> automatically.
defineProps<{
  loading: boolean
  errored: boolean
}>()

defineEmits<{
  load: []
}>()
</script>

<template>
  <div class="flex flex-col items-center gap-2">
    <span v-if="errored" class="font-mono text-[11px] uppercase tracking-widest text-text-muted">
      Couldn't load more — try again.
    </span>
    <button
      :disabled="loading"
      @click="$emit('load')"
      class="cursor-pointer rounded-sm border border-border bg-surface-raised px-6 py-2.5 font-mono text-[11px] uppercase tracking-[0.08em] text-text-primary transition-colors duration-150 hover:border-brand-light disabled:opacity-50"
    >
      {{ loading ? 'Loading…' : errored ? 'Retry' : 'Load more' }}
    </button>
  </div>
</template>
