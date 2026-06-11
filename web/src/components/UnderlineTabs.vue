<script setup lang="ts" generic="K extends string">
import type { RouteLocationRaw } from 'vue-router'

// Underline-style tab control used across the app (HomeView Latest/Following,
// UserView Clips/Liked, UserFollowListView Followers/Following). A tab is either
// a RouterLink (when `to` is set) or a <button> that emits `select`. The two
// modes coexist so callers can express "navigate to a new route" vs "swap local
// state" without each caller re-implementing the styling.
defineProps<{
  tabs: Array<{ key: K; label: string; to?: RouteLocationRaw }>
  active: K
}>()

defineEmits<{
  select: [key: K]
}>()
</script>

<template>
  <div class="flex items-center gap-7 border-b border-border">
    <template v-for="t in tabs" :key="t.key">
      <RouterLink
        v-if="t.to"
        :to="t.to"
        :class="[
          'relative -mb-px cursor-pointer whitespace-nowrap border-b-2 bg-transparent pb-3 font-mono text-[11px] uppercase tracking-[0.15em] no-underline transition-colors duration-150 hover:text-ink',
          t.key === active
            ? 'border-ink text-text-primary'
            : 'border-transparent text-text-secondary',
        ]"
      >
        {{ t.label }}
      </RouterLink>
      <button
        v-else
        :class="[
          'relative -mb-px cursor-pointer whitespace-nowrap border-b-2 bg-transparent pb-3 font-mono text-[11px] uppercase tracking-[0.15em] transition-colors duration-150 hover:text-ink',
          t.key === active
            ? 'border-ink text-text-primary'
            : 'border-transparent text-text-secondary',
        ]"
        @click="$emit('select', t.key)"
      >
        {{ t.label }}
      </button>
    </template>
  </div>
</template>
